using System;
using System.Collections.Generic;
using System.Linq;

namespace Dispatch.Model
{
    internal enum SortKey
    {
        Ping,
        Age,
        Nearest,
        Name,
        Mode,
        Map,
        Region,
        Bots,
        Slots,
    }

    internal sealed class RegionInfo
    {
        public RegionInfo(string code, int count, int ping)
        {
            Code = code;
            Count = count;
            Ping = ping;
        }

        public string Code { get; }
        public int Count { get; }

        /// <summary>Lowest known ping to a host in the region, or <see cref="Server.NoPing"/>.</summary>
        public int Ping { get; }
    }

    /// <summary>Caches Steam answers per request and builds the visible list. No UI, no Steam.</summary>
    internal sealed class ServerBrowser
    {
        private readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();
        private readonly Dictionary<string, DateTime> firstSeen = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, string> lastState = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> roundStart = new Dictionary<string, DateTime>();

        public FilterState Filters { get; } = new FilterState();

        public TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(90);

        public List<LobbyQuery> Plan() => QueryPlanner.Plan(Filters);

        /// <summary>Requests of the current plan that have no fresh answer.</summary>
        public List<LobbyQuery> Missing(DateTime nowUtc) =>
            Plan().Where(q => !cache.TryGetValue(q.Key, out var e) || nowUtc - e.FetchedUtc > MaxAge).ToList();

        public void Store(LobbyQuery query, IReadOnlyList<Server> servers, DateTime nowUtc)
        {
            cache[query.Key] = new Entry(servers, nowUtc);
            foreach (var s in servers)
            {
                if (!firstSeen.ContainsKey(s.Id))
                {
                    firstSeen[s.Id] = nowUtc;
                }

                // A host keeps their lobby across rounds; a new map or mode means a new round began.
                var state = s.Mode + "|" + s.Map;
                if (lastState.TryGetValue(s.Id, out var before) && before != state)
                {
                    roundStart[s.Id] = nowUtc;
                }

                lastState[s.Id] = state;
            }
        }

        /// <summary>When a new round was last seen to start on this server, if one was.</summary>
        public DateTime RoundStartUtc(string id) => roundStart.TryGetValue(id, out var t) ? t : DateTime.MinValue;

        /// <summary>The highest lobby counter among the given servers (the newest lobby), or 0.</summary>
        public static uint NewestCounter(IEnumerable<Server> servers) =>
            servers.Select(s => LobbyAge.CounterOf(s.Id)).DefaultIfEmpty(0u).Max();

        public void Invalidate() => cache.Clear();

        /// <summary>Fills in pings measured after the servers were listed.</summary>
        public void ApplyPings(IReadOnlyDictionary<string, int> pings)
        {
            foreach (var s in cache.Values.SelectMany(e => e.Servers))
            {
                if (pings.TryGetValue(s.Id, out var ms))
                {
                    s.Ping = ms;
                }
            }
        }

        /// <summary>True while some listed server has no ping yet.</summary>
        public bool HasUnknownPings() => Pool().Any(s => s.Ping < 0);

        /// <summary>Oldest answer behind the current plan, if every request has one.</summary>
        public DateTime? UpdatedUtc()
        {
            var times = new List<DateTime>();
            foreach (var q in Plan())
            {
                if (!cache.TryGetValue(q.Key, out var e))
                {
                    return null;
                }

                times.Add(e.FetchedUtc);
            }

            return times.Count == 0 ? (DateTime?)null : times.Min();
        }

        /// <summary>All servers behind the current plan, before the on-screen filters.</summary>
        public List<Server> Pool()
        {
            var byId = new Dictionary<string, Server>();
            foreach (var q in Plan())
            {
                if (!cache.TryGetValue(q.Key, out var e))
                {
                    continue;
                }

                foreach (var s in e.Servers)
                {
                    if (!byId.TryGetValue(s.Id, out var known) || s.Rank < known.Rank)
                    {
                        byId[s.Id] = s;
                    }
                }
            }

            return byId.Values.ToList();
        }

