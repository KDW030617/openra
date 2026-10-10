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
using System.Text;
using System.Text.Json.Nodes;
using OpenRA.Mods.Common.Pathfinder;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>
	/// Summarizes the static map layout for an LLM player: spawn points, ore fields and a coarse terrain grid.
	/// It only reveals what a human sees in the lobby map preview, never enemy units or the assigned enemy spawn.
	/// </summary>
	public static class LlmMapBriefing
	{
		const int MaxGridColumns = 48;
		const int FieldBlockSize = 6;

		public static JsonObject Describe(Player player)
		{
			var world = player.World;
			var map = world.Map;
			var bounds = map.Bounds;
			var home = player.HomeLocation;
			var spawns = map.ActorDefinitions
				.Where(n => n.Value.Value == "mpspawn")
				.Select(n => new ActorReference(n.Key, n.Value).GetValue<LocationInit, CPos>())
				.ToList();

			var others = spawns.Where(s => s != home)
				.OrderByDescending(s => (s - home).LengthSquared)
				.Select(s => (JsonNode)new JsonObject { ["cell"] = Cell(s), ["distance"] = Distance(s, home) })
				.ToArray();

			var scale = Math.Max(1, (Math.Max(bounds.Width, bounds.Height) + MaxGridColumns - 1) / MaxGridColumns);
			return new JsonObject
			{
				["title"] = map.Title,
				["bounds"] = new JsonObject
				{
					["left"] = bounds.Left,
					["top"] = bounds.Top,
					["right"] = bounds.Right - 1,
					["bottom"] = bounds.Bottom - 1,
				},
				["your_spawn"] = Cell(home),
				["other_spawns"] = new JsonArray(others),
				["ore_fields"] = OreFields(world, home),
				["grid_scale"] = scale,
				["grid"] = Grid(world, home, spawns, scale),
			};
		}

		/// <summary>Groups resource cells into connected fields of coarse blocks, nearest to the player's spawn first.</summary>
		static JsonArray OreFields(World world, CPos home)
		{
			var layer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (layer == null)
				return [];

			var blocks = new Dictionary<int2, List<(CPos Cell, string Type)>>();
			foreach (var cell in world.Map.AllCells)
			{
				var type = layer.GetResource(cell).Type;
				if (type == null)
					continue;

				var key = new int2(cell.X / FieldBlockSize, cell.Y / FieldBlockSize);
				if (!blocks.TryGetValue(key, out var list))
					blocks[key] = list = [];

				list.Add((cell, type));
			}

			var fields = new List<List<(CPos Cell, string Type)>>();
			var seen = new HashSet<int2>();
			foreach (var start in blocks.Keys)
			{
				if (!seen.Add(start))
					continue;

				var field = new List<(CPos Cell, string Type)>();
				var open = new Stack<int2>([start]);
				while (open.TryPop(out var block))
				{
					field.AddRange(blocks[block]);
					for (var dx = -1; dx <= 1; dx++)
						for (var dy = -1; dy <= 1; dy++)
						{
							var next = new int2(block.X + dx, block.Y + dy);
							if (blocks.ContainsKey(next) && seen.Add(next))
								open.Push(next);
						}
				}

				fields.Add(field);
			}

			var result = fields.Select(f =>
			{
				var center = new CPos((int)f.Average(c => c.Cell.X), (int)f.Average(c => c.Cell.Y));
				return (Center: center, Node: (JsonNode)new JsonObject
				{
					["center"] = Cell(center),
					["cells"] = f.Count,
					["resource"] = string.Join("/", f.Select(c => c.Type).Distinct()),
					["distance_from_you"] = Distance(center, home),
				});
			});

			return [.. result.OrderBy(f => (f.Center - home).LengthSquared).Select(f => f.Node)];
		}

		/// <summary>
		/// One character per scale x scale block: Y = your spawn, E = other spawn, $ = ore,
		/// ~ = water, # = impassable for ground units, . = open ground.
		/// </summary>
		static JsonArray Grid(World world, CPos home, List<CPos> spawns, int scale)
		{
			var map = world.Map;
			var bounds = map.Bounds;
			var layer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			var ground = world.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault(l => l.Info.Name == "tracked")
				?? world.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault();

			var rows = new JsonArray();
			for (var by = bounds.Top; by < bounds.Bottom; by += scale)
			{
				var line = new StringBuilder();
				for (var bx = bounds.Left; bx < bounds.Right; bx += scale)
				{
					int open = 0, water = 0, blocked = 0, ore = 0;
					char? marker = null;
					for (var y = by; y < Math.Min(by + scale, bounds.Bottom); y++)
					{
						for (var x = bx; x < Math.Min(bx + scale, bounds.Right); x++)
						{
							var cell = new CPos(x, y);
							if (cell == home)
								marker = 'Y';
							else if (marker == null && spawns.Contains(cell))
								marker = 'E';

							if (layer != null && layer.GetResource(cell).Type != null)
								ore++;

							if (ground != null && ground.MovementCostForCell(cell) != PathGraph.MovementCostForUnreachableCell)
								open++;
							else if (map.GetTerrainInfo(cell).Type is "Water" or "River")
								water++;
							else
								blocked++;
						}
					}

					line.Append(marker ?? (ore > 0 ? '$' : open >= water && open >= blocked ? '.' : water >= blocked ? '~' : '#'));
				}

				rows.Add($"y={by,3} {line}");
			}

			return rows;
		}

		static int Distance(CPos a, CPos b) { return (int)Math.Round(Math.Sqrt((a - b).LengthSquared)); }

		static JsonArray Cell(CPos cell) { return new JsonArray(cell.X, cell.Y); }
	}
}
