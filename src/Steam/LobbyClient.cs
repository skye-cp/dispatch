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

        // One lookup process at a time: each one signs in to Steam as the game, and the shared state below is per run.
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private string steamApi;

        /// <summary>The player's position on Steam's relay network, as Steam's own text form. Local only.</summary>
        public string Location { get; set; }

        public DateTime LocationUtc { get; set; }

        private bool LocationFresh => !string.IsNullOrEmpty(Location) && DateTime.UtcNow - LocationUtc < LocationMaxAge;

        /// <summary>The player's Steam friends (SteamID → name) from the latest lookup. Memory only.</summary>
        public IReadOnlyDictionary<string, string> Friends { get; private set; } = new Dictionary<string, string>();

        /// <summary>Highest lobby counter from the latest run's age sample, or 0 when that run took none. See <see cref="LobbyAge"/>.</summary>
        public uint NewestCounter { get; private set; }

        /// <param name="onAnswer">Called on a worker thread with each request's index and servers.</param>
        /// <param name="onPings">Called on a worker thread with pings measured after the servers were listed.</param>
        /// <param name="sampleAge">Also take one unfiltered sample of the newest servers, for age estimates.</param>
        public async Task RunAsync(IReadOnlyList<LobbyQuery> queries, Action<int, List<Server>> onAnswer,
            Action<Dictionary<string, int>> onPings, CancellationToken token, bool sampleAge = false)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Task.Run(() => Run(queries, onAnswer, onPings, token, sampleAge), token).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<List<Server>> FetchAsync(LobbyQuery query, CancellationToken token)
        {
            var result = new List<Server>();
            await RunAsync(new[] { query }, (_, servers) => result = servers, null, token).ConfigureAwait(false);
            return result;
        }

        private void Run(IReadOnlyList<LobbyQuery> queries, Action<int, List<Server>> onAnswer,
            Action<Dictionary<string, int>> onPings, CancellationToken token, bool sampleAge)
        {
            NewestCounter = 0;
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

                var timedOut = false;
                using (token.Register(() => Kill(process)))
                using (new Timer(_ =>
                {
                    timedOut = true;
                    Kill(process);
                }, null, Timeout, System.Threading.Timeout.InfiniteTimeSpan))
                {
                    try
                    {
                        WriteRequests(process, queries, sampleAge);
                        var stderr = process.StandardError.ReadToEndAsync();
                        var output = ReadOutput(process.StandardOutput, onAnswer);
                        if (output.Location != null)
                        {
                            Location = output.Location;
                            LocationUtc = DateTime.UtcNow;
                        }

                        NewestCounter = output.NewestCounter;
                        if (output.Friends.Count > 0)
                        {
                            Friends = output.Friends;
                        }

                        if (output.Pings.Count > 0)
                        {
                            onPings?.Invoke(output.Pings);
                        }

                        process.WaitForExit();
                        token.ThrowIfCancellationRequested();
                        if (output.Error != null)
                        {
                            throw new LookupException(output.Error);
                        }

                        if (timedOut)
                        {
                            throw new LookupException("Steam took too long to answer, so the list may be incomplete. Try again in a moment.");
                        }

                        if (output.Answered == 0 && queries.Count > 0)
                        {
                            var detail = stderr.Wait(500) ? stderr.Result.Trim() : "";
                            throw new LookupException("Steam did not answer. Check that Steam is running and online." +
                                (detail.Length > 0 ? " (" + detail.Split('\n')[0].Trim() + ")" : ""));
                        }
                    }
                    catch (System.IO.IOException e)
                    {
                        throw new LookupException("The Steam lookup stopped unexpectedly: " + e.Message);
                    }
                    finally
                    {
                        // Whatever happened, the lookup process does not outlive the request.
                        Kill(process);
                    }
                }
            }
        }

        private void WriteRequests(Process process, IReadOnlyList<LobbyQuery> queries, bool sampleAge)
        {
            try
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
            }
            catch (System.IO.IOException)
            {
                // The lookup process ended before reading its requests; its output (or silence) says why.
            }
        }

        /// <summary>
        /// Reads the lookup process's records to the end. Each request's servers go to
        /// <paramref name="onAnswer"/> as soon as that request is done; everything else comes back in the result.
        /// </summary>
        internal static LookupOutput ReadOutput(System.IO.TextReader stdout, Action<int, List<Server>> onAnswer)
        {
            var result = new LookupOutput();
            var pending = new List<Server>();
            string line;
            while ((line = stdout.ReadLine()) != null)
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
                        result.Answered++;
                        onAnswer(Index(f), pending);
                        pending = new List<Server>();
                        break;
                    case Protocol.FailedRecord:
                        pending = new List<Server>();
                        break;
                    case Protocol.ErrorRecord:
                        result.Error = f.Length > 2 ? f[2] : "Steam lookup failed.";
                        break;
                    case Protocol.LocationRecord:
                        if (f.Length > 1 && f[1].Length > 0)
                        {
                            result.Location = f[1];
                        }

                        break;
                    case Protocol.AnchorRecord:
                        result.NewestCounter = Protocol.DecodeAnchor(f);
                        break;
                    case Protocol.FriendRecord:
                        if (Protocol.TryDecodeFriend(f, out var friendId, out var friendName))
                        {
                            result.Friends[friendId] = friendName;
                        }

                        break;
                    case Protocol.PingRecord:
                        if (Protocol.TryDecodePing(f, out var lobby, out var ms))
                        {
                            result.Pings[lobby] = ms;
                        }

                        break;
                }
            }

            return result;
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

    /// <summary>What one lookup process reported, apart from the servers (which arrive as each request finishes).</summary>
    internal sealed class LookupOutput
    {
        public int Answered { get; set; }

        /// <summary>The lookup's own error message, if it reported one.</summary>
        public string Error { get; set; }

        /// <summary>A newly measured relay location, if one was measured.</summary>
        public string Location { get; set; }

        /// <summary>The age sample's newest lobby counter, or 0 when no sample was taken.</summary>
        public uint NewestCounter { get; set; }

        public Dictionary<string, string> Friends { get; } = new Dictionary<string, string>();

        public Dictionary<string, int> Pings { get; } = new Dictionary<string, int>();
    }

    internal class LookupException : Exception
    {
        public LookupException(string message)
            : base(message)
        {
        }
    }
}
