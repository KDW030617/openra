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
using System.Text.RegularExpressions;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>
	/// Lists everything the player's faction can eventually build in this game, with costs, roles
	/// and the buildings each item requires, so an LLM can plan a build order before it is unlocked.
	/// </summary>
	public static class LlmTechTreeBriefing
	{
		public static JsonArray Describe(Player player)
		{
			var rules = player.World.Map.Rules;
			var faction = player.Faction.InternalName;
			var queueTypes = AIUtils.FindQueuesByCategory(player).SelectMany(g => g).Select(q => q.Info.Type).ToHashSet();
			var candidates = rules.Actors.Values
				.Where(a => a.TraitInfoOrDefault<BuildableInfo>()?.Queue.Any(queueTypes.Contains) == true)
				.ToList();

			// Prerequisites active right now (tech level, game options) plus those of the starting units' deployed forms.
			var provided = player.PlayerActor.TraitsImplementing<ITechTreePrerequisite>()
				.SelectMany(p => p.ProvidesPrerequisites).ToHashSet();
			var owned = player.World.Actors.Where(a => a.Owner == player).Select(a => a.Info).ToList();
			var reachable = owned
				.Concat(owned.SelectMany(a => a.TraitInfos<TransformsInfo>()).Select(t => rules.Actors[t.IntoActor]))
				.ToHashSet();

			var providers = new Dictionary<string, HashSet<string>>();
			bool changed;
			do
			{
				changed = false;
				foreach (var actor in reachable)
				{
					foreach (var p in actor.TraitInfos<ProvidesPrerequisiteInfo>())
					{
						if ((p.Factions.Count > 0 && !p.Factions.Contains(faction)) || !Satisfied(p.RequiresPrerequisites, provided))
							continue;

						var name = p.Prerequisite ?? actor.Name;
						changed |= provided.Add(name);
						if (!providers.TryGetValue(name, out var set))
							providers[name] = set = [];

						set.Add(actor.Name);
					}
				}

				foreach (var actor in candidates)
					if (!reachable.Contains(actor) && Satisfied(actor.TraitInfo<BuildableInfo>().Prerequisites, provided))
						changed |= reachable.Add(actor);
			}
			while (changed);

			var buildable = candidates.Where(reachable.Contains).Select(a => a.Name).ToHashSet();
			var items = candidates.Where(reachable.Contains)
				.OrderBy(a => a.TraitInfo<BuildableInfo>().Queue.First())
				.ThenBy(a => a.TraitInfo<BuildableInfo>().BuildPaletteOrder)
				.Select(a => (JsonNode)Item(a, buildable, providers));

			return [.. items];
		}

		static JsonObject Item(ActorInfo actor, HashSet<string> buildable, Dictionary<string, HashSet<string>> providers)
		{
			var bi = actor.TraitInfo<BuildableInfo>();
			var requires = bi.Prerequisites
				.Where(p => !p.StartsWith('!'))
				.Select(p => p.TrimStart('~'))
				.Select(p => buildable.Contains(p) ? p
					: providers.TryGetValue(p, out var set) ? string.Join("|", set.Where(buildable.Contains).Order()) : "")
				.Where(p => p.Length > 0);

			var tooltip = actor.TraitInfos<TooltipInfo>().FirstOrDefault();
			return new JsonObject
			{
				["name"] = actor.Name,
				["label"] = Message(tooltip?.Name),
				["queue"] = bi.Queue.First(),
				["cost"] = actor.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0,
				["requires"] = new JsonArray(requires.Select(r => (JsonNode)r).ToArray()),
				["notes"] = Message(bi.Description),
			};
		}

		static bool Satisfied(IEnumerable<string> prerequisites, HashSet<string> provided)
		{
			return prerequisites.All(p => p.StartsWith('!') || p.StartsWith("~!", StringComparison.Ordinal)
				|| provided.Contains(p.TrimStart('~')));
		}

		static string Message(string key)
		{
			if (string.IsNullOrEmpty(key))
				return null;

			return Regex.Replace(FluentProvider.GetMessage(key), @"\s+", " ").Trim();
		}
	}
}
