using System;
using System.Collections.Generic;
using System.Linq;

namespace Dispatch.Model
{
    /// <summary>
    /// Knows which hosts are hosting right now, from host checks and recent list answers, and decides when a
    /// followed host's server counts as appearing. No UI, no Steam: the window asks Steam and records answers.
    /// </summary>
    internal sealed class HostWatch
    {
        /// <summary>A host check is trusted this long: a bit over two watch intervals, so a watched host is never "unknown".</summary>
        public static readonly TimeSpan CheckFor = TimeSpan.FromMinutes(2);

        /// <summary>A followed host must be gone this long before coming back alerts again, so a map change or a quick re-host stays quiet.</summary>
        public static readonly TimeSpan GoneAfter = TimeSpan.FromMinutes(3);

        /// <summary>Host checks per lookup run: each is one Steam request, about a third of a second.</summary>
        public const int MaxChecks = 10;

        private readonly Dictionary<string, Server> found = new Dictionary<string, Server>();
        private readonly Dictionary<string, DateTime> checkedUtc = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> seenUtc = new Dictionary<string, DateTime>();

        // Hosts a check found not hosting: only a host seen to be away can "appear" and alert.
        private readonly HashSet<string> away = new HashSet<string>();

        /// <summary>Every server a check has found, however old: for keeping followed hosts' names current.</summary>
        public IEnumerable<Server> Found => found.Values;

        /// <summary>Up to <see cref="MaxChecks"/> of these hosts, least recently checked first, so a longer list rotates.</summary>
        public List<string> NextToCheck(IEnumerable<string> owners) =>
            owners.Distinct()
                .OrderBy(o => checkedUtc.TryGetValue(o, out var at) ? at : DateTime.MinValue)
                .Take(MaxChecks)
                .ToList();

        /// <summary>Records a check: the hosts asked about, and the servers found for them.</summary>
        public void Record(IEnumerable<string> asked, IReadOnlyDictionary<string, Server> answers, DateTime nowUtc)
        {
            foreach (var owner in asked)
            {
                checkedUtc[owner] = nowUtc;
                if (answers.TryGetValue(owner, out var s))
                {
                    found[owner] = s;
                }
                else
                {
                    found.Remove(owner);
                    away.Add(owner);
                }
            }
        }

        private bool CheckedRecently(string owner, DateTime nowUtc) =>
            checkedUtc.TryGetValue(owner, out var at) && nowUtc - at < CheckFor;

        /// <summary>The host's server if known to be up: a recent check wins, else the current list answers.</summary>
        public Server HostingNow(string owner, IEnumerable<Server> current, DateTime nowUtc)
        {
            if (CheckedRecently(owner, nowUtc))
            {
                return found.TryGetValue(owner, out var s) ? s : null;
            }

            return current.FirstOrDefault(s => s.Owner == owner);
        }

        public bool KnownNotHosting(string owner, DateTime nowUtc) =>
            CheckedRecently(owner, nowUtc) && !found.ContainsKey(owner);

        /// <summary>True when nothing recent says whether the host is up.</summary>
        public bool NeedsCheck(string owner, IEnumerable<Server> current, DateTime nowUtc) =>
            HostingNow(owner, current, nowUtc) == null && !KnownNotHosting(owner, nowUtc);

        /// <summary>Servers known to be up right now: the current list answers plus recent checks.</summary>
        public List<Server> Online(IEnumerable<Server> current, DateTime nowUtc) =>
            current.Concat(found.Where(p => CheckedRecently(p.Key, nowUtc)).Select(p => p.Value)).ToList();

        /// <summary>A menu's detail line for a host.</summary>
        public string Detail(string owner, IEnumerable<Server> current, string fallback, DateTime nowUtc)
        {
            var s = HostingNow(owner, current, nowUtc);
            return s != null ? "Hosting now  ·  " + s.Mode + " · " + s.MapName
                : KnownNotHosting(owner, nowUtc) ? "Not hosting now"
                : fallback;
        }

        /// <summary>A host followed while online: no alert for the server they are already on.</summary>
        public void MarkSeen(string owner, DateTime nowUtc)
        {
            seenUtc[owner] = nowUtc;
            away.Remove(owner);
        }

        /// <summary>
        /// Followed hosts whose server counts as appearing: gone for a while since last seen, or found away by a
        /// check. A host simply noticed for the first time (online at start, or outside the filters) doesn't count.
        /// Marks every online followed host as seen.
        /// </summary>
        public List<Server> Appeared(IEnumerable<Server> online, Func<string, bool> followed, DateTime nowUtc)
        {
            var hosts = online.Where(s => followed(s.Owner)).GroupBy(s => s.Owner).Select(g => g.First()).ToList();
            var appeared = hosts.Where(s => seenUtc.TryGetValue(s.Owner, out var last)
                ? nowUtc - last > GoneAfter
                : away.Contains(s.Owner)).ToList();
            foreach (var s in hosts)
            {
                MarkSeen(s.Owner, nowUtc);
            }

            return appeared;
        }
    }
}
