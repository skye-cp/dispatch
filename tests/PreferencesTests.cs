using System;
using System.Linq;
using Dispatch.Model;
using Xunit;

namespace Dispatch.Tests
{
    public class PreferencesTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

        private static Settings RoundTrip(Settings s)
        {
            var copy = new Settings();
            copy.Parse(s.Serialize());
            return copy;
        }

        [Fact]
        public void EverythingSurvivesARoundTrip()
        {
            var filters = new FilterState { MaxPing = 100, AgeLimit = -30 };
            filters.Modes.Add("Zombie");
            filters.Maps.Add("Asylum");
            filters.Bots.Add("true");
            filters.Regions.Add("gru");
            var follows = new FollowList();
            follows.Toggle("200000000001", "Graveyard Watch");
            var recent = new RecentList();
            recent.Add(new Server { Id = "100000000000000001", Owner = "200000000002", Name = "Patrol 7", Mode = "Zombie", Map = "Asylum" }, T0);
            var age = new LobbyAge();
            age.AddAnchor(123456, T0);
            var p = new Preferences
            {
                Sort = SortKey.Age,
                Descending = true,
                AutoRefresh = true,
                Sounds = false,
                CopyOnJoin = false,
                PingLocation = "gru=12+1,iad=140+14",
                PingLocationUtc = T0,
                Left = 100,
                Top = 50,
                Width = 1200,
                Height = 800,
                Maximized = true,
            };
            var s = new Settings();
            p.Write(s, filters, follows, recent, age);

            var filters2 = new FilterState();
            var follows2 = new FollowList();
            var recent2 = new RecentList();
            var age2 = new LobbyAge();
            var q = Preferences.Read(RoundTrip(s), filters2, follows2, recent2, age2, T0.AddMinutes(1));

            Assert.Equal(new[] { "Zombie" }, filters2.Modes);
            Assert.Equal(new[] { "Asylum" }, filters2.Maps);
            Assert.Equal(new[] { "true" }, filters2.Bots);
            Assert.Equal(new[] { "gru" }, filters2.Regions);
            Assert.Equal(100, filters2.MaxPing);
            Assert.Equal(-30, filters2.AgeLimit);
            Assert.Equal("Graveyard Watch", follows2.Hosts.Single().Name);
            Assert.Equal("Patrol 7", recent2.Items.Single().Name);
            Assert.Single(age2.ToRecords());
            Assert.Equal(SortKey.Age, q.Sort);
            Assert.True(q.Descending);
            Assert.True(q.AutoRefresh);
            Assert.False(q.Sounds);
            Assert.False(q.CopyOnJoin);
            Assert.Equal("gru=12+1,iad=140+14", q.PingLocation);
            Assert.Equal(T0, q.PingLocationUtc);
            Assert.Equal(new[] { 100.0, 50, 1200, 800 }, new[] { q.Left, q.Top, q.Width, q.Height });
            Assert.True(q.Maximized);
        }

        [Fact]
        public void DefaultsWhenNothingIsSaved()
        {
            var q = Preferences.Read(new Settings(), new FilterState(), new FollowList(), new RecentList(), new LobbyAge(), T0);
            Assert.Equal(SortKey.Ping, q.Sort);
            Assert.False(q.AutoRefresh);
            Assert.True(q.Sounds);
            Assert.True(q.CopyOnJoin);
            Assert.True(double.IsNaN(q.Width));
            Assert.Equal(default(DateTime), q.PingLocationUtc);
        }

        [Fact]
        public void ValuesTheMenusDontOfferFallBackToAny()
        {
            var s = new Settings();
            s.Parse("maxPing=75\nageLimit=45\nsort=Nearest\npingLocationTime=99999999999999999999\n");
            var filters = new FilterState();
            var q = Preferences.Read(s, filters, new FollowList(), new RecentList(), new LobbyAge(), T0);
            Assert.Equal(0, filters.MaxPing);
            Assert.Equal(0, filters.AgeLimit);
            Assert.Equal(SortKey.Ping, q.Sort);
            Assert.Equal(default(DateTime), q.PingLocationUtc);
        }

        [Fact]
        public void UnsavedPlacementIsNotWritten()
        {
            var s = new Settings();
            new Preferences().Write(s, new FilterState(), new FollowList(), new RecentList(), new LobbyAge());
            Assert.Null(s.Get(Preferences.Keys.Width, null));
        }

        [Theory]
        [InlineData(100, 100, true)]
        [InlineData(-30, 0, true)]
        [InlineData(-100, 0, false)]
        [InlineData(1900, 100, false)]
        [InlineData(100, 1050, false)]
        public void SavedPositionMustStillBeReachable(double left, double top, bool expected)
        {
            Assert.Equal(expected, Preferences.IsOnScreen(left, top, 0, 0, 1920, 1080));
        }
    }
}
