using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dispatch.Model;
using Dispatch.Steam;
using Xunit;

namespace Dispatch.Tests
{
    public class LookupOutputTests
    {
        private static Server S(string id, string name) => new Server
        {
            Id = id,
            Owner = "200000000001",
            Name = name,
            Mode = "Zombie",
            Map = "Asylum",
            Bots = "true",
            Region = "gru",
            Limit = 4,
        };

        private static (LookupOutput Output, List<(int Index, List<Server> Servers)> Answers) Read(params string[] lines)
        {
            var answers = new List<(int, List<Server>)>();
            var output = LobbyClient.ReadOutput(new StringReader(string.Join("\n", lines) + "\n"), (i, servers) => answers.Add((i, servers)));
            return (output, answers);
        }

        [Fact]
        public void EachRequestsServersArriveWhenItIsDone()
        {
            var (output, answers) = Read(
                Protocol.EncodeServer(0, S("100000000000000001", "a")),
                Protocol.EncodeServer(0, S("100000000000000002", "b")),
                Protocol.EncodeDone(0, 2),
                Protocol.EncodeServer(1, S("100000000000000003", "c")),
                Protocol.EncodeDone(1, 1));
            Assert.Equal(2, output.Answered);
            Assert.Equal(new[] { 0, 1 }, answers.Select(a => a.Index));
            Assert.Equal(new[] { "a", "b" }, answers[0].Servers.Select(s => s.Name));
            Assert.Equal(new[] { 0, 1 }, answers[0].Servers.Select(s => s.Rank));
            Assert.Equal(new[] { "c" }, answers[1].Servers.Select(s => s.Name));
        }

        [Fact]
        public void AFailedRequestDropsItsPartialServers()
        {
            var (output, answers) = Read(
                Protocol.EncodeServer(0, S("100000000000000001", "partial")),
                Protocol.EncodeFailed(0),
                Protocol.EncodeServer(1, S("100000000000000002", "kept")),
                Protocol.EncodeDone(1, 1));
            Assert.Equal(1, output.Answered);
            Assert.Equal("kept", answers.Single().Servers.Single().Name);
        }

        [Fact]
        public void ErrorsLocationAgeFriendsAndPingsAreCollected()
        {
            var (output, _) = Read(
                Protocol.EncodeError("steam", "Steam is not running."),
                Protocol.EncodeLocation("gru=12+1"),
                Protocol.EncodeAnchor(123456),
                Protocol.EncodeFriend("200000000009", "Sam"),
                Protocol.EncodePing("100000000000000001", 42),
                "garbage line");
            Assert.Equal("Steam is not running.", output.Error);
            Assert.Equal("gru=12+1", output.Location);
            Assert.Equal(123456u, output.NewestCounter);
            Assert.Equal("Sam", output.Friends["200000000009"]);
            Assert.Equal(42, output.Pings["100000000000000001"]);
            Assert.Equal(0, output.Answered);
        }

        [Fact]
        public void NoAnchorMeansNoAgeSample()
        {
            var (output, _) = Read(Protocol.EncodeDone(0, 0));
            Assert.Equal(0u, output.NewestCounter);
            Assert.Null(output.Location);
        }
    }
}
