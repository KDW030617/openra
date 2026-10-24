#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>
	/// Loopback TCP server that accepts newline-delimited JSON requests from an external agent.
	/// Requests are handed to the game thread through <see cref="TryDequeue"/>, so no game state
	/// is ever touched from a network thread.
	/// </summary>
	public sealed class LlmBridgeServer : IDisposable
	{
		public sealed class Request
		{
			public readonly string Method;
			public readonly JsonObject Params;
			readonly DateTime deadline;
			readonly TaskCompletionSource<JsonNode> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

			public Request(string method, JsonObject parameters, TimeSpan timeout)
			{
				Method = method;
				Params = parameters ?? [];
				deadline = DateTime.UtcNow + timeout;
			}

			public Task<JsonNode> Result => completion.Task;
			public bool CanExecute => !Result.IsCompleted && DateTime.UtcNow < deadline;
			public void Expire() { completion.TrySetCanceled(); }
			public void Complete(JsonNode result) { completion.TrySetResult(result); }
			public void Fail(string error) { completion.TrySetException(new InvalidOperationException(error)); }
		}

		readonly TcpListener listener;
		readonly CancellationTokenSource cancellation = new();
		readonly ConcurrentQueue<Request> pending = new();
		readonly TimeSpan requestTimeout;

		public LlmBridgeServer(int port, TimeSpan requestTimeout)
		{
			this.requestTimeout = requestTimeout;
			listener = new TcpListener(IPAddress.Loopback, port);
		}

		public void Start()
		{
			listener.Start();
			Task.Run(AcceptLoop);
		}

		public bool TryDequeue(out Request request)
		{
			while (pending.TryDequeue(out request))
				if (request.CanExecute)
					return true;

			request = null;
			return false;
		}

		async Task AcceptLoop()
		{
			while (!cancellation.IsCancellationRequested)
			{
				try
				{
					var client = await listener.AcceptTcpClientAsync(cancellation.Token);
					_ = Task.Run(() => HandleClient(client));
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception e)
				{
					if (cancellation.IsCancellationRequested)
						return;

					Log.Write("debug", $"LLM bridge accept failed: {e.Message}");
				}
			}
		}

		async Task HandleClient(TcpClient client)
		{
			using (client)
			{
				var stream = client.GetStream();
				using var reader = new StreamReader(stream, Encoding.UTF8);
				await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

				try
				{
					while (!cancellation.IsCancellationRequested)
					{
						var line = await reader.ReadLineAsync(cancellation.Token);
						if (line == null)
							return;

						if (string.IsNullOrWhiteSpace(line))
							continue;

						var response = await Process(line);
						await writer.WriteLineAsync(response.ToJsonString());
					}
				}
				catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
				{
					// Client disconnected or the server is shutting down.
				}
			}
		}

		async Task<JsonObject> Process(string line)
		{
			JsonNode id = null;
			try
			{
				var message = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidOperationException("Request must be a JSON object.");
				id = message["id"]?.DeepClone();
				var method = message["method"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing 'method'.");

				var request = new Request(method, message["params"] as JsonObject, requestTimeout);
				pending.Enqueue(request);

				var finished = await Task.WhenAny(request.Result, Task.Delay(requestTimeout, cancellation.Token));
				if (finished != request.Result)
				{
					request.Expire();
					throw new TimeoutException("The game did not process the request in time (is the game paused?).");
				}

				var result = await request.Result;
				return new JsonObject { ["id"] = id, ["result"] = result?.DeepClone() };
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				return new JsonObject { ["id"] = id?.DeepClone(), ["error"] = e.InnerException?.Message ?? e.Message };
			}
		}

		public void Dispose()
		{
			cancellation.Cancel();
			listener.Stop();
			while (pending.TryDequeue(out var request))
				request.Fail("The game is shutting down.");
		}
	}
}
