using System;
using System.Collections.Generic;
using System.Linq;

namespace Dispatch.Model
{
    /// <summary>What the user wants to see. An empty set means "any".</summary>
    internal sealed class FilterState
    {
        public HashSet<string> Modes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Maps { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"true" and/or "false". Both or neither means any.</summary>
        public HashSet<string> Bots { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Regions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Part of a host name, matched as you type.</summary>
        public string Text { get; set; } = "";

        /// <summary>Exact host name searched across all servers; overrides the other filters.</summary>
        public string ExactHost { get; set; } = "";

        public bool IsHostSearch => !string.IsNullOrEmpty(ExactHost);

        public string OneSidedBots => Bots.Count == 1 ? Bots.First() : null;

        public int ActiveCount =>
            (Modes.Count > 0 ? 1 : 0) + (Maps.Count > 0 ? 1 : 0) + (OneSidedBots != null ? 1 : 0) +
            (Regions.Count > 0 ? 1 : 0) + (string.IsNullOrWhiteSpace(Text) ? 0 : 1);

        public void Reset()
        {
            Modes.Clear();
            Maps.Clear();
            Bots.Clear();
            Regions.Clear();
            Text = "";
            ExactHost = "";
        }

        public bool Matches(Server s)
        {
            if (IsHostSearch)
            {
                return string.Equals(s.Name, ExactHost, StringComparison.OrdinalIgnoreCase);
            }

            if (Modes.Count > 0 ? !Modes.Contains(s.Mode) : s.Mode == Catalog.Training)
            {
                return false;
            }

            if (Maps.Count > 0 && !Maps.Contains(s.Map))
            {
                return false;
            }

            var bots = OneSidedBots;
            if (bots != null && !string.Equals(s.Bots, bots, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Regions.Count > 0 && !Regions.Contains(s.Region))
            {
                return false;
            }

            var text = (Text ?? "").Trim();
            return text.Length == 0 || s.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// Turns filters into Steam requests. Steam returns at most 50 lobbies per request and only
    /// ANDs filters, so the planner asks once per selected value to reach more servers.
    /// </summary>
    internal static class QueryPlanner
    {
        public const int MaxQueries = 30;

        public static List<LobbyQuery> Plan(FilterState f)
        {
            if (f.IsHostSearch)
            {
                return new List<LobbyQuery> { LobbyQuery.HostName(f.ExactHost) };
            }

            var modes = f.Modes.Count > 0
                ? Catalog.Modes.Where(f.Modes.Contains).Concat(f.Modes.Where(m => !Catalog.Modes.Contains(m))).ToList()
                : Catalog.DefaultModes.ToList();
            var maps = Catalog.Maps.Where(f.Maps.Contains)
                .Concat(f.Maps.Where(m => !Catalog.Maps.Contains(m)).OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var bots = f.OneSidedBots;
            var perMap = maps.Count > 0 && modes.Count * maps.Count <= MaxQueries;

            var plan = new List<LobbyQuery>();
            foreach (var mode in modes)
            {
                if (perMap)
                {
                    plan.AddRange(maps.Select(map => LobbyQuery.Matches(mode, map, bots)));
                }
                else
                {
                    plan.Add(LobbyQuery.Matches(mode, null, bots));
                }
            }

            return plan;
        }
    }
}
