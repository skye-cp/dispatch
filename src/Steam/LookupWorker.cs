using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// <remarks>
    /// Pings are Steam's own estimates between the player's relay location and each host's, which
    /// every lobby publishes. Measuring the player's location takes a few seconds, so it happens
    /// once, in the background of the first lookup, and the window passes it back in later.
    /// </remarks>
    internal static class LookupWorker
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(10);

        /// <summary>The host's relay location, which every lobby publishes; the ping estimate is computed from it.</summary>
        private const string PingDataKey = "P2PPING";

        /// <summary>Older game versions published the host's name without the "_s" suffix.</summary>
        private const string LegacyNameKey = "OWNINGNAME";

        public static int Run(string steamApiPath)
        {
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

            var queries = new List<LobbyQuery>();
            string knownLocation = null;
            var sampleAge = false;
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (Protocol.TryDecodeLocationHeader(line, out var location))
                {
                    knownLocation = location;
                }
                else if (line == Protocol.AgeSampleHeader)
                {
                    sampleAge = true;
                }
                else if (line.Length > 0)
                {
                    queries.Add(Protocol.DecodeQuery(line));
                }
            }

            Environment.SetEnvironmentVariable("SteamAppId", Catalog.AppId);
            Environment.SetEnvironmentVariable("SteamGameId", Catalog.AppId);

            // The child loads nothing but Dispatch's own copy of Steam's library, and resolves its dependencies only from
            // that folder and System32, so a planted file elsewhere (the Downloads folder, the working directory) is ignored.
            var expected = Path.Combine(GameLocator.CacheFolder, GameLocator.LibraryName);
            if (!string.Equals(Path.GetFullPath(steamApiPath), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ||
                LoadLibraryExW(expected, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32) == IntPtr.Zero)
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
                var ping = Pinger.Start(knownLocation);
                WriteFriends(output);
                var matchmaking = SteamAPI_SteamMatchmaking_v009();
                var utils = SteamAPI_SteamUtils_v010();
                for (var i = 0; i < queries.Count; i++)
                {
                    RunOne(i, queries[i], matchmaking, utils, ping, output);
                }

                if (sampleAge)
                {
                    SampleNewest(matchmaking, utils, output);
                }

                ping.Finish(output);
            }
            finally
            {
                SteamAPI_Shutdown();
            }

            return 0;
        }

        /// <summary>
        /// Reports the player's Steam friends so the window can mark servers they host. Read from the
        /// local Steam client; the window keeps it in memory only.
        /// </summary>
        private static void WriteFriends(TextWriter output)
        {
            try
            {
                var friends = SteamAPI_SteamFriends_v017();
                if (friends == IntPtr.Zero)
                {
                    return;
                }

                var count = SteamAPI_ISteamFriends_GetFriendCount(friends, FriendFlagImmediate);
                for (var i = 0; i < count; i++)
                {
                    var id = SteamAPI_ISteamFriends_GetFriendByIndex(friends, i, FriendFlagImmediate);
                    var name = FromUtf8(SteamAPI_ISteamFriends_GetFriendPersonaName(friends, id));
                    output.WriteLine(Protocol.EncodeFriend(id.ToString(CultureInfo.InvariantCulture), name));
                }
            }
            catch (EntryPointNotFoundException)
            {
                // An unknown library version: friends simply aren't marked.
            }
        }

        /// <summary>
        /// One unfiltered request for the age estimate: the newest of the nearest 50 servers in any mode was
        /// created moments ago, whatever filters the player uses. Reports only the highest lobby counter.
        /// </summary>
        private static void SampleNewest(IntPtr matchmaking, IntPtr utils, TextWriter output)
        {
            var count = RequestLobbies(matchmaking, utils, new[] { new LobbyFilter(LobbyQuery.InGameKey, "1") });
            if (count == null)
            {
                return;
            }

            uint newest = 0;
            for (var i = 0; i < (int)count.Value; i++)
            {
                newest = LobbyAge.Newer(newest, LobbyAge.CounterOf(SteamAPI_ISteamMatchmaking_GetLobbyByIndex(matchmaking, i)));
            }

            if (count.Value >= LobbyAge.MinServersForAnchor && newest > 0)
            {
                output.WriteLine(Protocol.EncodeAnchor(newest));
            }
        }

        /// <summary>
        /// One public lobby-list request, worldwide, with these filters. Waits for Steam's answer and returns how many
        /// lobbies it listed, or null if it failed or took too long. Steam cancels a pending request when a new one
        /// starts, so requests run one at a time.
        /// </summary>
        private static uint? RequestLobbies(IntPtr matchmaking, IntPtr utils, IEnumerable<LobbyFilter> filters)
        {
            SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(matchmaking, DistanceWorldwide);
            foreach (var f in filters)
            {
                SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(matchmaking, Utf8(f.Key), Utf8(f.Value), (int)f.Comparison);
            }

            var call = SteamAPI_ISteamMatchmaking_RequestLobbyList(matchmaking);
            var clock = Stopwatch.StartNew();
            bool failed;
            while (!SteamAPI_ISteamUtils_IsAPICallCompleted(utils, call, out failed))
            {
                if (clock.Elapsed > RequestTimeout)
                {
                    return null;
                }

                Thread.Sleep(15);
            }

            if (failed || !SteamAPI_ISteamUtils_GetAPICallResult(utils, call, out var count, sizeof(uint), LobbyMatchListCallback, out failed) || failed)
            {
                return null;
            }

            return count;
        }

        private static void RunOne(int index, LobbyQuery query, IntPtr matchmaking, IntPtr utils, Pinger ping, TextWriter output)
        {
            var count = RequestLobbies(matchmaking, utils, query.Filters);
            if (count == null)
            {
                output.WriteLine(Protocol.EncodeFailed(index));
                return;
            }

            for (var i = 0; i < (int)count.Value; i++)
            {
                var lobby = SteamAPI_ISteamMatchmaking_GetLobbyByIndex(matchmaking, i);
                string Data(string key) => FromUtf8(SteamAPI_ISteamMatchmaking_GetLobbyData(matchmaking, lobby, Utf8(key)));
                var name = Data(LobbyQuery.NameKey);
                var pingData = Data(PingDataKey);
                var server = new Server
                {
                    Id = lobby.ToString(CultureInfo.InvariantCulture),
                    Owner = Data(LobbyQuery.OwnerKey),
                    Name = name.Length > 0 ? name : Data(LegacyNameKey),
                    Mode = Data(LobbyQuery.ModeKey),
                    Map = Data(LobbyQuery.MapKey),
                    Bots = Data(LobbyQuery.BotsKey),
                    Region = Catalog.RegionFromPing(pingData),
                    Limit = SteamAPI_ISteamMatchmaking_GetLobbyMemberLimit(matchmaking, lobby),
                    Ping = ping.Estimate(lobby, pingData),
                };
                // Host-set data: see Server.IsSteamId.
                if (Server.IsSteamId(server.Owner))
                {
                    output.WriteLine(Protocol.EncodeServer(index, server));
                }
            }

            output.WriteLine(Protocol.EncodeDone(index, (int)count.Value));
        }

        /// <summary>Estimates pings from relay locations; degrades to "unknown" if Steam's networking is unavailable.</summary>
        private sealed class Pinger
        {
            private readonly IntPtr net;
            private readonly Dictionary<ulong, string> pending = new Dictionary<ulong, string>();
            private byte[] mine;

            private Pinger(IntPtr net) => this.net = net;

            public static Pinger Start(string knownLocation)
            {
                IntPtr net;
                try
                {
                    net = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
                }
                catch (EntryPointNotFoundException)
                {
                    return new Pinger(IntPtr.Zero);
                }

                var p = new Pinger(net);
                if (net == IntPtr.Zero)
                {
                    return p;
                }

                var location = new byte[PingLocationSize];
                if (!string.IsNullOrEmpty(knownLocation) && SteamAPI_ISteamNetworkingUtils_ParsePingLocationString(net, Utf8(knownLocation), location))
                {
                    p.mine = location;
                }
                else
                {
                    // Starts measuring in the background while the lobby requests run.
                    SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(net);
                }

                return p;
            }

            public int Estimate(ulong server, string pingData)
            {
                if (net == IntPtr.Zero || string.IsNullOrEmpty(pingData))
                {
                    return Server.NoPing;
                }

                if (mine == null)
                {
                    pending[server] = pingData;
                    return Server.NoPing;
                }

                return Between(mine, pingData);
            }

            /// <summary>If the location had to be measured, waits for it, then reports it and the deferred pings.</summary>
            public void Finish(TextWriter output)
            {
                if (net == IntPtr.Zero || mine != null || pending.Count == 0)
                {
                    return;
                }

                var location = new byte[PingLocationSize];
                var clock = Stopwatch.StartNew();
                while (SteamAPI_ISteamNetworkingUtils_GetLocalPingLocation(net, location) < 0)
                {
                    if (clock.Elapsed > LocationTimeout)
                    {
                        return;
                    }

                    SteamAPI_RunCallbacks();
                    Thread.Sleep(50);
                }

                var text = new byte[1024];
                SteamAPI_ISteamNetworkingUtils_ConvertPingLocationToString(net, location, text, text.Length);
                output.WriteLine(Protocol.EncodeLocation(FromUtf8(text)));
                foreach (var p in pending)
                {
                    output.WriteLine(Protocol.EncodePing(p.Key.ToString(CultureInfo.InvariantCulture), Between(location, p.Value)));
                }
            }

            private int Between(byte[] location, string pingData)
            {
                var host = new byte[PingLocationSize];
                if (!SteamAPI_ISteamNetworkingUtils_ParsePingLocationString(net, Utf8(pingData), host))
                {
                    return Server.NoPing;
                }

                var ms = SteamAPI_ISteamNetworkingUtils_EstimatePingTimeBetweenTwoLocations(net, location, host);
                return ms >= 0 ? ms : Server.NoPing;
            }
        }
    }
}
