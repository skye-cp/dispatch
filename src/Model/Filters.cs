using System;
using System.Collections.Generic;
using System.Linq;

namespace Dispatch.Model
{
    /// <summary>The filter categories, in filter-bar order. Their names (lowercased) are the tags used in MainWindow.xaml.</summary>
    internal enum FilterKind
    {
        Mode,
        Map,
        Region,
        Age,
        Ping,
        Bots,
    }

    /// <summary>What the user wants to see. An empty set means "any".</summary>
    internal sealed class FilterState
    {
        public static readonly FilterKind[] Kinds = { FilterKind.Mode, FilterKind.Map, FilterKind.Bots, FilterKind.Region, FilterKind.Age, FilterKind.Ping };

        /// <summary>A XAML tag ("mode", "bots", …) as a filter kind.</summary>
        public static bool TryKind(object tag, out FilterKind kind)
        {
            kind = default(FilterKind);
            return tag is string text && text.Length > 0 && !char.IsDigit(text[0]) && Enum.TryParse(text, true, out kind) && Enum.IsDefined(typeof(FilterKind), kind);
        }

        /// <summary>Mode, map and bots are Steam search filters, so changing them can need new searches; the rest filter on screen.</summary>
        public static bool NeedsLookup(FilterKind kind) => kind == FilterKind.Mode || kind == FilterKind.Map || kind == FilterKind.Bots;

        public static string PingLimitText(int ms) => "≤ " + ms + " ms";

        /// <summary>What a click on this server's value filters by ("Zombie", "ping ≤ 50 ms"), or null when it can't filter.</summary>
        public static string Label(FilterKind kind, Server s)
        {
            switch (kind)
            {
                case FilterKind.Mode:
                    return s.Mode.Length > 0 ? s.Mode : null;
                case FilterKind.Map:
                    return s.Map.Length > 0 ? s.MapName : null;
                case FilterKind.Bots:
                    return s.Bots == "true" ? "bots on" : s.Bots == "false" ? "bots off" : null;
                case FilterKind.Region:
                    return s.Region.Length > 0 ? s.RegionName : null;
                case FilterKind.Age:
                    var age = AgeLimitFor(s.AgeMinutes);
                    return age != 0 ? "age " + AgeLimitText(age).ToLowerInvariant() : null;
                case FilterKind.Ping:
                    var limit = LimitFor(s.Ping);
                    return limit > 0 ? "ping " + PingLimitText(limit) : null;
                default:
                    return null;
            }
        }

        /// <summary>True when this server's value is what the filter currently selects.</summary>
        public bool IsActive(FilterKind kind, Server s)
        {
            switch (kind)
            {
                case FilterKind.Mode:
                    return Modes.Contains(s.Mode);
                case FilterKind.Map:
                    return Maps.Contains(s.Map);
                case FilterKind.Bots:
                    return OneSidedBots == s.Bots;
                case FilterKind.Region:
                    return Regions.Contains(s.Region);
                case FilterKind.Age:
                    return AgeLimit != 0 && AgeLimit == AgeLimitFor(s.AgeMinutes);
                case FilterKind.Ping:
                    return MaxPing > 0 && MaxPing == LimitFor(s.Ping);
                default:
                    return false;
            }
        }

        /// <summary>The menu or tooltip text for clicking this server's value.</summary>
        public string ActionText(FilterKind kind, Server s) =>
            (IsActive(kind, s) ? "Remove the " : "Filter by ") + Label(kind, s) + (IsActive(kind, s) ? " filter" : "");

        /// <summary>
        /// Filters by this server's value, or removes that filter when it is already on. Returns true when it
        /// removed it. <paramref name="regionCodes"/> are the relay codes seen, since one city can have several.
        /// </summary>
        public bool Toggle(FilterKind kind, Server s, IEnumerable<string> regionCodes)
        {
            var removing = IsActive(kind, s);
            switch (kind)
            {
                case FilterKind.Mode:
                    Flip(Modes, s.Mode, removing);
                    break;
                case FilterKind.Map:
                    Flip(Maps, s.Map, removing);
                    break;
                case FilterKind.Bots:
                    Bots.Clear();
                    if (!removing)
                    {
                        Bots.Add(s.Bots);
                    }

                    break;
                case FilterKind.Region:
                    // One city can have several relays (sto, sto2): toggle them together, like the chip.
                    var codes = regionCodes.Append(s.Region)
                        .Where(c => Catalog.RegionName(c) == s.RegionName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var code in codes)
                    {
                        Flip(Regions, code, removing);
                    }

                    break;
                case FilterKind.Age:
                    AgeLimit = removing ? 0 : AgeLimitFor(s.AgeMinutes);
                    break;
                case FilterKind.Ping:
                    MaxPing = removing ? 0 : LimitFor(s.Ping);
                    break;
            }

            return removing;
        }

        public void Clear(FilterKind kind)
        {
            switch (kind)
            {
                case FilterKind.Mode:
                    Modes.Clear();
                    break;
                case FilterKind.Map:
                    Maps.Clear();
                    break;
                case FilterKind.Region:
                    Regions.Clear();
                    break;
                case FilterKind.Age:
                    AgeLimit = 0;
                    break;
                case FilterKind.Ping:
                    MaxPing = 0;
                    break;
                case FilterKind.Bots:
                    Bots.Clear();
                    break;
            }
        }

