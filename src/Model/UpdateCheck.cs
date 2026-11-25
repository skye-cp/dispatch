using System;
using System.IO;
using System.Net;
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
    /// Asks GitHub for the latest release. It only works while the repository is public and fails
    /// silently otherwise (private repository, offline, rate-limited), so it never bothers the user.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Repository = "skye-cp/dispatch";
        public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

        private static readonly Regex Tag = new Regex("\"tag_name\"\\s*:\\s*\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex Url = new Regex("\"html_url\"\\s*:\\s*\"(?<u>https://github\\.com/[^\"]+/releases/tag/[^\"]+)\"", RegexOptions.CultureInvariant);

        public static Task<ReleaseInfo> LatestAsync() => Task.Run(() =>
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repository + "/releases/latest");
                request.UserAgent = "Dispatch-update-check";
                request.Accept = "application/vnd.github+json";
                request.Timeout = 6000;
                request.ReadWriteTimeout = 6000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    return Parse(reader.ReadToEnd());
                }
            }
            catch (WebException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        });

        public static ReleaseInfo Parse(string json)
        {
            var tag = Tag.Match(json ?? "");
            var url = Url.Match(json ?? "");
            return tag.Success && url.Success && TryVersion(tag.Groups["v"].Value, out var version)
                ? new ReleaseInfo(version, tag.Groups["v"].Value, url.Groups["u"].Value)
                : null;
        }

        public static bool TryVersion(string tag, out Version version) =>
            Version.TryParse((tag ?? "").TrimStart('v', 'V'), out version);

        public static bool IsNewer(ReleaseInfo latest, Version current) =>
            latest != null && latest.Version > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
    }
}
