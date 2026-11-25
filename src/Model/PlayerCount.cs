using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Dispatch.Model
{
    /// <summary>
    /// How many people are playing Bodycam right now, from Steam's public Web API (no key needed).
    /// Steam doesn't publish per-server counts, so this app-wide total is the honest figure available.
    /// </summary>
    internal static class PlayerCount
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

        private const string Endpoint = "https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid=" + Catalog.AppId;
        private static readonly Regex Count = new Regex("\"player_count\"\\s*:\\s*(?<n>\\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex Ok = new Regex("\"result\"\\s*:\\s*1\\b", RegexOptions.CultureInvariant);

        /// <summary>The current total, or null if Steam didn't answer.</summary>
        public static async Task<int?> FetchAsync() => Parse(await Web.GetTextAsync(Endpoint, "Dispatch").ConfigureAwait(false));

        public static int? Parse(string json)
        {
            var m = Count.Match(json ?? "");
            return m.Success && Ok.IsMatch(json) && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : (int?)null;
        }
    }
}
