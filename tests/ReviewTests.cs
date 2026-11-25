using System;
using System.Linq;
using Dispatch.Model;
using Dispatch.Steam;
using Xunit;

namespace Dispatch.Tests
{
    /// <summary>Regression tests for the bugs found in the November 2026 code review.</summary>
    public class ReviewTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void AgeSurvivesTheLobbyCounterWrapping()
        {
            // The anchor was taken just before the 32-bit counter wrapped; the server was created after it.
            var age = new LobbyAge();
            var anchor = uint.MaxValue - 100000;
            age.AddAnchor(anchor, T0);
            var server = unchecked(anchor + (uint)(LobbyAge.DefaultRatePerMinute * 30));
            var minutes = age.AgeMinutes(server, T0.AddMinutes(45)).Value;
            Assert.InRange(minutes, 14, 16);

            // And a server from before the wrap, judged by an anchor after it.
            var after = new LobbyAge();
            after.AddAnchor(5000000, T0);
            var old = unchecked(5000000u - (uint)(LobbyAge.DefaultRatePerMinute * 130));
            Assert.InRange(after.AgeMinutes(old, T0).Value, 129, 131);
        }

        [Fact]
        public void NewestCounterIsPickedAcrossTheWrap()
        {
            Assert.Equal(10u, LobbyAge.Newer(uint.MaxValue - 5, 10));
            Assert.Equal(10u, LobbyAge.Newer(10, uint.MaxValue - 5));
            Assert.Equal(7u, LobbyAge.Newer(0, 7));
        }

        [Fact]
        public void ARepeatedOldSampleDoesNotBecomeAFreshAnchor()
        {
            var age = new LobbyAge();
            const uint counter = 2000000000;
            age.AddAnchor(counter, T0);
            for (var i = 1; i <= 300; i++)
            {
                age.AddAnchor(counter, T0.AddMinutes(i));
            }

            // Only the real anchor counts, so a server 240 minutes old still reads about 240.
            var server = unchecked(counter - (uint)(LobbyAge.DefaultRatePerMinute * 180));
            Assert.InRange(age.AgeMinutes(server, T0.AddMinutes(60)).Value, 235, 245);
            Assert.Single(age.ToRecords());
        }

        [Fact]
        public void LobbyDataThatIsNotASteamIdIsRejected()
        {
            var record = new[] { Protocol.ServerRecord, "0", "109775241256387706", "1\" --evil", "name", "Zombie", "Asylum", "On", "gru", "4" };
            Assert.Null(Protocol.DecodeServer(record, 0, T0));
            record[3] = "200000000001";
            Assert.NotNull(Protocol.DecodeServer(record, 0, T0));

            Assert.Null(JoinLink.Format("109775241256387706", "1\" --evil"));
            Assert.False(JoinLink.IsValid("steam://joinlobby/2406770/109775241256387706/1\" --evil"));
            Assert.True(JoinLink.IsValid(JoinLink.Format("109775241256387706", "200000000001")));
        }

        [Fact]
        public void JoinLinksKeepWholeIds()
        {
            // A 23-digit owner is not a SteamID: it must not be cut down to a different one.
            Assert.False(JoinLink.TryParse("steam://joinlobby/2406770/109775241256387706/76561198000000000123456", out _));
            Assert.True(JoinLink.TryParse("see steam://joinlobby/2406770/109775241256387706/200000000001 there", out var link));
            Assert.Equal("steam://joinlobby/2406770/109775241256387706/200000000001", link);
        }

        [Fact]
        public void UpdatesComeOnlyFromThisRepository()
        {
            const string other = "{\"html_url\":\"https://github.com/someone-else/malware/releases/tag/v9.0.0\",\"tag_name\":\"v9.0.0\"}";
            Assert.Null(UpdateCheck.Parse(other));
            Assert.False(UpdateCheck.IsReleasePage("https://github.com/skye-cp/dispatch/releases/tag/v1 x"));
            Assert.True(UpdateCheck.IsReleasePage("https://github.com/skye-cp/dispatch/releases/tag/v0.9.0"));
        }

        [Fact]
        public void AFourPartTagIsTheSameVersion()
        {
            var latest = new ReleaseInfo(new Version(0, 8, 0, 0), "v0.8.0.0", "https://github.com/skye-cp/dispatch/releases/tag/v0.8.0.0");
            Assert.False(UpdateCheck.IsNewer(latest, new Version(0, 8, 0, 0)));
        }

        [Fact]
        public void AnOutOfRangeTimeInSettingsDoesNotStopStartUp()
        {
            var recent = new RecentList();
            recent.Load(new[] { new[] { "1", "2", "n", "m", "p", "4000000000000000000" } });
            Assert.Equal(DateTime.MaxValue.Ticks, recent.Items.Single().JoinedUtc.Ticks);
        }

        [Fact]
        public void OldLibraryFoldersFormatIsRead()
        {
            const string vdf = "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\t\"1600000000\"\n\t\"ContentStatsID\"\t\t\"-123\"\n\t\"1\"\t\t\"D:\\\\SteamLibrary\"\n}";
            Assert.Equal(new[] { @"D:\SteamLibrary" }, GameLocator.ParseLibraryFolders(vdf));
        }

        [Fact]
        public void PlannerMatchesNamesWhateverTheirCase()
        {
            var f = new FilterState();
            f.Modes.Add("zombie");
            f.Maps.Add("asylum");
            var plan = QueryPlanner.Plan(f);
            Assert.Single(plan);
        }
    }
}
