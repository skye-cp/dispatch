using System.Linq;
using Dispatch.Model;
using Xunit;

namespace Dispatch.Tests
{
    public class FilterKindTests
    {
        private static Server S() => new Server
        {
            Id = "1",
            Owner = "2",
            Name = "Patrol 7",
            Mode = "Zombie",
            Map = "Asylum",
            Bots = "true",
            Region = "sto",
            Ping = 42,
            AgeMinutes = 20,
        };

        [Theory]
        [InlineData("mode", "Mode")]
        [InlineData("bots", "Bots")]
        [InlineData("Region", "Region")]
        public void XamlTagsReadAsKinds(string tag, string expected)
        {
            Assert.True(FilterState.TryKind(tag, out var kind));
            Assert.Equal(expected, kind.ToString());
        }

        [Theory]
        [InlineData("star")]
        [InlineData("bot")]
        [InlineData("3")]
        [InlineData("")]
        [InlineData(null)]
        public void OtherTagsAreNotKinds(string tag) => Assert.False(FilterState.TryKind(tag, out _));

        [Fact]
        public void LabelsDescribeTheClickedValue()
        {
            var s = S();
            Assert.Equal("Zombie", FilterState.Label(FilterKind.Mode, s));
            Assert.Equal("bots on", FilterState.Label(FilterKind.Bots, s));
            Assert.Equal("ping ≤ 50 ms", FilterState.Label(FilterKind.Ping, s));
            Assert.Equal("age under 30 min", FilterState.Label(FilterKind.Age, s));
            s.Ping = Server.NoPing;
            Assert.Null(FilterState.Label(FilterKind.Ping, s));
        }

        [Fact]
        public void ClickingAValueTwiceAddsThenRemovesItsFilter()
        {
            var f = new FilterState();
            var s = S();
            foreach (var kind in FilterState.Kinds)
            {
                Assert.False(f.Toggle(kind, s, new string[0]));
                Assert.True(f.IsActive(kind, s));
                Assert.StartsWith("Remove the ", f.ActionText(kind, s));
                Assert.True(f.Toggle(kind, s, new string[0]));
                Assert.False(f.IsActive(kind, s));
                Assert.StartsWith("Filter by ", f.ActionText(kind, s));
            }

            Assert.Equal(0, f.ActiveCount);
        }

        [Fact]
        public void ARegionClickCoversEveryRelayInTheCity()
        {
            var f = new FilterState();
            f.Toggle(FilterKind.Region, S(), new[] { "sto", "sto2", "gru" });
            Assert.Equal(new[] { "sto", "sto2" }, f.Regions.OrderBy(r => r));
        }

        [Fact]
        public void ClearEmptiesOneCategoryOnly()
        {
            var f = new FilterState { MaxPing = 100 };
            f.Modes.Add("Zombie");
            f.Clear(FilterKind.Mode);
            Assert.Empty(f.Modes);
            Assert.Equal(100, f.MaxPing);
            f.Clear(FilterKind.Ping);
            Assert.Equal(0, f.MaxPing);
        }

        [Fact]
        public void TheFilterBarSummarisesSelections()
        {
            var f = new FilterState { MaxPing = 50 };
            f.Modes.Add("Versus");
            f.Modes.Add("Zombie");
            f.Maps.Add("Asylum");
            f.Maps.Add("Rome");
            f.Maps.Add("Village");
            Assert.Equal("Any", FilterState.Summary(f.Selected(FilterKind.Region)));
            Assert.Equal(2, f.Selected(FilterKind.Mode).Count);
            Assert.Equal("Asylum +2", FilterState.Summary(f.Selected(FilterKind.Map)));
            Assert.Equal("≤ 50 ms", FilterState.Summary(f.Selected(FilterKind.Ping)));
            f.Bots.Add("false");
            Assert.Equal("Off", FilterState.Summary(f.Selected(FilterKind.Bots)));
        }

        [Fact]
        public void OnlySteamFiltersNeedNewSearches()
        {
            Assert.Equal(new[] { FilterKind.Mode, FilterKind.Map, FilterKind.Bots }, FilterState.Kinds.Where(FilterState.NeedsLookup));
        }
    }
}
