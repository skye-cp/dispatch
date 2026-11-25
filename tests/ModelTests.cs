using System;
using System.Linq;
using Dispatch.Model;
using Xunit;

namespace Dispatch.Tests
{
    public class ModelTests
    {
        private static Server S(string name, string mode = "Deathmatch", string map = "Asylum", string bots = "true", string region = "gru", int rank = 0) =>
            new Server { Id = "1" + name.Length + rank, Owner = "9" + rank, Name = name, Mode = mode, Map = map, Bots = bots, Region = region, Rank = rank };

        [Fact]
        public void PlanWithoutFiltersAsksOncePerModeExceptTraining()
        {
            var plan = QueryPlanner.Plan(new FilterState());
            Assert.Equal(Catalog.DefaultModes.Count, plan.Count);
            Assert.DoesNotContain(plan, q => q.Key.Contains("Training"));
            Assert.All(plan, q => Assert.StartsWith("InGame_s=1&GameMode_s=", q.Key));
        }

        [Fact]
        public void PlanSplitsByMapWhenItFits()
        {
            var f = new FilterState();
            f.Modes.Add("GunGame");
            f.Maps.Add("Asylum");
            f.Maps.Add("Village");
            var plan = QueryPlanner.Plan(f);
            Assert.Equal(new[] { "InGame_s=1&GameMode_s=GunGame&Map_s=Asylum", "InGame_s=1&GameMode_s=GunGame&Map_s=Village" }, plan.Select(q => q.Key));
        }

        [Fact]
        public void PlanFallsBackToPerModeWhenTooManyCombinations()
        {
            var f = new FilterState();
            foreach (var map in Catalog.Maps)
            {
                f.Maps.Add(map);
            }

            var plan = QueryPlanner.Plan(f);
            Assert.Equal(Catalog.DefaultModes.Count, plan.Count);
            Assert.DoesNotContain(plan, q => q.Key.Contains("Map_s"));
        }

        [Fact]
        public void OneSidedBotsIsSentToSteamBothSidesIsNot()
        {
            var f = new FilterState();
            f.Bots.Add("false");
            Assert.All(QueryPlanner.Plan(f), q => Assert.EndsWith("&Bots_s=false", q.Key));
            f.Bots.Add("true");
            Assert.DoesNotContain(QueryPlanner.Plan(f), q => q.Key.Contains("Bots_s"));
        }

        [Fact]
        public void HostSearchIsASingleExactRequest()
        {
            var f = new FilterState { ExactHost = "Some Host" };
            f.Modes.Add("Zombie");
            var plan = QueryPlanner.Plan(f);
            Assert.Single(plan);
            Assert.Equal("InGame_s=1&OWNINGNAME_s=Some Host", plan[0].Key);
            Assert.True(f.Matches(S("some host", mode: "Training")));
            Assert.False(f.Matches(S("some hostess")));
        }

        [Fact]
        public void FiltersCombineWithinAndAcrossKinds()
        {
            var f = new FilterState();
            Assert.True(f.Matches(S("a")));
            Assert.False(f.Matches(S("a", mode: Catalog.Training)));

            f.Modes.Add("Deathmatch");
            f.Modes.Add("Zombie");
            f.Regions.Add("gru");
            f.Text = "AL";
            Assert.True(f.Matches(S("alpha", mode: "Zombie")));
            Assert.False(f.Matches(S("alpha", mode: "GunGame")));
            Assert.False(f.Matches(S("alpha", region: "iad")));
            Assert.False(f.Matches(S("bravo")));
            Assert.Equal(3, f.ActiveCount);
        }

        [Fact]
        public void BrowserMergesAnswersAndKeepsTheNearestCopy()
        {
            var b = new ServerBrowser();
            var now = DateTime.UtcNow;
            var plan = b.Plan();
            var shared = S("shared", rank: 7);
            var nearer = S("shared", rank: 2);
            nearer.Id = shared.Id;
            b.Store(plan[0], new[] { shared, S("x", rank: 1) }, now);
            b.Store(plan[1], new[] { nearer }, now);

            var pool = b.Pool();
            Assert.Equal(2, pool.Count);
            Assert.Equal(2, pool.Single(s => s.Name == "shared").Rank);
            Assert.Equal(plan.Count - 2, b.Missing(now).Count);
            Assert.Null(b.UpdatedUtc());
            Assert.Equal(plan.Count, b.Missing(now + b.MaxAge + TimeSpan.FromSeconds(1)).Count);
        }

        [Fact]
        public void BrowserSortsAndCountsRegions()
        {
            var b = new ServerBrowser();
            var now = DateTime.UtcNow;
            b.Store(b.Plan()[0], new[] { S("charlie", region: "iad", rank: 0), S("alpha", rank: 1), S("bravo", rank: 2) }, now);

            Assert.Equal(new[] { "alpha", "bravo", "charlie" }, b.Visible(SortKey.Name, false).Select(s => s.Name));
            Assert.Equal(new[] { "charlie", "bravo", "alpha" }, b.Visible(SortKey.Name, true).Select(s => s.Name));
            Assert.Equal("gru", b.Regions()[0].Code);
            Assert.Equal(2, b.Regions()[0].Count);

            b.Filters.Modes.Add("Zombie");
            Assert.Empty(b.Pool());
            Assert.Equal(2, b.Regions().Count);
        }

