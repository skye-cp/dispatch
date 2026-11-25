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

        private static Server S(string owner, string name = "host", DateTime seen = default(DateTime)) =>
            new Server { Id = "9" + owner, Owner = owner, Name = name, Mode = "Zombie", Map = "Asylum", SeenUtc = seen };

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
            Assert.True(w.KnownNotHosting("1", None, T0));
            Assert.Single(w.Appeared(new[] { S("1") }, o => true, T0.AddMinutes(1)));
            Assert.Empty(w.Appeared(new[] { S("1") }, o => true, T0.AddMinutes(2)));
        }

        [Fact]
        public void AQuickReHostStaysQuietButALongerAbsenceAlerts()
        {
            var w = new HostWatch();
            w.Appeared(new[] { S("1") }, o => true, T0);

            // Found away a minute later, back a minute after that: within GoneAfter of last being seen.
            w.Record(new[] { "1" }, Found(), T0.AddMinutes(1));
            Assert.Empty(w.Appeared(new[] { S("1") }, o => true, T0.AddMinutes(2)));

            // Found away again, and back only after GoneAfter has passed since they were last seen.
            w.Record(new[] { "1" }, Found(), T0.AddMinutes(3));
            Assert.Single(w.Appeared(new[] { S("1") }, o => true, T0.AddMinutes(2) + HostWatch.GoneAfter + TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void BeingLeftOutForAWhileIsNotLeaving()
        {
            // A long follow list rotates slowly through checks: a host nobody saw for minutes, but no check found
            // away, has not left, so seeing them again must not alert.
            var w = new HostWatch();
            w.Appeared(new[] { S("1") }, o => true, T0);
            Assert.Empty(w.Appeared(new[] { S("1") }, o => true, T0 + HostWatch.GoneAfter + HostWatch.GoneAfter));
        }

        [Fact]
        public void ALongFollowListNeverAlertsForHostsWhoStayed()
        {
            // 40 followed hosts, all hosting throughout, outside the list, checked ten at a time every 75 seconds.
            var w = new HostWatch();
            var owners = Enumerable.Range(1, 40).Select(i => i.ToString()).ToList();
            var alerts = 0;
            for (var tick = 0; tick < 48; tick++)
            {
                var now = T0.AddSeconds(75 * tick);
                var batch = w.NextToCheck(owners);
                w.Record(batch, Found(batch.Select(o => S(o)).ToArray()), now);
                alerts += w.Appeared(w.Online(None, now), o => true, now).Count;
            }

            Assert.Equal(0, alerts);
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
        public void ARecentCheckWinsOverAnOlderListEntryAndExpires()
        {
            var w = new HostWatch();
            var listed = new[] { S("1", "from list", seen: T0.AddSeconds(-30)) };
            w.Record(new[] { "1" }, Found(), T0);

            // The check said "not hosting" after the list entry was fetched, so the entry is ignored while the check is fresh.
            Assert.Null(w.HostingNow("1", listed, T0.AddSeconds(30)));
            Assert.False(w.NeedsCheck("1", None, T0.AddSeconds(30)));

            // At exactly CheckFor the check has expired: the list answers again, and with no list entry a check is needed.
            var expired = T0 + HostWatch.CheckFor;
            Assert.Equal("from list", w.HostingNow("1", listed, expired).Name);
            Assert.True(w.NeedsCheck("1", None, expired));
        }

        [Fact]
        public void AListSightingNewerThanTheCheckWins()
        {
            var w = new HostWatch();
            w.Record(new[] { "1" }, Found(), T0);
            var rehosted = new[] { S("1", "back", seen: T0.AddSeconds(40)) };
            Assert.Equal("back", w.HostingNow("1", rehosted, T0.AddSeconds(50)).Name);
            Assert.False(w.KnownNotHosting("1", rehosted, T0.AddSeconds(50)));
        }

        [Fact]
        public void AHostFoundAndLaterFoundGoneIsNotHosting()
        {
            var w = new HostWatch();
            w.Record(new[] { "1" }, Found(S("1")), T0);
            Assert.NotNull(w.HostingNow("1", None, T0.AddSeconds(10)));
            w.Record(new[] { "1" }, Found(), T0.AddSeconds(30));
            Assert.Null(w.HostingNow("1", None, T0.AddSeconds(40)));
            Assert.Equal("Not hosting now", w.Detail("1", None, "?", T0.AddSeconds(40)));
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
            Assert.Single(w.Online(new[] { S("1") }, T0 + HostWatch.CheckFor));
        }
    }
}
