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
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenRA.Mods.Common.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class LlmBridgeServerTest
	{
		static int FreePort()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		static async Task<JsonObject> Roundtrip(int port, string request)
		{
			using var client = new TcpClient();
			await client.ConnectAsync(IPAddress.Loopback, port);
			var stream = client.GetStream();
			using var reader = new StreamReader(stream);
			await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
			await writer.WriteLineAsync(request);
			return JsonNode.Parse(await reader.ReadLineAsync()).AsObject();
		}

		// Simulates the game thread draining requests on each tick.
		static void Pump(LlmBridgeServer server, Func<LlmBridgeServer.Request, JsonNode> handler, CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				while (server.TryDequeue(out var request))
				{
					if (request.Method == "fail")
						request.Fail("rejected");
					else
						request.Complete(handler(request));
				}

				Thread.Sleep(5);
			}
		}

		[TestCase(TestName = "Requests are answered with the game thread's result and the request id.")]
		public async Task ReturnsResult()
		{
			var port = FreePort();
			using var server = new LlmBridgeServer(port, TimeSpan.FromSeconds(5));
			using var cts = new CancellationTokenSource();
			server.Start();
			var pump = Task.Run(() => Pump(server, r => r.Params["value"]?.GetValue<int>() * 2, cts.Token));

			var response = await Roundtrip(port, "{\"id\":7,\"method\":\"double\",\"params\":{\"value\":21}}");
			await cts.CancelAsync();
			await pump;

			Assert.That(response["id"].GetValue<int>(), Is.EqualTo(7));
			Assert.That(response["result"].GetValue<int>(), Is.EqualTo(42));
		}

		[TestCase(TestName = "Failures from the game thread are reported as errors.")]
		public async Task ReturnsError()
		{
			var port = FreePort();
			using var server = new LlmBridgeServer(port, TimeSpan.FromSeconds(5));
			using var cts = new CancellationTokenSource();
			server.Start();
			var pump = Task.Run(() => Pump(server, _ => null, cts.Token));

			var response = await Roundtrip(port, "{\"id\":1,\"method\":\"fail\"}");
			await cts.CancelAsync();
			await pump;

			Assert.That(response["error"].GetValue<string>(), Is.EqualTo("rejected"));
		}

		[TestCase(TestName = "Requests time out when the game thread does not process them.")]
		public async Task TimesOut()
		{
			var port = FreePort();
			using var server = new LlmBridgeServer(port, TimeSpan.FromMilliseconds(200));
			server.Start();

			var response = await Roundtrip(port, "{\"id\":1,\"method\":\"get_state\"}");

			Assert.That(response["error"].GetValue<string>(), Does.Contain("in time"));
			Assert.That(server.TryDequeue(out _), Is.False, "Expired commands must not execute after resuming.");
		}
	}
}
