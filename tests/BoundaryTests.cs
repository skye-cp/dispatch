using System;
using System.Linq;
using Dispatch.Model;
using Xunit;

namespace Dispatch.Tests
{
    /// <summary>The edges of filters, the query plan, sorting and the age estimate.</summary>
    public class BoundaryTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        private const double Rate = LobbyAge.DefaultRatePerMinute;

        private static Server S(string id, string name = "alpha", int ping = 50, int limit = 4, double age = 10, int rank = 0) => new Server
        {
            Id = id,
            Owner = "2000000" + id,
            Name = name,
            Mode = "Zombie",
            Map = "Asylum",
            Bots = "true",
            Region = "gru",
            Ping = ping,
            Limit = limit,
            AgeMinutes = age,
            Rank = rank,
        };

        [Theory]
        [InlineData(100, true)]
        [InlineData(101, false)]
        [InlineData(-1, true)]
        public void PingLimitIncludesItsOwnValue(int ping, bool shown)
        {
            var f = new FilterState { MaxPing = 100 };
            Assert.Equal(shown, f.Matches(S("10001", ping: ping)));
        }

        [Theory]
        [InlineData(15, 15.0, true)]
        [InlineData(15, 15.01, false)]
        [InlineData(-60, 60.0, false)]
        [InlineData(-60, 60.01, true)]
        [InlineData(15, -1.0, true)]
        public void AgeLimitsIncludeTheirOwnValue(int limit, double age, bool shown)
        {
            Assert.Equal(shown, FilterState.AgeMatches(limit, age));
        }

        [Fact]
        public void SearchTextIsTrimmed() => Assert.True(new FilterState { Text = "  al " }.Matches(S("10001")));

        [Fact]
        public void ResetEndsAHostSearch()
        {
            var f = new FilterState { ExactHost = "someone" };
            f.Reset();
            Assert.False(f.IsHostSearch);
            Assert.Equal(Catalog.DefaultModes.Count, QueryPlanner.Plan(f).Count);
        }

        [Fact]
        public void ModesAreSummarisedInCatalogueOrderAndCitiesOnce()
        {
            var f = new FilterState();
            f.Modes.Add("Zombie");
            f.Modes.Add("Versus");
            f.Regions.Add("sto");
            f.Regions.Add("sto2");
            f.Regions.Add("gru");
            Assert.Equal("Versus, Zombie", FilterState.Summary(f.Selected(FilterKind.Mode)));
            Assert.Equal(new[] { "São Paulo", "Stockholm" }, f.Selected(FilterKind.Region));
        }

        [Fact]
        public void PlanSizesAtTheCap()
        {
            var f = new FilterState();
            foreach (var mode in Catalog.DefaultModes.Take(3))
            {
                f.Modes.Add(mode);
            }

            foreach (var map in Catalog.Maps.Take(10))
            {
                f.Maps.Add(map);
            }

            Assert.Equal(QueryPlanner.MaxQueries, QueryPlanner.Plan(f).Count);
            f.Maps.Add(Catalog.Maps[10]);
            Assert.Equal(3, QueryPlanner.Plan(f).Count);
        }

        [Fact]
        public void NoModeAsksPerModeAndMapUpToTheCapThenPerMap()
        {
            var f = new FilterState();
            foreach (var map in Catalog.Maps.Take(4))
            {
                f.Maps.Add(map);
            }

            var plan = QueryPlanner.Plan(f);
            Assert.Equal(28, plan.Count);
            Assert.All(plan, q => Assert.Contains("Map_s=", q.Key));

            f.Maps.Add(Catalog.Maps[4]);
            f.Bots.Add("false");
            plan = QueryPlanner.Plan(f);
            Assert.Equal(5, plan.Count);
            Assert.All(plan, q => Assert.EndsWith("&Bots_s=false", q.Key));
        }

        [Fact]
        public void NoBranchOfThePlanExceedsTheCap()
        {
            var f = new FilterState();
            foreach (var i in Enumerable.Range(1, 45))
            {
                f.Maps.Add("UnknownMap" + i);
            }

            Assert.Equal(QueryPlanner.MaxQueries, QueryPlanner.Plan(f).Count);
        }