        /// <summary>Every server Steam has returned and is still cached, whatever the filters.</summary>
        public List<Server> All() =>
            cache.Values.SelectMany(e => e.Servers).GroupBy(s => s.Id).Select(g => g.First()).ToList();

        /// <summary>When each server was first returned this session, for highlighting new arrivals.</summary>
        public DateTime FirstSeenUtc(string id) => firstSeen.TryGetValue(id, out var t) ? t : DateTime.MinValue;

        /// <summary>The visible list. Friends' and followed servers stay on top whatever the sort.</summary>
        public List<Server> Visible(SortKey key, bool descending)
        {
            var sorted = SortedVisible(key, descending);
            return sorted.Where(s => s.IsPinned).Concat(sorted.Where(s => !s.IsPinned)).ToList();
        }

        private List<Server> SortedVisible(SortKey key, bool descending)
        {
            var list = Pool().Where(Filters.Matches);
            IOrderedEnumerable<Server> sorted;
            switch (key)
            {
                case SortKey.Name:
                    sorted = Order(list, s => s.Name, descending, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case SortKey.Mode:
                    sorted = Order(list, s => s.Mode, descending, StringComparer.OrdinalIgnoreCase);
                    break;
                case SortKey.Map:
                    sorted = Order(list, s => s.MapName, descending, StringComparer.OrdinalIgnoreCase);
                    break;
                case SortKey.Region:
                    sorted = Order(list, s => s.RegionName, descending, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case SortKey.Bots:
                    sorted = Order(list, s => s.Bots, descending, StringComparer.OrdinalIgnoreCase);
                    break;
                case SortKey.Slots:
                    sorted = descending ? list.OrderByDescending(s => s.Limit) : list.OrderBy(s => s.Limit);
                    break;
                case SortKey.Age:
                    // Unknown ages always go last.
                    sorted = descending
                        ? list.OrderBy(s => s.AgeMinutes < 0).ThenByDescending(s => s.AgeMinutes)
                        : list.OrderBy(s => s.AgeMinutes < 0).ThenBy(s => s.AgeMinutes);
                    break;
                case SortKey.Ping:
                    // Unknown pings always go last, whichever the direction.
                    sorted = descending
                        ? list.OrderBy(s => s.Ping < 0).ThenByDescending(s => s.Ping)
                        : list.OrderBy(s => s.Ping < 0).ThenBy(s => s.Ping);
                    break;
                default:
                    sorted = descending ? list.OrderByDescending(s => s.Rank) : list.OrderBy(s => s.Rank);
                    break;
            }

            return sorted.ThenBy(s => s.Rank).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Regions of every server Steam has returned (not just the filtered ones, so chips stay put),
        /// nearest first once pings are known, otherwise busiest first.
        /// </summary>
        public List<RegionInfo> Regions() =>
            cache.Values.SelectMany(e => e.Servers)
                .GroupBy(s => s.Id).Select(g => g.First())
                .Where(s => s.Region.Length > 0)
                .GroupBy(s => s.Region, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RegionInfo(g.Key, g.Count(), g.Where(s => s.Ping >= 0).Select(s => s.Ping).DefaultIfEmpty(Server.NoPing).Min()))
                .OrderBy(r => r.Ping < 0)
                .ThenBy(r => r.Ping)
                .ThenByDescending(r => r.Count)
                .ThenBy(r => r.Code, StringComparer.Ordinal)
                .ToList();

        private static IOrderedEnumerable<Server> Order(IEnumerable<Server> list, Func<Server, string> by, bool descending, IComparer<string> cmp) =>
            descending ? list.OrderByDescending(by, cmp) : list.OrderBy(by, cmp);

        private sealed class Entry
        {
            public Entry(IReadOnlyList<Server> servers, DateTime fetchedUtc)
            {
                Servers = servers;
                FetchedUtc = fetchedUtc;
            }

            public IReadOnlyList<Server> Servers { get; }
            public DateTime FetchedUtc { get; }
        }
    }
}
