using System;
using System.Collections.Generic;
using System.Linq;

namespace Dispatch.Model
{
    /// <summary>
    /// Invented servers for screenshots and demos (<c>Dispatch.exe --demo</c>), so documentation
    /// never shows real players' names. Nothing here talks to Steam.
    /// </summary>
    internal static class DemoData
    {
        private static readonly (string Name, string Mode, string Map, string Bots, string Region, int Slots, int Ping)[] Rows =
        {
            ("Night Shift", "TeamDeathMatch", "Main_Trenches_Level_Design", "true", "gru", 10, 21),
            ("Patrol 7", "Zombie", "Asylum", "false", "gru", 4, 23),
            ("Code Three", "Deathmatch", "Rome", "true", "gru", 7, 24),
            ("Backup Unit", "HardPoint", "AirSoft", "true", "gru", 10, 26),
            ("Graveyard Watch", "Zombie", "Village", "true", "gru", 4, 29),
            ("Dispatch Test", "GunGame", "RussianBuilding", "false", "gru", 7, 31),
            ("Precinct 12", "Versus", "WornHouse", "false", "eze", 10, 52),
            ("Lights and Sirens", "TeamDeathMatch", "Hospital", "true", "eze", 10, 58),
            ("Second Shift", "Wingman", "PublicPool", "false", "scl", 4, 74),
            ("Cold Case", "Deathmatch", "BombHouse", "false", "lim", 7, 96),
            ("Unit 44", "Zombie", "Main_Trenches_Level_Design", "true", "iad", 4, 118),
            ("Stakeout", "HardPoint", "CQB", "true", "atl", 10, 127),
            ("Ten Four", "GunGame", "TheBackrooms", "false", "atl", 7, 131),
            ("Night Court", "TeamDeathMatch", "Paintball", "true", "dfw", 10, 142),
            ("Overwatch", "Versus", "AirSoft", "true", "ord", 10, 156),
            ("Last Call", "Deathmatch", "WornHouse", "true", "lax", 7, 171),
        };

        /// <summary>An invented Steam friend hosting one demo server, so screenshots show the FRIEND badge.</summary>
        public static readonly IReadOnlyDictionary<string, string> Friends = new Dictionary<string, string>
        {
            [OwnerOf(6)] = "Sam",
        };

        private const ulong FirstId = 100000000000000000UL;

        /// <summary>Invented server ages in minutes, one per row.</summary>
        private static readonly int[] Ages = { 3, 12, 25, 40, 8, 55, 95, 18, 6, 140, 33, 70, 15, 47, 22, 9 };

        /// <summary>The demo row shown as having just started a new round.</summary>
        private const int NewRoundRow = 2;

        public static double AgeFor(string id) => RowOf(id) is int row && row < Ages.Length ? Ages[row] : -1;

        public static bool IsNewRound(string id) => RowOf(id) == NewRoundRow;

        private static int? RowOf(string id) =>
            ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= FirstId && n < FirstId + (ulong)Rows.Length
                ? (int)(n - FirstId)
                : (int?)null;

        /// <summary>An invented "playing now" total for the demo.</summary>
        public const int PlayersOnline = 4812;

        /// <summary>A demo host shown as followed.</summary>
        public static readonly string FollowedOwner = OwnerOf(4);

        public static string FollowedName => Rows[4].Name;

        private static string OwnerOf(int row) => (200000000000UL + (ulong)row).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static List<Server> For(LobbyQuery query, DateTime nowUtc)
        {
            var servers = Rows.Select((r, i) => new Server
            {
                Id = (FirstId + (ulong)i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Owner = OwnerOf(i),
                Name = r.Name,
                Mode = r.Mode,
                Map = r.Map,
                Bots = r.Bots,
                Region = r.Region,
                Limit = r.Slots,
                Ping = r.Ping,
                Rank = i,
                SeenUtc = nowUtc,
            });
            return servers.Where(s => query.Filters.All(f => Matches(s, f))).ToList();
        }

        private static bool Matches(Server s, LobbyFilter f)
        {
            string value;
            switch (f.Key)
            {
                case LobbyQuery.ModeKey:
                    value = s.Mode;
                    break;
                case LobbyQuery.MapKey:
                    value = s.Map;
                    break;
                case LobbyQuery.BotsKey:
                    value = s.Bots;
                    break;
                case LobbyQuery.NameKey:
                    value = s.Name;
                    break;
                case LobbyQuery.OwnerKey:
                    value = s.Owner;
                    break;
                default:
                    return true;
            }

            var equal = string.Equals(value, f.Value, StringComparison.OrdinalIgnoreCase);
            return f.Comparison == Comparison.Equal ? equal : !equal;
        }
    }
}