        [Fact]
        public void SlotsSortBothWaysAndTiesKeepSteamsOrder()
        {
            var b = new ServerBrowser();
            b.Store(b.Plan()[0], new[] { S("10001", "a", limit: 10, rank: 0), S("10002", "b", limit: 4, rank: 1), S("10003", "c", limit: 7, rank: 2) }, T0);
            Assert.Equal(new[] { "b", "c", "a" }, b.Visible(SortKey.Slots, false).Select(s => s.Name));
            Assert.Equal(new[] { "a", "c", "b" }, b.Visible(SortKey.Slots, true).Select(s => s.Name));

            var c = new ServerBrowser();
            c.Store(c.Plan()[0], new[] { S("10001", "second", ping: 30, rank: 1), S("10002", "first", ping: 30, rank: 0) }, T0);
            Assert.Equal(new[] { "first", "second" }, c.Visible(SortKey.Ping, false).Select(s => s.Name));
        }

        [Fact]
        public void CurrentAnswersAreThoseAtMostTwoRefreshesOld()
        {
            var b = new ServerBrowser();
            b.Store(b.Plan()[0], new[] { S("10001") }, T0);
            Assert.Single(b.Current(T0 + b.MaxAge + b.MaxAge));
            Assert.Empty(b.Current(T0 + b.MaxAge + b.MaxAge + TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void UpdatedIsTheOldestAnswer()
        {
            var b = new ServerBrowser();
            var plan = b.Plan();
            for (var i = 0; i < plan.Count; i++)
            {
                b.Store(plan[i], new Server[0], T0.AddSeconds(i));
            }

            Assert.Equal(T0, b.UpdatedUtc());
        }

        [Fact]
        public void AgeUsesTheMostAdvancedRecentAnchor()
        {
            var age = new LobbyAge();
            const uint c = 1000000000;
            age.AddAnchor(c, T0);
            age.AddAnchor(c + (uint)(Rate * 3), T0.AddMinutes(5));

            // Projected to T0+10: the first anchor says c+10 min of lobbies, the lagging second only c+8.
            Assert.InRange(age.AgeMinutes(c + (uint)(Rate * 5), T0.AddMinutes(10)).Value, 4.9, 5.1);
        }

        [Fact]
        public void AQuietPeriodCannotDragTheRateFarDown()
        {
            var age = new LobbyAge();
            const uint c = 1000000000;
            age.AddAnchor(c, T0);
            age.AddAnchor(c + (uint)(Rate * 3), T0.AddMinutes(10));
            Assert.Equal(Rate * 0.6, age.RatePerMinute(T0.AddMinutes(10)), 3);
        }

        [Fact]
        public void ExpiredAnchorsDoNotBlockNewOnesOrCount()
        {
            var age = new LobbyAge();
            age.AddAnchor(5000000, T0);

            // Forty days on, with the counter past a wrap: the old anchor is gone, so ages are unknown, not absurd,
            // and the next sample is accepted.
            var later = T0.AddDays(40);
            Assert.Null(age.AgeMinutes(4000000, later));
            age.AddAnchor(4000000, later);
            Assert.Equal("4000000", age.ToRecords().Single()[0]);
        }

        [Fact]
        public void AnchorsFromTheFutureAreIgnored()
        {
            var age = new LobbyAge();
            age.AddAnchor(1000000000, T0.AddHours(1));
            Assert.Null(age.AgeMinutes(999000000, T0));
        }

        [Fact]
        public void LoadDropsAnchorsPastTheirLifetime()
        {
            var age = new LobbyAge();
            age.Load(new[]
            {
                new[] { "1000", T0.AddHours(-25).Ticks.ToString() },
                new[] { "2000", T0.AddHours(-1).Ticks.ToString() },
            }, T0);
            Assert.Equal("2000", age.ToRecords().Single()[0]);
        }
    }
}
