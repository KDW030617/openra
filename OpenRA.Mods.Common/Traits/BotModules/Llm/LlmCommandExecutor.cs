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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>Translates bridge requests into regular player orders. Bots can only act through orders.</summary>
	public sealed class LlmCommandExecutor
	{
		readonly Player player;
		readonly World world;
		readonly LlmBuildingPlacer placer;
		readonly string gameId = Guid.NewGuid().ToString("N");
		JsonObject gameInfo;

		public LlmCommandExecutor(Player player, LlmBuildingPlacer placer)
		{
			this.player = player;
			world = player.World;
			this.placer = placer;
		}

		public JsonNode Execute(IBot bot, string method, JsonObject p)
		{
			if (p["game_id"] != null && p["game_id"].GetValue<string>() != gameId)
				throw new InvalidOperationException("Game changed. Read a fresh state before issuing commands.");

			switch (method)
			{
				case "get_state":
					var state = LlmStateReporter.Snapshot(player, placer.BaseCenter());
					state["game_id"] = gameId;
					return state;
				case "get_game_info":
					gameInfo ??= new JsonObject
					{
						["game_id"] = gameId,
						["faction"] = player.Faction.InternalName,
						["map"] = LlmMapBriefing.Describe(player),
						["tech_tree"] = LlmTechTreeBriefing.Describe(player),
					};
					return gameInfo.DeepClone();
				case "build":
					return Build(bot, p);
				case "cancel_build":
					return CancelBuild(bot, p);
				case "move":
					return UnitOrder(bot, p, "Move", Target.FromCell(world, Cell(p)));
				case "attack_move":
					return UnitOrder(bot, p, "AttackMove", Target.FromCell(world, Cell(p)));
				case "attack":
					return UnitOrder(bot, p, "Attack", Target.FromActor(VisibleEnemy(p["target_id"]?.GetValue<uint>() ?? 0)));
				case "stop":
					return UnitOrder(bot, p, "Stop", null);
				case "deploy":
					return UnitOrder(bot, p, "DeployTransform", null);
				case "sell":
					return UnitOrder(bot, p, "Sell", null);
				default:
					throw new InvalidOperationException($"Unknown method '{method}'.");
			}
		}

		JsonNode Build(IBot bot, JsonObject p)
		{
			var item = p["item"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing 'item'.");
			var count = Math.Clamp(p["count"]?.GetValue<int>() ?? 1, 1, 10);
			var queue = AIUtils.FindQueuesByCategory(player).SelectMany(g => g)
				.Where(q => q.BuildableItems().Any(ai => ai.Name == item))
				.OrderBy(q => q.AllQueued().Count())
				.FirstOrDefault() ?? throw new InvalidOperationException($"'{item}' is not buildable right now (missing prerequisites or unknown name).");

			if (p["near_x"] != null && p["near_y"] != null)
				placer.SetHint(item, Cell(p, "near_x", "near_y"));

			bot.QueueOrder(Order.StartProduction(queue.Actor, item, count));
			return $"Queued {count} x {item} in {queue.Info.Type} queue.";
		}

		JsonNode CancelBuild(IBot bot, JsonObject p)
		{
			var item = p["item"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing 'item'.");
			var queue = AIUtils.FindQueuesByCategory(player).SelectMany(g => g)
				.FirstOrDefault(q => q.AllQueued().Any(i => i.Item == item))
				?? throw new InvalidOperationException($"'{item}' is not in any production queue.");

			placer.SetHint(item, null);
			bot.QueueOrder(Order.CancelProduction(queue.Actor, item, 1));
			return $"Cancelled one {item}.";
		}

		JsonNode UnitOrder(IBot bot, JsonObject p, string orderName, Target? target)
		{
			var ids = (p["unit_ids"] as JsonArray ?? throw new InvalidOperationException("Missing 'unit_ids'."))
				.Select(n => n.GetValue<uint>());

			var queued = p["queued"]?.GetValue<bool>() ?? false;
			var accepted = new List<uint>();
			var rejected = new List<uint>();
			foreach (var id in ids)
			{
				var actor = world.GetActorById(id);
				if (actor == null || actor.IsDead || !actor.IsInWorld || actor.Owner != player)
				{
					rejected.Add(id);
					continue;
				}

				var order = target.HasValue ? new Order(orderName, actor, target.Value, queued) : new Order(orderName, actor, queued);
				bot.QueueOrder(order);
				accepted.Add(id);
			}

			return new JsonObject
			{
				["order"] = orderName,
				["accepted"] = new JsonArray(accepted.Select(i => (JsonNode)i).ToArray()),
				["rejected_not_owned_or_dead"] = new JsonArray(rejected.Select(i => (JsonNode)i).ToArray()),
			};
		}

		Actor VisibleEnemy(uint id)
		{
			var actor = world.GetActorById(id);
			if (actor == null || actor.IsDead || !actor.IsInWorld || !actor.CanBeViewedByPlayer(player)
				|| actor.Owner.RelationshipWith(player) != PlayerRelationship.Enemy)
				throw new InvalidOperationException($"Actor {id} is not a visible enemy.");

			return actor;
		}

		CPos Cell(JsonObject p, string x = "x", string y = "y")
		{
			var cell = new CPos(
				p[x]?.GetValue<int>() ?? throw new InvalidOperationException($"Missing '{x}'."),
				p[y]?.GetValue<int>() ?? throw new InvalidOperationException($"Missing '{y}'."));

			if (!world.Map.Contains(cell))
				throw new InvalidOperationException($"Cell {cell} is outside the map.");

			return cell;
		}
	}
}
