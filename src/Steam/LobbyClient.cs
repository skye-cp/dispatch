using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dispatch.Model;

namespace Dispatch.Steam
{
    /// <summary>
    /// Window side of a lookup: starts Dispatch again in lookup mode, sends the requests and
    /// streams back each answer as it completes. The window process never loads Steam's library.
    /// </summary>
    internal sealed class LobbyClient
    {
        public const string WorkerFlag = "--lookup";

        /// <summary>How long a measured relay location is reused before measuring again.</summary>
        public static readonly TimeSpan LocationMaxAge = TimeSpan.FromHours(2);

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private string steamApi;

        /// <summary>The player's position on Steam's relay network, as Steam's own text form. Local only.</summary>
        public string Location { get; set; }

        public DateTime LocationUtc { get; set; }

        private bool LocationFresh => !string.IsNullOrEmpty(Location) && DateTime.UtcNow - LocationUtc < LocationMaxAge;

        /// <summary>The player's Steam friends (SteamID → name) from the latest lookup. Memory only.</summary>
        public IReadOnlyDictionary<string, string> Friends { get; private set; } = new Dictionary<string, string>();

        /// <summary>Highest lobby counter from the latest age sample, or 0. See <see cref="LobbyAge"/>.</summary>
        public uint NewestCounter { get; private set; }

        /// <param name="onAnswer">Called on a worker thread with each request's index and servers.</param>
        /// <param name="onPings">Called on a worker thread with pings measured after the servers were listed.</param>
        /// <param name="sampleAge">Also take one unfiltered sample of the newest servers, for age estimates.</param>
        public Task RunAsync(IReadOnlyList<LobbyQuery> queries, Action<int, List<Server>> onAnswer,
            Action<Dictionary<string, int>> onPings, CancellationToken token, bool sampleAge = false) =>
            Task.Run(() => Run(queries, onAnswer, onPings, token, sampleAge), token);

        public async Task<List<Server>> FetchAsync(LobbyQuery query, CancellationToken token)
        {
            var result = new List<Server>();
            await RunAsync(new[] { query }, (_, servers) => result = servers, null, token).ConfigureAwait(false);
            return result;
        }

        private void Run(IReadOnlyList<LobbyQuery> queries, Action<int, List<Server>> onAnswer,
            Action<Dictionary<string, int>> onPings, CancellationToken token, bool sampleAge)
        {
            if (steamApi == null)
            {
                steamApi = GameLocator.PrepareSteamApi();
            }

            var info = new ProcessStartInfo
            {
                FileName = Assembly.GetEntryAssembly().Location,
                Arguments = WorkerFlag + " \"" + steamApi + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            info.EnvironmentVariables["SteamAppId"] = Catalog.AppId;
            info.EnvironmentVariables["SteamGameId"] = Catalog.AppId;

            using (var process = new Process { StartInfo = info })
            {
                try
                {
                    process.Start();
                }
                catch (Win32Exception e)
                {
                    throw new LookupException("Could not start the Steam lookup: " + e.Message);
                }

                using (token.Register(() => Kill(process)))
                using (var timer = new Timer(_ => Kill(process), null, Timeout, System.Threading.Timeout.InfiniteTimeSpan))
                {
                    var stdin = new System.IO.StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n" };
                    if (LocationFresh)
                    {
                        stdin.WriteLine(Protocol.EncodeLocationHeader(Location));
                    }

                    if (sampleAge)
                    {
                        stdin.WriteLine(Protocol.AgeSampleHeader);
                    }

                    foreach (var q in queries)
                    {
                        stdin.WriteLine(Protocol.EncodeQuery(q));
                    }

                    stdin.Close();
                    process.StandardError.ReadToEndAsync();

                    var pending = new List<Server>();
                    var pings = new Dictionary<string, int>();
                    var friends = new Dictionary<string, string>();
                    string error = null;
                    var answered = 0;
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        var f = Protocol.Split(line);
                        switch (f[0])
                        {
                            case Protocol.ServerRecord:
                                var s = Protocol.DecodeServer(f, pending.Count, DateTime.UtcNow);
                                if (s != null)
                                {
                                    pending.Add(s);
                                }

                                break;
                            case Protocol.DoneRecord:
                                answered++;
                                onAnswer(Index(f), pending);
                                pending = new List<Server>();
                                break;
                            case Protocol.FailedRecord:
                                pending = new List<Server>();
                                break;
                            case Protocol.ErrorRecord:
                                error = f.Length > 2 ? f[2] : "Steam lookup failed.";
                                break;
                            case Protocol.LocationRecord:
                                if (f.Length > 1 && f[1].Length > 0)
                                {
                                    Location = f[1];
                                    LocationUtc = DateTime.UtcNow;
                                }

                                break;
                            case Protocol.AnchorRecord:
                                NewestCounter = Protocol.DecodeAnchor(f);
                                break;
                            case Protocol.FriendRecord:
                                if (Protocol.TryDecodeFriend(f, out var friendId, out var friendName))
                                {
                                    friends[friendId] = friendName;
                                }

                                break;
                            case Protocol.PingRecord:
                                if (Protocol.TryDecodePing(f, out var lobby, out var ms))
                                {
                                    pings[lobby] = ms;
                                }

                                break;
                        }
                    }

                    if (friends.Count > 0)
                    {
                        Friends = friends;
                    }

                    if (pings.Count > 0)
                    {
                        onPings?.Invoke(pings);
                    }

                    process.WaitForExit();
                    token.ThrowIfCancellationRequested();
                    if (error != null)
                    {
                        throw new LookupException(error);
                    }

                    if (answered == 0 && queries.Count > 0)
                    {
                        throw new LookupException("Steam did not answer. Check that Steam is running and online.");
                    }
                }
            }
        }

        private static int Index(string[] f) =>
            f.Length > 1 && int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;

        private static void Kill(Process p)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }
    }

    internal sealed class LookupException : Exception
    {
        public LookupException(string message)
            : base(message)
        {
        }
    }
}