        /// <summary>What a filter-bar button shows as selected: names in display order, empty for any.</summary>
        public List<string> Selected(FilterKind kind)
        {
            switch (kind)
            {
                case FilterKind.Mode:
                    return Catalog.Modes.Where(Modes.Contains).Concat(Modes.Where(m => !Catalog.Modes.Contains(m, StringComparer.OrdinalIgnoreCase))).ToList();
                case FilterKind.Map:
                    return Maps.Select(Catalog.MapName).OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase).ToList();
                case FilterKind.Region:
                    return Regions.Select(Catalog.RegionName).Distinct().OrderBy(r => r, StringComparer.CurrentCultureIgnoreCase).ToList();
                case FilterKind.Age:
                    return AgeLimit != 0 ? new List<string> { AgeLimitText(AgeLimit) } : new List<string>();
                case FilterKind.Ping:
                    return MaxPing > 0 ? new List<string> { PingLimitText(MaxPing) } : new List<string>();
                case FilterKind.Bots:
                    return OneSidedBots != null ? new List<string> { OneSidedBots == "true" ? "On" : "Off" } : new List<string>();
                default:
                    return new List<string>();
            }
        }

        /// <summary>A filter-bar button's value: "Any", "A, B" or "A +2".</summary>
        public static string Summary(IReadOnlyList<string> selected) =>
            selected.Count == 0 ? "Any" : selected.Count <= 2 ? string.Join(", ", selected) : selected[0] + " +" + (selected.Count - 1);

        private static void Flip(HashSet<string> set, string value, bool remove)
        {
            if (remove)
            {
                set.Remove(value);
            }
            else
            {
                set.Add(value);
            }
        }

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

        /// <summary>The ping limits offered as filters, in ms.</summary>
        public static readonly int[] PingLimits = { 50, 100, 150, 200 };

        /// <summary>Highest ping to show, in ms; 0 means any. Servers whose ping is not known yet stay visible.</summary>
        public int MaxPing { get; set; }

        /// <summary>Server age filters, in minutes: positive keeps servers up to that age, negative keeps older ones.</summary>
        public static readonly int[] AgeLimits = { 15, 30, 60, -15, -30, -60 };

        /// <summary>One of <see cref="AgeLimits"/>, or 0 for any age. Servers with no estimate stay visible.</summary>
        public int AgeLimit { get; set; }

        /// <summary>The age filter a server of this age falls in.</summary>
        public static int AgeLimitFor(double minutes) => minutes < 0 ? 0 : minutes <= 15 ? 15 : minutes <= 30 ? 30 : minutes <= 60 ? 60 : -60;

        public static string AgeLimitText(int limit) => (limit > 0 ? "Under " : "Over ") + (Math.Abs(limit) < 60 ? Math.Abs(limit) + " min" : Math.Abs(limit) / 60 + " h");

        public static bool AgeMatches(int limit, double minutes) =>
            limit == 0 || minutes < 0 || (limit > 0 ? minutes <= limit : minutes > -limit);

        /// <summary>The smallest offered limit that still includes this ping, or 0 if none does.</summary>
        public static int LimitFor(int ping) => ping < 0 ? 0 : PingLimits.FirstOrDefault(limit => ping <= limit);

        public string OneSidedBots => Bots.Count == 1 ? Bots.First() : null;

        public int ActiveCount =>
            (Modes.Count > 0 ? 1 : 0) + (Maps.Count > 0 ? 1 : 0) + (OneSidedBots != null ? 1 : 0) +
            (Regions.Count > 0 ? 1 : 0) + (MaxPing > 0 ? 1 : 0) + (AgeLimit != 0 ? 1 : 0) + (string.IsNullOrWhiteSpace(Text) ? 0 : 1);

        public void Reset()
        {
            Modes.Clear();
            Maps.Clear();
            Bots.Clear();
            Regions.Clear();
            MaxPing = 0;
            AgeLimit = 0;
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

            if (MaxPing > 0 && s.Ping >= 0 && s.Ping > MaxPing)
            {
                return false;
            }

            if (!AgeMatches(AgeLimit, s.AgeMinutes))
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
        /// <summary>Cap on per-map requests: each takes about a third of a second, so a full plan stays well inside the lookup timeout.</summary>
        public const int MaxQueries = 30;

        public static List<LobbyQuery> Plan(FilterState f)
        {
            if (f.IsHostSearch)
            {
                return new List<LobbyQuery> { LobbyQuery.HostName(f.ExactHost) };
            }

            var modes = f.Modes.Count > 0
                ? Catalog.Modes.Where(f.Modes.Contains).Concat(f.Modes.Where(m => !Catalog.Modes.Contains(m, StringComparer.OrdinalIgnoreCase))).ToList()
                : Catalog.DefaultModes.ToList();
            var maps = Catalog.Maps.Where(f.Maps.Contains)
                .Concat(f.Maps.Where(m => !Catalog.Maps.Contains(m, StringComparer.OrdinalIgnoreCase)).OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
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