        [Fact]
        public void PingSortsLowestFirstWithUnknownLastAndRanksRegions()
        {
            var b = new ServerBrowser();
            var far = S("far", region: "iad", rank: 0);
            var near = S("near", rank: 1);
            var unknown = S("unknown", region: "lim", rank: 2);
            b.Store(b.Plan()[0], new[] { far, near, unknown }, DateTime.UtcNow);
            Assert.Contains(b.Pool(), s => s.Ping < 0);

            b.ApplyPings(new System.Collections.Generic.Dictionary<string, int> { [far.Id] = 140, [near.Id] = 20 });
            Assert.Equal(new[] { "near", "far", "unknown" }, b.Visible(SortKey.Ping, false).Select(s => s.Name));
            Assert.Equal(new[] { "far", "near", "unknown" }, b.Visible(SortKey.Ping, true).Select(s => s.Name));
            Assert.Equal(new[] { "gru", "iad", "lim" }, b.Regions().Select(r => r.Code));
            Assert.Equal(20, b.Regions()[0].Ping);
        }

        [Fact]
        public void MaxPingHidesSlowServersButKeepsUnmeasuredOnes()
        {
            var f = new FilterState { MaxPing = 100 };
            Assert.True(f.Matches(new Server { Name = "a", Mode = "Zombie", Ping = 80 }));
            Assert.False(f.Matches(new Server { Name = "b", Mode = "Zombie", Ping = 120 }));
            Assert.True(f.Matches(new Server { Name = "c", Mode = "Zombie", Ping = Server.NoPing }));
            Assert.Equal(1, f.ActiveCount);
            f.Reset();
            Assert.Equal(0, f.MaxPing);
        }

        [Theory]
        [InlineData(23, 50)]
        [InlineData(50, 50)]
        [InlineData(51, 100)]
        [InlineData(150, 150)]
        [InlineData(151, 200)]
        [InlineData(201, 0)]
        [InlineData(-1, 0)]
        public void ClickedPingMapsToTheSmallestLimitThatKeepsIt(int ping, int limit) =>
            Assert.Equal(limit, FilterState.LimitFor(ping));

        [Fact]
        public void DemoDataHonoursQueriesAndUsesNoRealIds()
        {
            var zombies = DemoData.For(LobbyQuery.Matches("Zombie"), DateTime.UtcNow);
            Assert.NotEmpty(zombies);
            Assert.All(zombies, s => Assert.Equal("Zombie", s.Mode));
            Assert.Single(DemoData.For(LobbyQuery.HostName("night shift"), DateTime.UtcNow));
            Assert.All(DemoData.For(LobbyQuery.Matches(), DateTime.UtcNow), s => Assert.False(s.Owner.StartsWith("7656119", StringComparison.Ordinal)));
        }

        [Theory]
        [InlineData(-1, "Unknown", "—")]
        [InlineData(35, "Good", "35")]
        [InlineData(110, "Fair", "110")]
        [InlineData(200, "Poor", "200")]
        public void PingLevels(int ping, string level, string text)
        {
            var s = new Server { Ping = ping };
            Assert.Equal(level, s.PingLevel);
            Assert.Equal(text, s.PingText);
        }

        [Theory]
        [InlineData("gru=12+1,eze=52+5/40+1", "gru")]
        [InlineData("SEA=20", "sea")]
        [InlineData("", "")]
        [InlineData("garbage", "")]
        public void RegionComesFromTheFirstRelay(string ping, string region) =>
            Assert.Equal(region, Catalog.RegionFromPing(ping));

        [Fact]
        public void NamesAreFriendly()
        {
            Assert.Equal("Trenches", Catalog.MapName("Main_Trenches_Level_Design"));
            Assert.Equal("Village", Catalog.MapName("Village"));
            Assert.Equal("São Paulo", Catalog.RegionName("gru"));
            Assert.Equal("XYZ", Catalog.RegionName("xyz"));
        }

        [Fact]
        public void JoinLinksRoundTripAndAreFoundInsideText()
        {
            var link = JoinLink.Format("111111111111111111", "222222222222");
            Assert.Equal("steam://joinlobby/2406770/111111111111111111/222222222222", link);
            Assert.True(JoinLink.TryParse("join me: " + link + " thanks", out var found));
            Assert.Equal(link, found);
            Assert.False(JoinLink.TryParse("steam://joinlobby/12345/111111/222222", out _));
            Assert.False(JoinLink.TryParse(null, out _));
        }

        [Fact]
        public void SettingsRoundTrip()
        {
            var s = new Settings();
            s.SetList("modes", new[] { "Zombie", "GunGame" });
            s.SetDouble("width", 1234.5);
            s.Set("note", "a\nb");
            var copy = new Settings();
            copy.Parse(s.Serialize());
            Assert.Equal(new[] { "Zombie", "GunGame" }, copy.GetList("modes"));
            Assert.Equal(1234.5, copy.GetDouble("width", 0));
            Assert.Equal("a b", copy.Get("note"));
            Assert.Equal("x", copy.Get("missing", "x"));
        }
    }
}
