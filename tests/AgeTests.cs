using System;
using System.Linq;
using Dispatch.Model;
using Xunit;

namespace Dispatch.Tests
{
    public class AgeTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void CounterIsTheLowThirtyTwoBitsOfTheLobbyId()
        {
            Assert.Equal((uint)(109775241256387706UL & 0xFFFFFFFF), LobbyAge.CounterOf("109775241256387706"));
            Assert.Equal(0u, LobbyAge.CounterOf("not a number"));
        }

        [Fact]
        public void AgeUsesTheDefaultRateUntilCalibrated()
        {
            var age = new LobbyAge();
            Assert.Null(age.AgeMinutes(1000, T0));
            age.AddAnchor(10_000_000, T0);
            var tenMinutesOfLobbies = (uint)(10 * LobbyAge.DefaultRatePerMinute);
            Assert.Equal(10, age.AgeMinutes(10_000_000 - tenMinutesOfLobbies, T0).Value, 1);
            Assert.Equal(15, age.AgeMinutes(10_000_000 - tenMinutesOfLobbies, T0.AddMinutes(5)).Value, 1);
            Assert.Equal(0, age.AgeMinutes(10_000_000 + 5000, T0).Value, 3);
        }

        [Fact]
        public void AgeCalibratesFromAnchorsAtLeastTenMinutesApart()
        {
            var age = new LobbyAge();
            age.AddAnchor(1_000_000, T0);
            age.AddAnchor(1_600_000, T0.AddMinutes(20)); // 30,000 a minute
            Assert.Equal(30_000, age.RatePerMinute(T0.AddMinutes(20)), 1);
            Assert.Equal(10, age.AgeMinutes(1_300_000, T0.AddMinutes(20)).Value, 1);

            // A stale anchor (its newest lobby was already old) must not drag ages down.
            age.AddAnchor(1_300_000, T0.AddMinutes(20));
            Assert.Equal(10, age.AgeMinutes(1_300_000, T0.AddMinutes(20)).Value, 1);

            var close = new LobbyAge();
            close.AddAnchor(1_000_000, T0);
            close.AddAnchor(1_100_000, T0.AddMinutes(2));
            Assert.Equal(LobbyAge.DefaultRatePerMinute, close.RatePerMinute(T0.AddMinutes(2)));
        }

        [Fact]
        public void AnchorsSurviveSettingsAndOldOnesExpire()
        {
            var age = new LobbyAge();
            age.AddAnchor(1_000_000, T0.AddHours(-30));
            age.AddAnchor(2_000_000, T0);
            var s = new Settings();
            s.SetRecords("ageAnchors", age.ToRecords());
            var back = new LobbyAge();
            back.Load(s.GetRecords("ageAnchors"), T0);
            Assert.Single(back.ToRecords());
        }

        [Theory]
        [InlineData(-1, "—")]
        [InlineData(0.4, "<1 min")]
        [InlineData(17.9, "17 min")]
        [InlineData(65, "1 h 05")]
        [InlineData(144, "2 h 24")]
        public void AgeFormats(double minutes, string text) => Assert.Equal(text, LobbyAge.Format(minutes));

        [Fact]
        public void AgeFilterBucketsAndMatches()
        {
            Assert.Equal(15, FilterState.AgeLimitFor(3));
            Assert.Equal(60, FilterState.AgeLimitFor(40));
            Assert.Equal(-60, FilterState.AgeLimitFor(90));
            Assert.Equal(0, FilterState.AgeLimitFor(-1));
            Assert.Equal("Under 15 min", FilterState.AgeLimitText(15));
            Assert.Equal("Under 1 h", FilterState.AgeLimitText(60));
            Assert.Equal("Over 1 h", FilterState.AgeLimitText(-60));

            var f = new FilterState { AgeLimit = 15 };
            Assert.True(f.Matches(new Server { Name = "a", Mode = "Zombie", AgeMinutes = 10 }));
            Assert.False(f.Matches(new Server { Name = "b", Mode = "Zombie", AgeMinutes = 20 }));
            Assert.True(f.Matches(new Server { Name = "c", Mode = "Zombie", AgeMinutes = -1 }));
            f.AgeLimit = -60;
            Assert.True(f.Matches(new Server { Name = "d", Mode = "Zombie", AgeMinutes = 61 }));
            Assert.False(f.Matches(new Server { Name = "e", Mode = "Zombie", AgeMinutes = 59 }));
            Assert.Equal(1, f.ActiveCount);
        }

        [Fact]
        public void MapOrModeChangeMarksANewRound()
        {
            var b = new ServerBrowser();
            var q = b.Plan()[0];
            b.Store(q, new[] { new Server { Id = "1", Owner = "o", Name = "x", Mode = "Zombie", Map = "Asylum" } }, T0);
            Assert.Equal(DateTime.MinValue, b.RoundStartUtc("1"));
            b.Store(q, new[] { new Server { Id = "1", Owner = "o", Name = "x", Mode = "Zombie", Map = "Asylum" } }, T0.AddMinutes(1));
            Assert.Equal(DateTime.MinValue, b.RoundStartUtc("1"));
            b.Store(q, new[] { new Server { Id = "1", Owner = "o", Name = "x", Mode = "Zombie", Map = "Village" } }, T0.AddMinutes(2));
            Assert.Equal(T0.AddMinutes(2), b.RoundStartUtc("1"));
        }

        [Fact]
        public void AgeSortsYoungestFirstWithUnknownLast()
        {
            var b = new ServerBrowser();
            var old = new Server { Id = "1", Owner = "a", Name = "old", Mode = "Zombie", AgeMinutes = 90 };
            var young = new Server { Id = "2", Owner = "b", Name = "young", Mode = "Zombie", AgeMinutes = 4 };
            var unknown = new Server { Id = "3", Owner = "c", Name = "unknown", Mode = "Zombie" };
            b.Store(b.Plan()[0], new[] { old, unknown, young }, T0);
            Assert.Equal(new[] { "young", "old", "unknown" }, b.Visible(SortKey.Age, false).Select(s => s.Name));
            Assert.Equal(new[] { "old", "young", "unknown" }, b.Visible(SortKey.Age, true).Select(s => s.Name));
            Assert.Equal(new uint[] { 1, 2, 3 }.Max(), ServerBrowser.NewestCounter(new[] { old, young, unknown }));
        }
    }
}
