using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Dispatch.Model
{
    internal sealed class ReleaseInfo
    {
        public ReleaseInfo(Version version, string tag, string url)
        {
            Version = version;
            Tag = tag;
            Url = url;
        }

        public Version Version { get; }
        public string Tag { get; }
        public string Url { get; }
    }

    /// <summary>
    /// Asks GitHub for the latest release. Fails silently (offline, rate-limited, no release yet), so it
    /// never bothers the user.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Repository = "skye-cp/dispatch";
        public const string RepositoryUrl = "https://github.com/" + Repository;
        public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

        private static readonly Regex Tag = new Regex("\"tag_name\"\\s*:\\s*\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex Url = new Regex("\"html_url\"\\s*:\\s*\"(?<u>[^\"]+)\"", RegexOptions.CultureInvariant);

        /// <summary>Only a release page of this repository is ever offered or opened.</summary>
        private static readonly Regex ReleasePage = new Regex(
            @"\Ahttps://github\.com/" + Regex.Escape(Repository) + @"/releases/tag/[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant);

        public static bool IsReleasePage(string url) => url != null && ReleasePage.IsMatch(url);

        public static async Task<ReleaseInfo> LatestAsync() =>
            Parse(await Web.GetTextAsync("https://api.github.com/repos/" + Repository + "/releases/latest", "Dispatch-update-check", "application/vnd.github+json").ConfigureAwait(false));

        public static ReleaseInfo Parse(string json)
        {
            var tag = Tag.Match(json ?? "");
            var url = Url.Match(json ?? "");
            return tag.Success && url.Success && IsReleasePage(url.Groups["u"].Value) && TryVersion(tag.Groups["v"].Value, out var version)
                ? new ReleaseInfo(version, tag.Groups["v"].Value, url.Groups["u"].Value)
                : null;
        }

        public static bool TryVersion(string tag, out Version version) =>
            Version.TryParse((tag ?? "").TrimStart('v', 'V'), out version);

        /// <summary>Compares major.minor.build only, so "v0.8.0.0" and 0.8.0 count as the same version.</summary>
        public static bool IsNewer(ReleaseInfo latest, Version current) =>
            latest != null && Normal(latest.Version) > Normal(current);

        private static Version Normal(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}
