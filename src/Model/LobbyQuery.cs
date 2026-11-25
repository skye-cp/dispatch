using System.Collections.Generic;
using System.Linq;

namespace Dispatch.Model
{
    internal enum Comparison
    {
        Equal = 0,
        NotEqual = 3,
    }

    /// <summary>One string filter of a Steam lobby-list request.</summary>
    internal sealed class LobbyFilter
    {
        public LobbyFilter(string key, string value, Comparison comparison = Comparison.Equal)
        {
            Key = key;
            Value = value;
            Comparison = comparison;
        }

        public string Key { get; }
        public string Value { get; }
        public Comparison Comparison { get; }

        public override string ToString() => Key + (Comparison == Comparison.Equal ? "=" : "!=") + Value;
    }

    /// <summary>One Steam lobby-list request. Steam combines its filters with AND and returns up to 50 lobbies.</summary>
    internal sealed class LobbyQuery
    {
        public const string InGameKey = "InGame_s";
        public const string ModeKey = "GameMode_s";
        public const string MapKey = "Map_s";
        public const string BotsKey = "Bots_s";
        public const string NameKey = "OWNINGNAME_s";
        public const string OwnerKey = "OWNINGID";

        public LobbyQuery(IEnumerable<LobbyFilter> filters)
        {
            Filters = filters.ToList();
            Key = string.Join("&", Filters.Select(f => f.ToString()));
        }

        public IReadOnlyList<LobbyFilter> Filters { get; }

        /// <summary>Stable identity, used to cache results.</summary>
        public string Key { get; }

        public static LobbyQuery Matches(string mode = null, string map = null, string bots = null)
        {
            var filters = new List<LobbyFilter> { new LobbyFilter(InGameKey, "1") };
            if (mode != null)
            {
                filters.Add(new LobbyFilter(ModeKey, mode));
            }

            if (map != null)
            {
                filters.Add(new LobbyFilter(MapKey, map));
            }

            if (bots != null)
            {
                filters.Add(new LobbyFilter(BotsKey, bots));
            }

            return new LobbyQuery(filters);
        }

        /// <summary>
        /// Every mode except Training on one map. Used when maps are picked without a mode and per-mode-and-map requests would exceed <see cref="QueryPlanner.MaxQueries"/>: one request per map
        /// reaches far more of that map's servers than per-mode requests filtered on screen (50 against 14 for one
        /// map in a live test, November 2026), and Steam honours the "not Training" filter.
        /// </summary>
        public static LobbyQuery OnMap(string map, string bots = null)
        {
            var filters = new List<LobbyFilter>
            {
                new LobbyFilter(InGameKey, "1"),
                new LobbyFilter(ModeKey, Catalog.Training, Comparison.NotEqual),
                new LobbyFilter(MapKey, map),
            };
            if (bots != null)
            {
                filters.Add(new LobbyFilter(BotsKey, bots));
            }

            return new LobbyQuery(filters);
        }

        public static LobbyQuery HostName(string name) =>
            new LobbyQuery(new[] { new LobbyFilter(InGameKey, "1"), new LobbyFilter(NameKey, name) });

        public static LobbyQuery HostId(string owner) =>
            new LobbyQuery(new[] { new LobbyFilter(InGameKey, "1"), new LobbyFilter(OwnerKey, owner) });

        public override string ToString() => Key;
    }
}
