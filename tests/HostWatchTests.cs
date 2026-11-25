using System;
using System.Collections.Generic;
using System.Linq;
using Dispatch.Model;
using Xunit;

namespace Dispatch.Tests
{
    public class HostWatchTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

        private static Server S(string owner, string name = "host") =>
            new Server { Id = "9" + owner, Owner = owner, Name = name, Mode = "Zombie", Map = "Asylum" };

        private static readonly Server[] None = new Server[0];

        private static Dictionary<string, Server> Found(params Server[] servers) => servers.ToDictionary(s => s.Owner);

        [Fact]
        public void HostOnlineAtStartDoesNotAlert()
        {
            var w = new HostWatch();
            Assert.Empty(w.Appeared(new[] { S("1") }, o => true, T0));
            Assert.Empty(w.Appeared(new[] { S("1") }, o => true, T0.AddMinutes(1)));
        }

        [Fact]
        public void HostFoundAwayThenOnlineAlertsOnce()
        {
            var w = new HostWatch();
            w.Record(new[] { "1" }, Found(), T0);
            Assert.True(w.KnownNotHosting("1", T0));
            Assert.Single(w.Appeared(new[] { S("1") }, o => true, T0.AddMinutes(1)));
            Assert.Empty(w.Appeared(new[] { S("1") }, o => true, T0.AddMinutes(2)));
        }

        [Fact]
        public void HostGoneForAWhileAlertsAgainButAQuickReHostDoesNot()
        {
            var w = new HostWatch();
            w.Appeared(new[] { S("1") }, o => true, T0);
            Assert.Empty(w.Appeared(new[] { S("1") }, o => true, T0.Add(HostWatch.GoneAfter)));
            Assert.Single(w.Appeared(new[] { S("1") }, o => true, T0.Add(HostWatch.GoneAfter + HostWatch.GoneAfter + TimeSpan.FromSeconds(1))));
        }

        [Fact]
        public void OnlyFollowedHostsCount()
        {
            var w = new HostWatch();
            w.Record(new[] { "1", "2" }, Found(), T0);
            var appeared = w.Appeared(new[] { S("1"), S("2") }, o => o == "2", T0.AddMinutes(1));
            Assert.Equal(new[] { "2" }, appeared.Select(s => s.Owner));
        }

        [Fact]
        public void FollowingSomeoneAlreadyOnlineDoesNotAlert()
        {
            var w = new HostWatch();
            w.Record(new[] { "1" }, Found(), T0);
            w.MarkSeen("1", T0.AddMinutes(1));
            Assert.Empty(w.Appeared(new[] { S("1") }, o => true, T0.AddMinutes(2)));
        }

        [Fact]
        public void ARecentCheckWinsOverTheListAndExpires()
        {
            var w = new HostWatch();
            var listed = new[] { S("1", "from list") };
            w.Record(new[] { "1" }, Found(), T0);

            // The check said "not hosting", so the (possibly older) list entry is ignored while the check is fresh.
            Assert.Null(w.HostingNow("1", listed, T0.AddSeconds(30)));
            Assert.False(w.NeedsCheck("1", None, T0.AddSeconds(30)));

            // Once the check expires the list answers again, and with no list entry a new check is needed.
            var later = T0 + HostWatch.CheckFor + TimeSpan.FromSeconds(1);
            Assert.Equal("from list", w.HostingNow("1", listed, later).Name);
            Assert.True(w.NeedsCheck("1", None, later));
        }

        [Fact]
        public void DetailDescribesWhatIsKnown()
        {
            var w = new HostWatch();
            w.Record(new[] { "1", "2" }, Found(S("1")), T0);
            Assert.StartsWith("Hosting now", w.Detail("1", None, "?", T0));
            Assert.Equal("Not hosting now", w.Detail("2", None, "?", T0));
            Assert.Equal("?", w.Detail("3", None, "?", T0));
        }

        [Fact]
        public void ChecksRotateThroughLongLists()
        {
            var w = new HostWatch();
            var owners = Enumerable.Range(1, 15).Select(i => i.ToString()).ToList();
            var first = w.NextToCheck(owners);
            Assert.Equal(HostWatch.MaxChecks, first.Count);
            w.Record(first, Found(), T0);
            var second = w.NextToCheck(owners);
            Assert.Equal(owners.Except(first), second.Take(owners.Count - first.Count));
        }

        [Fact]
        public void OnlineCombinesTheListAndRecentChecks()
        {
            var w = new HostWatch();
            w.Record(new[] { "2" }, Found(S("2")), T0);
            var online = w.Online(new[] { S("1") }, T0.AddSeconds(10));
            Assert.Equal(new[] { "1", "2" }, online.Select(s => s.Owner).OrderBy(o => o));
            Assert.Single(w.Online(new[] { S("1") }, T0 + HostWatch.CheckFor + TimeSpan.FromSeconds(1)));
        }
    }
}
