using System;
using System.Collections.Generic;
using System.Linq;

namespace Dispatch.Model
{
    /// <summary>Fixed facts about Bodycam's lobbies: app id, modes, maps and relay regions.</summary>
    internal static class Catalog
    {
        public const string AppId = "2406770";
        public const string Training = "Training";

        /// <summary>Game modes in the order the filter shows them.</summary>
        public static readonly IReadOnlyList<string> Modes = new[]
        {
            "TeamDeathMatch", "Deathmatch", "GunGame", "HardPoint", "Versus", "Wingman", "Zombie", Training,
        };

        /// <summary>Modes fetched when no mode is selected. Training lobbies are private shooting ranges.</summary>
        public static readonly IReadOnlyList<string> DefaultModes = Modes.Where(m => m != Training).ToArray();

        /// <summary>Known maps (lobby value), in the order the filter shows them.</summary>
        public static readonly IReadOnlyList<string> Maps = new[]
        {
            "AirSoft", "Asylum", "BombHouse", "CQB", "Hospital", "Paintball", "PublicPool", "Rome",
            "RussianBuilding", "TheBackrooms", "Main_Trenches_Level_Design", "Village", "WornHouse",
        };

        private static readonly Dictionary<string, string> MapNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Main_Trenches_Level_Design"] = "Trenches",
            ["Lobby"] = "Shooting Range",
        };

        private static readonly Dictionary<string, string> RegionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gru"] = "São Paulo",
            ["eze"] = "Buenos Aires",
            ["scl"] = "Santiago",
            ["lim"] = "Lima",
            ["bog"] = "Bogotá",
            ["iad"] = "Virginia",
            ["atl"] = "Atlanta",
            ["dfw"] = "Dallas",
            ["ord"] = "Chicago",
            ["lax"] = "Los Angeles",
            ["sea"] = "Seattle",
            ["mia"] = "Miami",
            ["lhr"] = "London",
            ["fra"] = "Frankfurt",
            ["ams"] = "Amsterdam",
            ["par"] = "Paris",
            ["mad"] = "Madrid",
            ["sto"] = "Stockholm",
            ["sto2"] = "Stockholm",
            ["waw"] = "Warsaw",
            ["vie"] = "Vienna",
            ["hel"] = "Helsinki",
            ["dxb"] = "Dubai",
            ["bom"] = "Mumbai",
            ["bom2"] = "Mumbai",
            ["maa"] = "Chennai",
            ["maa2"] = "Chennai",
            ["sgp"] = "Singapore",
            ["hkg"] = "Hong Kong",
            ["tyo"] = "Tokyo",
            ["seo"] = "Seoul",
            ["syd"] = "Sydney",
            ["jnb"] = "Johannesburg",
        };

        public static string MapName(string map) =>
            string.IsNullOrEmpty(map) ? "—" : MapNames.TryGetValue(map, out var name) ? name : map;

        public static string RegionName(string code) =>
            string.IsNullOrEmpty(code) ? "—" : RegionNames.TryGetValue(code, out var name) ? name : code.ToUpperInvariant();

        /// <summary>Relay region of a host from its Steam ping data ("gru=12+1,eze=52+5/40+1,...").</summary>
        public static string RegionFromPing(string pingData)
        {
            if (string.IsNullOrEmpty(pingData))
            {
                return "";
            }

            var end = pingData.IndexOf('=');
            return end > 0 ? pingData.Substring(0, end).Trim().ToLowerInvariant() : "";
        }
    }
}
