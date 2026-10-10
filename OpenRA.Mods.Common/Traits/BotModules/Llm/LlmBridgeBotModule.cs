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
using System.Net.Sockets;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Exposes the bot player to an external agent (e.g. an LLM via an MCP server) over a loopback TCP JSON-lines bridge.",
		"The external agent reads the game state and issues high-level commands; finished structures are placed automatically.")]
	public class LlmBridgeBotModuleInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("Loopback TCP port the bridge listens on. Must be unique per LLM bot type.")]
		public readonly int Port = 0;

		[Desc("Maximum number of bridge requests to process per tick.")]
		public readonly int MaxRequestsPerTick = 8;

		[Desc("Seconds a request may wait for the game thread before it fails.")]
		public readonly int RequestTimeoutSeconds = 10;

		[Desc("Maximum distance (in cells) from the base center for automatic structure placement.")]
		public readonly int MaxBaseRadius = 20;

		public override object Create(ActorInitializer init) { return new LlmBridgeBotModule(this); }
	}

	public class LlmBridgeBotModule : ConditionalTrait<LlmBridgeBotModuleInfo>, IBotTick, INotifyActorDisposing
	{
		LlmBridgeServer server;
		LlmBuildingPlacer placer;
		LlmCommandExecutor executor;
		bool startFailed;

		public LlmBridgeBotModule(LlmBridgeBotModuleInfo info)
			: base(info) { }

		void IBotTick.BotTick(IBot bot)
		{
			if (server == null && !startFailed)
				StartServer(bot.Player);

			if (server == null)
				return;

			placer.Tick(bot);

			for (var i = 0; i < Info.MaxRequestsPerTick && server.TryDequeue(out var request); i++)
			{
				try
				{
					request.Complete(executor.Execute(bot, request.Method, request.Params));
				}
				catch (Exception e)
				{
					request.Fail(e.Message);
				}
			}
		}

		void StartServer(Player player)
		{
			placer = new LlmBuildingPlacer(player, Info.MaxBaseRadius);
			executor = new LlmCommandExecutor(player, placer);
			try
			{
				server = new LlmBridgeServer(Info.Port, TimeSpan.FromSeconds(Info.RequestTimeoutSeconds));
				server.Start();
				TextNotificationsManager.Debug($"LLM bridge for {player.ResolvedPlayerName} listening on 127.0.0.1:{Info.Port}");
			}
			catch (SocketException e)
			{
				startFailed = true;
				server = null;
				TextNotificationsManager.Debug($"LLM bridge for {player.ResolvedPlayerName} failed to listen on port {Info.Port}: {e.Message}");
			}
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			server?.Dispose();
			server = null;
		}
	}
}
