using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Dispatch.Model
{
    /// <summary>
    /// Estimates when a server was created from its lobby ID. Steam numbers lobbies from one counter
    /// shared by every game, which rises steadily (about 46,000 a minute in late 2026). Each refresh
    /// adds an anchor: the newest lobby seen was created moments ago. The rate is measured from the
    /// anchors, so the estimate tracks Steam's actual pace.
    /// </summary>
    internal sealed class LobbyAge
    {
        public const double DefaultRatePerMinute = 46300;
        private const int MaxAnchors = 200;
        private static readonly TimeSpan AnchorLifetime = TimeSpan.FromHours(24);
        private static readonly TimeSpan RateWindow = TimeSpan.FromHours(12);
        private static readonly TimeSpan MinRateSpan = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ProjectionWindow = TimeSpan.FromHours(3);

        /// <summary>A refresh needs this many servers for its newest lobby to be a fair "created just now" anchor.</summary>
        public const int MinServersForAnchor = 20;

        private readonly List<Anchor> anchors = new List<Anchor>();

        public static uint CounterOf(string lobbyId) =>
            ulong.TryParse(lobbyId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? (uint)(id & 0xFFFFFFFF) : 0;

        /// <summary>Records that the newest lobby in a complete refresh had this counter.</summary>
        public void AddAnchor(uint newestCounter, DateTime nowUtc)
        {
            if (newestCounter == 0)
            {
                return;
            }

            anchors.Add(new Anchor(newestCounter, nowUtc));
            anchors.RemoveAll(a => nowUtc - a.Utc > AnchorLifetime);
            if (anchors.Count > MaxAnchors)
            {
                anchors.RemoveRange(0, anchors.Count - MaxAnchors);
            }
        }

        public double RatePerMinute(DateTime nowUtc)
        {
            // From the earliest anchor to the most advanced later one: a stale anchor has a low counter, so it never wins.
            var recent = anchors.Where(a => nowUtc - a.Utc <= RateWindow).OrderBy(a => a.Utc).ToList();
            if (recent.Count >= 2)
            {
                var first = recent[0];
                var best = recent.Where(a => a.Utc - first.Utc >= MinRateSpan)
                    .OrderByDescending(a => a.Counter - (DefaultRatePerMinute * (a.Utc - first.Utc).TotalMinutes))
                    .FirstOrDefault();
                if (best != null && best.Counter > first.Counter)
                {
                    return (best.Counter - first.Counter) / (best.Utc - first.Utc).TotalMinutes;
                }
            }

            return DefaultRatePerMinute;
        }

        /// <summary>Estimated age in minutes, or null before the first anchor.</summary>
        public double? AgeMinutes(uint counter, DateTime nowUtc)
        {
            if (counter == 0 || anchors.Count == 0)
            {
                return null;
            }

            // Each anchor's newest lobby may be a little old, so it understates "now": take the most advanced projection.
            var rate = RatePerMinute(nowUtc);
            var recent = anchors.Where(a => nowUtc - a.Utc <= ProjectionWindow).ToList();
            if (recent.Count == 0)
            {
                recent = new List<Anchor> { anchors.OrderBy(a => a.Utc).Last() };
            }

            var counterNow = recent.Max(a => a.Counter + (rate * (nowUtc - a.Utc).TotalMinutes));
            return Math.Max(0, (counterNow - counter) / rate);
        }

        public IEnumerable<string[]> ToRecords() =>
            anchors.Select(a => new[] { a.Counter.ToString(CultureInfo.InvariantCulture), a.Utc.Ticks.ToString(CultureInfo.InvariantCulture) });

        public void Load(IEnumerable<string[]> records, DateTime nowUtc)
        {
            anchors.Clear();
            foreach (var r in records.Where(r => r.Length >= 2))
            {
                if (uint.TryParse(r[0], NumberStyles.None, CultureInfo.InvariantCulture, out var counter) &&
                    long.TryParse(r[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                    ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                {
                    var utc = new DateTime(ticks, DateTimeKind.Utc);
                    if (nowUtc - utc <= AnchorLifetime)
                    {
                        anchors.Add(new Anchor(counter, utc));
                    }
                }
            }
        }

        /// <summary>"7 min", "1 h 05".</summary>
        public static string Format(double minutes)
        {
            if (minutes < 0)
            {
                return "—";
            }

            if (minutes < 1)
            {
                return "<1 min";
            }

            if (minutes < 60)
            {
                return ((int)minutes).ToString(CultureInfo.InvariantCulture) + " min";
            }

            var total = (int)minutes;
            return (total / 60).ToString(CultureInfo.InvariantCulture) + " h " + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private sealed class Anchor
        {
            public Anchor(uint counter, DateTime utc)
            {
                Counter = counter;
                Utc = utc;
            }

            public uint Counter { get; }
            public DateTime Utc { get; }
        }
    }
}
