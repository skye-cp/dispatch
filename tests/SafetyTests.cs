using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Dispatch.Model;
using Dispatch.Steam;
using Xunit;

namespace Dispatch.Tests
{
    /// <summary>Input from hosts, the clipboard and the settings file must never become anything but data.</summary>
    public class SafetyTests
    {
        private const string Lobby = "109775241256387706";
        private const string Owner = "200000000001";
        private static readonly string Link = "steam://joinlobby/2406770/" + Lobby + "/" + Owner;

        [Theory]
        [InlineData("0")]
        [InlineData("00000")]
        [InlineData("012345678")]
        [InlineData("1234")]
        [InlineData("+12345")]
        [InlineData(" 12345")]
        [InlineData("12345 ")]
        [InlineData("18446744073709551616")]
        [InlineData("\u0661\u0662\u0663\u0664\u0665")]
        public void OnlyPlainSteamIdsAreAccepted(string value)
        {
            Assert.False(Server.IsSteamId(value));
            Assert.Null(JoinLink.Format(Lobby, value));
        }

        [Fact]
        public void PlainSteamIdsAreAccepted()
        {
            Assert.True(Server.IsSteamId("12345"));
            Assert.True(Server.IsSteamId("18446744073709551615"));
        }

        [Theory]
        [InlineData("x")]
        [InlineData(" --evil")]
        [InlineData("\n")]
        [InlineData("/")]
        public void OnlyACompleteJoinLinkMayBeOpened(string extra)
        {
            Assert.True(JoinLink.IsValid(Link));
            Assert.False(JoinLink.IsValid(extra + Link));
            Assert.False(JoinLink.IsValid(Link + extra));
        }

        [Fact]
        public void LinksWithForeignDigitsOrOverlongIdsAreNotJoinLinks()
        {
            Assert.False(JoinLink.IsValid("steam://joinlobby/2406770/\u0661\u0662\u0663\u0664\u0665/\u0661\u0662\u0663\u0664\u0665"));
            Assert.False(JoinLink.TryParse("steam://joinlobby/2406770/\u0661\u0662\u0663\u0664\u0665/\u0661\u0662\u0663\u0664\u0665", out _));

            // 22 digits whose first 20 are a valid number: must not be cut down to a different ID.
            Assert.False(JoinLink.TryParse("steam://joinlobby/2406770/" + Lobby + "/1000000000000000000005", out _));
        }

        [Fact]
        public void AMalformedLinkEarlierInTheTextDoesNotHideARealOne()
        {
            Assert.True(JoinLink.TryParse("try steam://joinlobby/2406770/00000/00000 or " + Link + " thanks", out var link));
            Assert.Equal(Link, link);
        }

        [Fact]
        public void HostNamesCannotBreakRecordsOrReorderText()
        {
            var name = "a\rb\0c\u0085d\u2028e\u202Ef\uFEFFg\u200Dh";
            var server = new Server { Id = Lobby, Owner = Owner, Name = name, Mode = "Zombie", Map = "Asylum", Bots = "true", Region = "gru", Limit = 4 };
            var stream = Protocol.EncodeServer(0, server) + "\n" + Protocol.EncodeDone(0, 1) + "\n";
            var answers = new System.Collections.Generic.List<Server>();
            var output = LobbyClient.ReadOutput(new StringReader(stream), (i, servers) => answers.AddRange(servers));

            Assert.Equal(1, output.Answered);
            var back = answers.Single().Name;
            Assert.DoesNotContain(back, ch => ch == '\r' || ch == '\0' || ch == '\u0085' || ch == '\u2028' || ch == '\u202E' || ch == '\uFEFF');
            Assert.Contains('\u200D', back);
        }

        [Fact]
        public void HostSearchTextCannotAddFilters()
        {
            var query = LobbyQuery.HostName("a\tb\u001fc\nd");
            var back = Protocol.DecodeQuery(Protocol.EncodeQuery(query));
            Assert.Equal(2, back.Filters.Count);
            Assert.DoesNotContain(back.Filters[1].Value, ch => char.IsControl(ch));
        }

        [Fact]
        public void FollowsFromADamagedFileKeepOnlyRealHostsOnce()
        {
            var follows = new FollowList();
            follows.Load(new[] { new[] { Owner, "a" }, new[] { Owner, "again" }, new[] { "not-an-id", "b" }, new[] { "", "c" } });
            Assert.Equal(new[] { Owner }, follows.Hosts.Select(h => h.Owner));
        }

        [Fact]
        public void HugeRecordFieldsAreCutDown()
        {
            var s = new Settings();
            s.SetRecords("followed", new[] { new[] { Owner, new string('x', 100000) } });
            var copy = new Settings();
            copy.Parse(s.Serialize());
            Assert.Equal(512, copy.GetRecords("followed").Single()[1].Length);
        }

        [Fact]
        public void BotsFromSettingsTakeOnlySteamsValues()
        {
            var s = new Settings();
            s.Parse("bots=yes,TRUE\n");
            var filters = new FilterState();
            Preferences.Read(s, filters, new FollowList(), new RecentList(), new LobbyAge(), DateTime.UtcNow);
            Assert.Equal(new[] { "true" }, filters.Bots);
        }

        [Fact]
        public void PreferencesDontDependOnTheUsersNumberFormat()
        {
            var before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var s = new Settings();
                new Preferences { Width = 1234.5, Height = 800, Left = -7.25, Top = 3 }.Write(s, new FilterState(), new FollowList(), new RecentList(), new LobbyAge());
                var copy = new Settings();
                copy.Parse(s.Serialize());
                var p = Preferences.Read(copy, new FilterState(), new FollowList(), new RecentList(), new LobbyAge(), DateTime.UtcNow);
                Assert.Equal(1234.5, p.Width);
                Assert.Equal(-7.25, p.Left);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = before;
            }
        }

        [Fact]
        public void UpdateTagsWithTwoPartsAreRead()
        {
            var latest = UpdateCheck.Parse("{\"html_url\":\"https://github.com/skye-cp/dispatch/releases/tag/v1.0\",\"tag_name\":\"v1.0\"}");
            Assert.NotNull(latest);
            Assert.True(UpdateCheck.IsNewer(latest, new Version(0, 8, 0, 0)));
        }

        [Fact]
        public void AnInstallFolderWithAnEscapedQuoteIsRead()
        {
            Assert.Equal("Body\"cam", GameLocator.ParseInstallDir("\"installdir\"\t\t\"Body\\\"cam\""));
        }
    }
}
