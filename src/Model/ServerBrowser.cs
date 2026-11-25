using System;
using System.Collections.Generic;
using System.Linq;

namespace Dispatch.Model
{
    internal enum SortKey
    {
        Nearest,
        Name,
        Mode,
        Map,
        Region,
        Bots,
        Slots,
    }

    /// <summary>Caches Steam answers per request and builds the visible list. No UI, no Steam.</summary>
    internal sealed class ServerBrowser
    {
        private readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();

        public FilterState Filters { get; } = new FilterState();

        public TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(90);

        public List<LobbyQuery> Plan() => QueryPlanner.Plan(Filters);

        /// <summary>Requests of the current plan that have no fresh answer.</summary>
        public List<LobbyQuery> Missing(DateTime nowUtc) =>
            Plan().Where(q => !cache.TryGetValue(q.Key, out var e) || nowUtc - e.FetchedUtc > MaxAge).ToList();

        public void Store(LobbyQuery query, IReadOnlyList<Server> servers, DateTime nowUtc) =>
            cache[query.Key] = new Entry(servers, nowUtc);

        public void Invalidate() => cache.Clear();

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

        public List<Server> Visible(SortKey key, bool descending)
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
                default:
                    sorted = descending ? list.OrderByDescending(s => s.Rank) : list.OrderBy(s => s.Rank);
                    break;
            }

            return sorted.ThenBy(s => s.Rank).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Region codes of every server Steam has returned, busiest first, so region chips stay put.</summary>
        public List<KeyValuePair<string, int>> Regions() =>
            cache.Values.SelectMany(e => e.Servers)
                .GroupBy(s => s.Id).Select(g => g.First())
                .Where(s => s.Region.Length > 0)
                .GroupBy(s => s.Region, StringComparer.OrdinalIgnoreCase)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
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
