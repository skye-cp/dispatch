using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Dispatch.Model
{
    /// <summary>A host the player follows, remembered by Steam ID so renames don't lose it.</summary>
    internal sealed class FollowedHost
    {
        public FollowedHost(string owner, string name)
        {
            Owner = owner;
            Name = name;
        }

        public string Owner { get; }
        public string Name { get; set; }
    }

    /// <summary>Hosts the player follows; their servers are pinned to the top and announced when they appear.</summary>
    internal sealed class FollowList
    {
        private readonly List<FollowedHost> hosts = new List<FollowedHost>();

        public IReadOnlyList<FollowedHost> Hosts => hosts;

        public bool Contains(string owner) => hosts.Any(h => h.Owner == owner);

        public bool Toggle(string owner, string name)
        {
            var existing = hosts.FirstOrDefault(h => h.Owner == owner);
            if (existing != null)
            {
                hosts.Remove(existing);
                return false;
            }

            hosts.Add(new FollowedHost(owner, name));
            return true;
        }

        /// <summary>Keeps stored names current when hosts rename.</summary>
        public void Refresh(IEnumerable<Server> servers)
        {
            foreach (var s in servers)
            {
                var h = hosts.FirstOrDefault(x => x.Owner == s.Owner);
                if (h != null && s.Name.Length > 0)
                {
                    h.Name = s.Name;
                }
            }
        }

        public IEnumerable<string[]> ToRecords() => hosts.Select(h => new[] { h.Owner, h.Name });

        public void Load(IEnumerable<string[]> records)
        {
            hosts.Clear();
            foreach (var r in records.Where(r => r.Length >= 2 && r[0].Length > 0))
            {
                if (!Contains(r[0]))
                {
                    hosts.Add(new FollowedHost(r[0], r[1]));
                }
            }
        }
    }

    internal sealed class RecentServer
    {
        public string Lobby { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Name { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Map { get; set; } = "";
        public DateTime JoinedUtc { get; set; }

        public string Link => JoinLink.Format(Lobby, Owner);
    }

    /// <summary>The last servers the player joined, newest first, one per host.</summary>
    internal sealed class RecentList
    {
        public const int Capacity = 8;
        private readonly List<RecentServer> items = new List<RecentServer>();

        public IReadOnlyList<RecentServer> Items => items;

        public void Add(Server s, DateTime nowUtc)
        {
            items.RemoveAll(r => r.Owner == s.Owner);
            items.Insert(0, new RecentServer { Lobby = s.Id, Owner = s.Owner, Name = s.Name, Mode = s.Mode, Map = s.Map, JoinedUtc = nowUtc });
            if (items.Count > Capacity)
            {
                items.RemoveRange(Capacity, items.Count - Capacity);
            }
        }

        public IEnumerable<string[]> ToRecords() =>
            items.Select(r => new[] { r.Lobby, r.Owner, r.Name, r.Mode, r.Map, r.JoinedUtc.Ticks.ToString(CultureInfo.InvariantCulture) });

        public void Load(IEnumerable<string[]> records)
        {
            items.Clear();
            foreach (var r in records.Where(r => r.Length >= 6))
            {
                long.TryParse(r[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks);
                items.Add(new RecentServer
                {
                    Lobby = r[0],
                    Owner = r[1],
                    Name = r[2],
                    Mode = r[3],
                    Map = r[4],
                    JoinedUtc = new DateTime(Math.Max(0, ticks), DateTimeKind.Utc),
                });
            }
        }
    }
}
