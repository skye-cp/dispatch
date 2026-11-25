using System.Text.RegularExpressions;

namespace Dispatch.Model
{
    /// <summary>Steam's own join link: Steam hands it to the game, and the game joins the lobby.</summary>
    internal static class JoinLink
    {
        private static readonly Regex Pattern = new Regex(
            @"steam://joinlobby/" + Catalog.AppId + @"/(?<lobby>\d{5,20})/(?<owner>\d{5,20})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string Format(string lobby, string owner) =>
            "steam://joinlobby/" + Catalog.AppId + "/" + lobby + "/" + owner;

        /// <summary>Finds a Bodycam join link anywhere in the text, such as a pasted chat message.</summary>
        public static bool TryParse(string text, out string link)
        {
            link = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var m = Pattern.Match(text);
            if (!m.Success)
            {
                return false;
            }

            link = Format(m.Groups["lobby"].Value, m.Groups["owner"].Value);
            return true;
        }
    }
}
