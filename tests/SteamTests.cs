using System;
using System.Linq;
using Dispatch.Model;
using Dispatch.Steam;
using Xunit;

namespace Dispatch.Tests
{
    public class SteamTests
    {
        [Fact]
        public void QueriesSurviveTheProtocol()
        {
            var q = new LobbyQuery(new[]
            {
                new LobbyFilter("InGame_s", "1"),
                new LobbyFilter("GameMode_s", "Training", Comparison.NotEqual),
                new LobbyFilter("OWNINGNAME_s", "name with = and ; and ★"),
            });
            var back = Protocol.DecodeQuery(Protocol.EncodeQuery(q));
            Assert.Equal(q.Key, back.Key);
            Assert.Equal(Comparison.NotEqual, back.Filters[1].Comparison);
        }

        [Fact]
        public void ServersSurviveTheProtocolAndCannotBreakIt()
        {
            var s = new Server
            {
                Id = "111",
                Owner = "222",
                Name = "tab\there\nnewline ★",
                Mode = "Zombie",
                Map = "Asylum",
                Bots = "true",
                Region = "gru",
                Limit = 4,
            };
            var line = Protocol.EncodeServer(3, s);
            Assert.DoesNotContain('\n', line);
            var f = Protocol.Split(line);
            Assert.Equal(11, f.Length);
            Assert.Equal(Protocol.ServerRecord, f[0]);
            var back = Protocol.DecodeServer(f, 5, DateTime.UtcNow);
            Assert.Equal("tab here newline ★", back.Name);
            Assert.Equal(4, back.Limit);
            Assert.Equal(5, back.Rank);
            Assert.Null(Protocol.DecodeServer(new[] { "L", "0" }, 0, DateTime.UtcNow));
        }

        [Fact]
        public void LibraryFoldersAreReadFromSteamsVdf()
        {
            const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n" +
                               "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\Games\\\\SteamLibrary\"\n\t\t\"label\"\t\t\"\"\n\t}\n}\n";
            Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\Games\SteamLibrary" }, GameLocator.ParseLibraryFolders(vdf).ToArray());
        }

        [Fact]
        public void InstallFolderIsReadFromTheAppManifest()
        {
            const string acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"2406770\"\n\t\"name\"\t\t\"Bodycam\"\n\t\"installdir\"\t\t\"Bodycam\"\n}\n";
            Assert.Equal("Bodycam", GameLocator.ParseInstallDir(acf));
            Assert.Equal("", GameLocator.ParseInstallDir("{}"));
        }

        [Fact]
        public void PingsAndLocationSurviveTheProtocol()
        {
            var s = new Server { Id = "1", Owner = "2", Name = "n", Ping = 42 };
            Assert.Equal(42, Protocol.DecodeServer(Protocol.Split(Protocol.EncodeServer(0, s)), 0, DateTime.UtcNow).Ping);

            var old = Protocol.Split("L\t0\t1\t2\tn\tm\tp\tb\tgru\t10");
            Assert.Equal(Server.NoPing, Protocol.DecodeServer(old, 0, DateTime.UtcNow).Ping);

            Assert.True(Protocol.TryDecodePing(Protocol.Split(Protocol.EncodePing("123", 77)), out var lobby, out var ms));
            Assert.Equal("123", lobby);
            Assert.Equal(77, ms);

            const string location = "gru=18+1,eze=46+4/40+1";
            Assert.True(Protocol.TryDecodeLocationHeader(Protocol.EncodeLocationHeader(location), out var back));
            Assert.Equal(location, back);
            Assert.False(Protocol.TryDecodeLocationHeader(Protocol.EncodeQuery(LobbyQuery.HostId("9")), out _));
        }

        [Fact]
        public void CleanRemovesControlCharacters() =>
            Assert.Equal("a b c", Protocol.Clean("a\tb\u001fc"));
    }
}
