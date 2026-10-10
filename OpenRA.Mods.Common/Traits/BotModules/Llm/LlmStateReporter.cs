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

using System.Linq;
using System.Text.Json.Nodes;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>Builds a compact, fog-respecting JSON snapshot of the game from one player's perspective.</summary>
	public static class LlmStateReporter
	{
		public static JsonObject Snapshot(Player player, CPos? baseCenter)
		{
			var world = player.World;
			var resources = player.PlayerActor.Trait<PlayerResources>();
			var power = player.PlayerActor.TraitOrDefault<PowerManager>();
			var bounds = world.Map.Bounds;

			var own = world.Actors.Where(a => a.Owner == player && a.IsInWorld && !a.IsDead).ToList();
			var enemies = world.Actors
				.Where(a => a.IsInWorld && !a.IsDead && a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy
					&& a.OccupiesSpace != null && a.CanBeViewedByPlayer(player))
				.ToList();

			return new JsonObject
			{
				["tick"] = world.WorldTick,
				["player"] = player.ResolvedPlayerName,
				["faction"] = player.Faction.InternalName,
				["win_state"] = player.WinState.ToString(),
				["cash"] = resources.GetCashAndResources(),
				["resource_storage"] = $"{resources.Resources}/{resources.ResourceCapacity}",
				["power"] = power == null ? null : new JsonObject
				{
					["provided"] = power.PowerProvided,
					["drained"] = power.PowerDrained,
				},
				["map"] = new JsonObject
				{
					["left"] = bounds.Left,
					["top"] = bounds.Top,
					["right"] = bounds.Right - 1,
					["bottom"] = bounds.Bottom - 1,
				},
				["base_center"] = baseCenter.HasValue ? Cell(baseCenter.Value) : null,
				["buildings"] = new JsonArray(own.Where(IsBuilding).Select(a => Describe(a, null)).ToArray()),
				["units"] = new JsonArray(own.Where(IsUnit).Select(a => Describe(a, null)).ToArray()),
				["visible_enemies"] = new JsonArray(enemies.Select(a => Describe(a, a.Owner)).ToArray()),
				["production"] = Production(player),
			};
		}

		public static bool IsBuilding(Actor a) { return a.Info.HasTraitInfo<BuildingInfo>(); }

		public static bool IsUnit(Actor a) { return !IsBuilding(a) && a.Info.HasTraitInfo<IMoveInfo>(); }

		static JsonNode Describe(Actor a, Player owner)
		{
			var health = a.TraitOrDefault<IHealth>();
			var node = new JsonObject
			{
				["id"] = a.ActorID,
				["type"] = a.Info.Name,
				["x"] = a.Location.X,
				["y"] = a.Location.Y,
				["hp"] = health == null ? null : health.HP * 100 / health.MaxHP,
			};

			if (owner != null)
			{
				node["owner"] = owner.ResolvedPlayerName;
				node["building"] = IsBuilding(a);
			}
			else if (IsUnit(a))
				node["idle"] = a.IsIdle;

			return node;
		}

		static JsonArray Production(Player player)
		{
			var result = new JsonArray();
			foreach (var category in AIUtils.FindQueuesByCategory(player))
			{
				var queue = category.First();
				var queued = queue.AllQueued().Select(i => (JsonNode)new JsonObject
				{
					["item"] = i.Item,
					["progress"] = i.TotalTime > 0 ? 100 - i.RemainingTime * 100 / i.TotalTime : 0,
					["done"] = i.Done,
				});

				var buildable = queue.BuildableItems().Select(ai => (JsonNode)new JsonObject
				{
					["name"] = ai.Name,
					["cost"] = queue.GetProductionCost(ai),
				});

				result.Add(new JsonObject
				{
					["queue"] = category.Key,
					["queued"] = new JsonArray(queued.ToArray()),
					["buildable"] = new JsonArray(buildable.ToArray()),
				});
			}

			return result;
		}

		static JsonArray Cell(CPos cell) { return new JsonArray(cell.X, cell.Y); }
	}
}
