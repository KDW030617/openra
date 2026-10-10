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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>
	/// Places finished structures for an LLM-controlled player, so the model only has to decide
	/// what to build and roughly where. Placement follows the same validity rules as human players.
	/// </summary>
	public sealed class LlmBuildingPlacer
	{
		const int PlacementRetryDelay = 25;

		readonly Player player;
		readonly World world;
		readonly int maxRadius;
		readonly Dictionary<string, CPos> hints = [];
		readonly Dictionary<ProductionQueue, int> lastAttempt = [];

		public LlmBuildingPlacer(Player player, int maxRadius)
		{
			this.player = player;
			world = player.World;
			this.maxRadius = maxRadius;
		}

		public void SetHint(string item, CPos? cell)
		{
			if (cell.HasValue)
				hints[item] = cell.Value;
			else
				hints.Remove(item);
		}

		public CPos? BaseCenter()
		{
			var own = world.Actors.Where(a => a.Owner == player && a.IsInWorld && !a.IsDead && LlmStateReporter.IsBuilding(a)).ToList();
			var provider = own.FirstOrDefault(a => a.Info.HasTraitInfo<BaseProviderInfo>()) ?? own.FirstOrDefault();
			return provider?.Location;
		}

		public void Tick(IBot bot)
		{
			foreach (var category in AIUtils.FindQueuesByCategory(player))
			{
				foreach (var queue in category)
				{
					var item = queue.AllQueued().FirstOrDefault();
					if (item == null || !item.Done)
						continue;

					var actorInfo = world.Map.Rules.Actors[item.Item];
					var bi = actorInfo.TraitInfoOrDefault<BuildingInfo>();
					if (bi == null || actorInfo.HasTraitInfo<PlugInfo>())
						continue;

					if (lastAttempt.TryGetValue(queue, out var tick) && world.WorldTick - tick < PlacementRetryDelay)
						continue;

					lastAttempt[queue] = world.WorldTick;
					var cell = FindLocation(actorInfo, bi, item.Item);
					if (cell == null)
						continue;

					hints.Remove(item.Item);
					bot.QueueOrder(new Order("PlaceBuilding", player.PlayerActor, Target.FromCell(world, cell.Value), false)
					{
						TargetString = item.Item,
						ExtraLocation = new CPos(0, 0),
						ExtraData = queue.Actor.ActorID,
						SuppressVisualFeedback = true
					});
				}
			}
		}

		CPos? FindLocation(ActorInfo actorInfo, BuildingInfo bi, string item)
		{
			var center = BaseCenter();
			if (center == null)
				return null;

			CPos? target = hints.TryGetValue(item, out var hint) ? hint : null;
			if (target == null && actorInfo.HasTraitInfo<RefineryInfo>())
				target = ClosestResource(center.Value);

			var cells = world.Map.FindTilesInAnnulus(center.Value, 2, maxRadius);
			if (target.HasValue)
				cells = cells.OrderBy(c => (c - target.Value).LengthSquared);

			foreach (var cell in cells)
				if (world.CanPlaceBuilding(cell, actorInfo, bi, null) && bi.IsCloseEnoughToBase(world, player, actorInfo, cell))
					return cell;

			return null;
		}

		CPos? ClosestResource(CPos center)
		{
			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (resourceLayer == null)
				return null;

			foreach (var cell in world.Map.FindTilesInAnnulus(center, 0, maxRadius))
				if (resourceLayer.GetResource(cell).Type != null)
					return cell;

			return null;
		}
	}
}
