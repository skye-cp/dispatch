using System;
using System.Linq;
using Dispatch.Model;
using Dispatch.Steam;
using Xunit;

namespace Dispatch.Tests
{
    public class PeopleTests
    {
        private static Server S(string id, string owner, string name, int ping = 50, int rank = 0) =>
            new Server { Id = id, Owner = owner, Name = name, Mode = "Zombie", Map = "Asylum", Region = "gru", Ping = ping, Rank = rank };

        [Fact]
        public void FollowingTogglesAndTracksRenames()
        {
            var f = new FollowList();
            Assert.True(f.Toggle("1", "old name"));
            Assert.True(f.Contains("1"));
            f.Refresh(new[] { S("10", "1", "new name") });
            Assert.Equal("new name", f.Hosts.Single().Name);
            Assert.False(f.Toggle("1", "new name"));
            Assert.Empty(f.Hosts);
        }

        [Fact]
        public void RecentKeepsOnePerHostNewestFirstAndCaps()
        {
            var r = new RecentList();
            var now = DateTime.UtcNow;
            for (var i = 0; i < RecentList.Capacity + 3; i++)
            {
                r.Add(S("l" + i, "o" + i, "n" + i), now.AddMinutes(i));
            }

            r.Add(S("l-again", "o5", "n5"), now.AddHours(1));
            Assert.Equal(RecentList.Capacity, r.Items.Count);
            Assert.Equal("o5", r.Items[0].Owner);
            Assert.Equal("l-again", r.Items[0].Lobby);
            Assert.Single(r.Items, x => x.Owner == "o5");
        }

        [Fact]
        public void FollowsAndRecentSurviveSettingsEvenWithAwkwardNames()
        {
            var follows = new FollowList();
            follows.Toggle("200000000111", "a|b,c%d ★");
            var recent = new RecentList();
            var when = new DateTime(2026, 11, 25, 3, 0, 0, DateTimeKind.Utc);
            recent.Add(S("100000000000000009", "200000000222", "x,y|z"), when);

            var s = new Settings();
            s.SetRecords("followed", follows.ToRecords());
            s.SetRecords("recent", recent.ToRecords());
            var copy = new Settings();
            copy.Parse(s.Serialize());

            var f2 = new FollowList();
            f2.Load(copy.GetRecords("followed"));
            var r2 = new RecentList();
            r2.Load(copy.GetRecords("recent"));
            Assert.Equal("a|b,c%d ★", f2.Hosts.Single().Name);
            Assert.Equal("x,y|z", r2.Items.Single().Name);
            Assert.Equal(when, r2.Items.Single().JoinedUtc);
            Assert.Equal(JoinLink.Format("100000000000000009", "200000000222"), r2.Items.Single().Link);
        }

        [Fact]
        public void PinnedServersStayOnTopWhateverTheSort()
        {
            var b = new ServerBrowser();
            var near = S("1", "a", "near", 10, 0);
            var friend = S("2", "b", "friend", 150, 1);
            var followed = S("3", "c", "followed", 90, 2);
            friend.IsFriend = true;
            followed.IsFollowed = true;
            b.Store(b.Plan()[0], new[] { near, friend, followed }, DateTime.UtcNow);
            Assert.Equal(new[] { "followed", "friend", "near" }, b.Visible(SortKey.Ping, false).Select(x => x.Name));
        }

        [Fact]
        public void FirstSeenIsKeptAcrossRefreshes()
        {
            var b = new ServerBrowser();
            var t0 = DateTime.UtcNow;
            b.Store(b.Plan()[0], new[] { S("1", "a", "x") }, t0);
            b.Invalidate();
            b.Store(b.Plan()[0], new[] { S("1", "a", "x"), S("2", "b", "y") }, t0.AddMinutes(1));
            Assert.Equal(DateTime.MinValue, b.FirstSeenUtc("1"));
            Assert.Equal(t0.AddMinutes(1), b.FirstSeenUtc("2"));

            // A search never run before shows existing servers, not new ones.
            b.Store(b.Plan()[1], new[] { S("4", "d", "z") }, t0.AddMinutes(2));
            Assert.Equal(DateTime.MinValue, b.FirstSeenUtc("4"));
            Assert.Equal(DateTime.MinValue, b.FirstSeenUtc("3"));
            Assert.Equal(3, b.All().Count);
        }

        [Fact]
        public void RefreshKeepsShowingCurrentServersUntilAnswersArrive()
        {
            var b = new ServerBrowser();
            var now = DateTime.UtcNow;
            foreach (var q in b.Plan())
            {
                b.Store(q, new[] { S(q.Key, "o" + q.Key, "x") }, now);
            }

            Assert.Empty(b.Missing(now));
            b.Invalidate();
            Assert.Equal(b.Plan().Count, b.Missing(now).Count);
            Assert.Equal(b.Plan().Count, b.Pool().Count);

            b.Store(b.Plan()[0], new Server[0], now);
            Assert.Equal(b.Plan().Count - 1, b.Pool().Count);
            Assert.Equal(b.Plan().Count - 1, b.Missing(now).Count);
        }

        [Fact]
        public void FriendRecordsSurviveTheProtocol()
        {
            Assert.True(Protocol.TryDecodeFriend(Protocol.Split(Protocol.EncodeFriend("123", "Some\tName")), out var id, out var name));
            Assert.Equal("123", id);
            Assert.Equal("Some Name", name);
        }

        [Fact]
        public void PlayerCountReadsSteamsAnswer()
        {
            Assert.Equal(5285, PlayerCount.Parse("{\"response\":{\"player_count\":5285,\"result\":1}}"));
            Assert.Null(PlayerCount.Parse("{\"response\":{\"result\":42}}"));
            Assert.Null(PlayerCount.Parse("{\"response\":{\"player_count\":0,\"result\":2}}"));
            Assert.Null(PlayerCount.Parse(null));
        }

        [Fact]
        public void UpdateCheckParsesGitHubAndComparesVersions()
        {
            const string json = "{\"html_url\":\"https://github.com/skye-cp/dispatch/releases/tag/v0.6.0\",\"tag_name\":\"v0.6.0\",\"name\":\"x\"}";
            var latest = UpdateCheck.Parse(json);
            Assert.NotNull(latest);
            Assert.Equal(new Version(0, 6, 0), latest.Version);
            Assert.True(UpdateCheck.IsNewer(latest, new Version(0, 5, 0, 0)));
            Assert.False(UpdateCheck.IsNewer(latest, new Version(0, 6, 0, 0)));
            Assert.Null(UpdateCheck.Parse("{\"message\":\"Not Found\"}"));
            Assert.Null(UpdateCheck.Parse("{\"tag_name\":\"v1\",\"html_url\":\"https://evil.example/releases/tag/v1\"}"));
        }
    }
}
