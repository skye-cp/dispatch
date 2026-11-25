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

        // Bounds on the measured rate. Steam's pace drifts slowly; a measurement far outside this band comes from an
        // anchor whose "newest" lobby was minutes old (a quiet period) or from a clock change, so it is clamped.
        private const double MinRateFactor = 0.6;
        private const double MaxRateFactor = 1.6;

        /// <summary>A refresh needs this many servers for its newest lobby to be a fair "created just now" anchor.</summary>
        public const int MinServersForAnchor = 20;

        private readonly List<Anchor> anchors = new List<Anchor>();

        public static uint CounterOf(string lobbyId) =>
            ulong.TryParse(lobbyId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? CounterOf(id) : 0;

        public static uint CounterOf(ulong lobbyId) => (uint)(lobbyId & 0xFFFFFFFF);

        /// <summary>
        /// How far counter a is ahead of b. The counter is 32 bits and wraps about every 64 days, so it is
        /// compared as a signed difference, which is right for any two counters less than a month apart.
        /// </summary>
        public static int Delta(uint a, uint b) => unchecked((int)(a - b));

        /// <summary>The newer of two counters, across a wrap.</summary>
        public static uint Newer(uint a, uint b) => a == 0 ? b : b == 0 ? a : Delta(a, b) >= 0 ? a : b;

        /// <summary>Records that the newest lobby in a complete refresh had this counter.</summary>
        public void AddAnchor(uint newestCounter, DateTime nowUtc)
        {
            if (newestCounter == 0)
            {
                return;
            }

            // Expired anchors go first, so one left from long ago (a hibernated PC, a counter wrap since) can't block
            // every new one. Then a counter behind the latest anchor is not "created just now": a repeated old sample
            // would skew the rate.
            Prune(nowUtc);
            var latest = anchors.OrderBy(a => a.Utc).LastOrDefault();
            if (latest != null && Delta(newestCounter, latest.Counter) <= 0)
            {
                return;
            }

            anchors.Add(new Anchor(newestCounter, nowUtc));
            if (anchors.Count > MaxAnchors)
            {
                anchors.RemoveRange(0, anchors.Count - MaxAnchors);
            }
        }

        /// <summary>Drops anchors past their lifetime, and any stamped in the future (the clock was set back).</summary>
        private void Prune(DateTime nowUtc) =>
            anchors.RemoveAll(a => nowUtc - a.Utc > AnchorLifetime || a.Utc - nowUtc > TimeSpan.FromMinutes(1));

        public double RatePerMinute(DateTime nowUtc)
        {
            // From the earliest anchor to the most advanced later one: a stale anchor has a low counter, so it never wins.
            var recent = anchors.Where(a => nowUtc - a.Utc <= RateWindow).OrderBy(a => a.Utc).ToList();
            if (recent.Count >= 2)
            {
                var first = recent[0];
                var best = recent.Where(a => a.Utc - first.Utc >= MinRateSpan)
                    .OrderByDescending(a => Delta(a.Counter, first.Counter) - (DefaultRatePerMinute * (a.Utc - first.Utc).TotalMinutes))
                    .FirstOrDefault();
                if (best != null && Delta(best.Counter, first.Counter) > 0)
                {
                    var rate = Delta(best.Counter, first.Counter) / (best.Utc - first.Utc).TotalMinutes;
                    return Math.Max(DefaultRatePerMinute * MinRateFactor, Math.Min(DefaultRatePerMinute * MaxRateFactor, rate));
                }
            }

            return DefaultRatePerMinute;
        }

        /// <summary>Estimated age in minutes, or null before the first anchor.</summary>
        public double? AgeMinutes(uint counter, DateTime nowUtc)
        {
            Prune(nowUtc);
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

            // Measured relative to this server's counter, so a wrap between the two does not matter.
            var aheadNow = recent.Max(a => Delta(a.Counter, counter) + (rate * (nowUtc - a.Utc).TotalMinutes));
            return Math.Max(0, aheadNow / rate);
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
