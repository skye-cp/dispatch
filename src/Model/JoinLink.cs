using System.Text.RegularExpressions;

namespace Dispatch.Model
{
    /// <summary>Steam's own join link: Steam hands it to the game, and the game joins the lobby.</summary>
    internal static class JoinLink
    {
        private static readonly Regex Pattern = new Regex(
            @"steam://joinlobby/" + Catalog.AppId + @"/(?<lobby>[0-9]{5,20})/(?<owner>[0-9]{5,20})(?![0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Exact = new Regex(
            @"\Asteam://joinlobby/" + Catalog.AppId + @"/[0-9]{5,20}/[0-9]{5,20}\z", RegexOptions.CultureInvariant);

        private const int ScanLimit = 4096;

        /// <summary>The join link for a lobby, or null when either ID is not a plain SteamID.</summary>
        public static string Format(string lobby, string owner) =>
            Server.IsSteamId(lobby) && Server.IsSteamId(owner)
                ? "steam://joinlobby/" + Catalog.AppId + "/" + lobby + "/" + owner
                : null;

        /// <summary>True only for a complete Bodycam join link and nothing else: the check before a link is opened.</summary>
        public static bool IsValid(string link) => link != null && Exact.IsMatch(link);

        /// <summary>Finds a Bodycam join link anywhere in the text, such as a pasted chat message.</summary>
        public static bool TryParse(string text, out string link)
        {
            link = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            // The first well-formed link wins; a malformed one earlier in the text doesn't hide it.
            // Only the start of a very long paste is searched, so a huge clipboard can't stall the window.
            foreach (Match m in Pattern.Matches(text.Length > ScanLimit ? text.Substring(0, ScanLimit) : text))
            {
                link = Format(m.Groups["lobby"].Value, m.Groups["owner"].Value);
                if (link != null)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
