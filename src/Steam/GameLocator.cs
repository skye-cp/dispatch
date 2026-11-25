using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dispatch.Model;
using Microsoft.Win32;

namespace Dispatch.Steam
{
    /// <summary>
    /// Finds Steam's library inside the player's own Bodycam install, so Dispatch never ships it.
    /// A private copy is loaded, so the game's file is only ever read.
    /// </summary>
    internal static class GameLocator
    {
        private const string LibraryName = "steam_api64.dll";

        private static readonly Regex PathEntry = new Regex("\"path\"\\s+\"(?<p>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex OldLibraryEntry = new Regex("\"\\d+\"\\s+\"(?<p>[A-Za-z]:(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        private static readonly Regex InstallDirEntry = new Regex("\"installdir\"\\s+\"(?<p>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);

        public static string CacheFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dispatch");

        /// <summary>Returns the path of a private copy of the game's Steam library.</summary>
        public static string PrepareSteamApi()
        {
            try
            {
                var source = FindSteamApi();
                Directory.CreateDirectory(CacheFolder);
                var target = Path.Combine(CacheFolder, LibraryName);
                var src = new FileInfo(source);
                var dst = new FileInfo(target);
                if (!dst.Exists || dst.Length != src.Length || dst.LastWriteTimeUtc != src.LastWriteTimeUtc)
                {
                    // Copied beside the target and then swapped in, so a reader never sees a half-written library.
                    var temp = target + ".tmp";
                    File.Copy(source, temp, true);
                    File.SetLastWriteTimeUtc(temp, src.LastWriteTimeUtc);
                    if (dst.Exists)
                    {
                        File.Replace(temp, target, null);
                    }
                    else
                    {
                        File.Move(temp, target);
                    }
                }

                return target;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException)
            {
                throw new LocatorException("Could not prepare Steam's library from the game folder: " + e.Message);
            }
        }

        public static string FindSteamApi()
        {
            var game = FindGameFolder();
            var plugins = Path.Combine(game, "Bodycam", "Plugins");
            var hit = SafeFind(plugins) ?? SafeFind(game);
            if (hit == null)
            {
                throw new LocatorException("Steam's library was not found in the Bodycam folder. Verify the game files in Steam.");
            }

            return hit;
        }

        public static string FindGameFolder()
        {
            var steam = FindSteamFolder();
            foreach (var library in LibraryFolders(steam))
            {
                var manifest = Path.Combine(library, "steamapps", "appmanifest_" + Catalog.AppId + ".acf");
                if (!File.Exists(manifest))
                {
                    continue;
                }

                var dir = ParseInstallDir(File.ReadAllText(manifest));
                var game = Path.Combine(library, "steamapps", "common", string.IsNullOrEmpty(dir) ? "Bodycam" : dir);
                if (Directory.Exists(game))
                {
                    return game;
                }
            }

            throw new LocatorException("Bodycam is not installed in any Steam library on this PC.");
        }

        public static string FindSteamFolder()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                var path = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }

            throw new LocatorException("Steam is not installed for this Windows user.");
        }

        public static List<string> LibraryFolders(string steamFolder)
        {
            var folders = new List<string> { steamFolder };
            var vdf = Path.Combine(steamFolder, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                folders.AddRange(ParseLibraryFolders(File.ReadAllText(vdf)));
            }

            return folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static IEnumerable<string> ParseLibraryFolders(string vdf)
        {
            var paths = PathEntry.Matches(vdf).Cast<Match>().Select(m => Unescape(m.Groups["p"].Value)).ToList();

            // Steam's format before 2021 listed libraries as numbered entries instead: "1"  "D:\\SteamLibrary".
            return paths.Count > 0 ? paths : OldLibraryEntry.Matches(vdf).Cast<Match>().Select(m => Unescape(m.Groups["p"].Value)).ToList();
        }

        public static string ParseInstallDir(string acf)
        {
            var m = InstallDirEntry.Match(acf);
            return m.Success ? Unescape(m.Groups["p"].Value) : "";
        }

        private static string Unescape(string value) => value.Replace("\\\\", "\\").Replace("\\\"", "\"");

        private static string SafeFind(string folder)
        {
            try
            {
                return Directory.Exists(folder)
                    ? PickLibrary(Directory.EnumerateFiles(folder, LibraryName, SearchOption.AllDirectories))
                    : null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// Of several copies, the newest Steamworks build: an older one bundled elsewhere in the game folder may lack
        /// the API Dispatch calls. (Bodycam ships one copy, inside its Steam plugin's ThirdParty folder.)
        /// </summary>
        private static string PickLibrary(IEnumerable<string> paths) =>
            PickNewest(paths.Select(p => (p, FileVersionInfo.GetVersionInfo(p).FileVersion)));

        /// <summary>The path with the highest version; unreadable versions rank last, and ties keep the first found.</summary>
        public static string PickNewest(IEnumerable<(string Path, string Version)> copies) =>
            copies
                .Select((c, i) => (c.Path, Index: i, Version: Version.TryParse(c.Version ?? "", out var v) ? v : new Version(0, 0)))
                .OrderByDescending(c => c.Version)
                .ThenBy(c => c.Index)
                .Select(c => c.Path)
                .FirstOrDefault();
    }

    /// <summary>Bodycam or its Steam library could not be found: a lookup failure with a clearer message.</summary>
    internal sealed class LocatorException : LookupException
    {
        public LocatorException(string message)
            : base(message)
        {
        }
    }
}
