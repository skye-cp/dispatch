using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Dispatch.Model;
using static Dispatch.Steam.SteamNative;

namespace Dispatch.Steam
{
    /// <summary>
    /// Runs inside the short-lived lookup process: reads requests from stdin, asks Steam for each
    /// public lobby list in turn, writes the answers to stdout and exits. It only reads public lobby
    /// data; it never creates or joins a lobby.
    /// </summary>
    internal static class LookupWorker
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        public static int Run(string steamApiPath)
        {
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

            var queries = new List<LobbyQuery>();
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Length > 0)
                {
                    queries.Add(Protocol.DecodeQuery(line));
                }
            }

            Environment.SetEnvironmentVariable("SteamAppId", Catalog.AppId);
            Environment.SetEnvironmentVariable("SteamGameId", Catalog.AppId);

            if (LoadLibraryW(steamApiPath) == IntPtr.Zero)
            {
                output.WriteLine(Protocol.EncodeError("library", "Could not load Steam's library from the game folder."));
                return 2;
            }

            try
            {
                var error = new byte[1024];
                if (SteamAPI_InitFlat(error) != 0)
                {
                    output.WriteLine(Protocol.EncodeError("steam", "Steam is not running or not signed in. " + FromUtf8(error)));
                    return 3;
                }
            }
            catch (EntryPointNotFoundException)
            {
                output.WriteLine(Protocol.EncodeError("library", "The game's Steam library is a version Dispatch does not know."));
                return 4;
            }

            try
            {
                var matchmaking = SteamAPI_SteamMatchmaking_v009();
                var utils = SteamAPI_SteamUtils_v010();
                for (var i = 0; i < queries.Count; i++)
                {
                    RunOne(i, queries[i], matchmaking, utils, output);
                }
            }
            finally
            {
                SteamAPI_Shutdown();
            }

            return 0;
        }

        private static void RunOne(int index, LobbyQuery query, IntPtr matchmaking, IntPtr utils, TextWriter output)
        {
            SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(matchmaking, DistanceWorldwide);
            foreach (var f in query.Filters)
            {
                SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(matchmaking, Utf8(f.Key), Utf8(f.Value), (int)f.Comparison);
            }

            // Steam cancels a pending request when a new one starts, so requests run one at a time.
            var call = SteamAPI_ISteamMatchmaking_RequestLobbyList(matchmaking);
            var clock = Stopwatch.StartNew();
            bool failed;
            while (!SteamAPI_ISteamUtils_IsAPICallCompleted(utils, call, out failed))
            {
                if (clock.Elapsed > RequestTimeout)
                {
                    output.WriteLine(Protocol.EncodeFailed(index));
                    return;
                }

                Thread.Sleep(15);
            }

            if (failed || !SteamAPI_ISteamUtils_GetAPICallResult(utils, call, out var count, sizeof(uint), LobbyMatchListCallback, out failed) || failed)
            {
                output.WriteLine(Protocol.EncodeFailed(index));
                return;
            }

            for (var i = 0; i < (int)count; i++)
            {
                var lobby = SteamAPI_ISteamMatchmaking_GetLobbyByIndex(matchmaking, i);
                string Data(string key) => FromUtf8(SteamAPI_ISteamMatchmaking_GetLobbyData(matchmaking, lobby, Utf8(key)));
                var name = Data("OWNINGNAME_s");
                var server = new Server
                {
                    Id = lobby.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Owner = Data("OWNINGID"),
                    Name = name.Length > 0 ? name : Data("OWNINGNAME"),
                    Mode = Data("GameMode_s"),
                    Map = Data("Map_s"),
                    Bots = Data("Bots_s"),
                    Region = Catalog.RegionFromPing(Data("P2PPING")),
                    Limit = SteamAPI_ISteamMatchmaking_GetLobbyMemberLimit(matchmaking, lobby),
                };
                if (server.Owner.Length > 0)
                {
                    output.WriteLine(Protocol.EncodeServer(index, server));
                }
            }

            output.WriteLine(Protocol.EncodeDone(index, (int)count));
        }
    }
}
