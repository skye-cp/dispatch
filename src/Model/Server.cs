using System;

namespace Dispatch.Model
{
    /// <summary>One Bodycam server, as listed in Steam's public lobby list.</summary>
    internal sealed class Server
    {
        /// <summary>Ping not known (yet).</summary>
        public const int NoPing = -1;

        public string Id { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Name { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Map { get; set; } = "";
        public string Bots { get; set; } = "";
        public string Region { get; set; } = "";
        public int Limit { get; set; }

        /// <summary>Estimated round trip in ms through Steam's relays, or <see cref="NoPing"/>.</summary>
        public int Ping { get; set; } = NoPing;

        /// <summary>Position in Steam's nearest-first answer.</summary>
        public int Rank { get; set; }

        public DateTime SeenUtc { get; set; }

        /// <summary>Set by the window: the host is one of the player's Steam friends.</summary>
        public bool IsFriend { get; set; }

        /// <summary>Set by the window: the friend's Steam name, when <see cref="IsFriend"/>.</summary>
        public string FriendName { get; set; } = "";

        /// <summary>Set by the window: the player follows this host.</summary>
        public bool IsFollowed { get; set; }

        /// <summary>Set by the window: the server appeared after the list first loaded.</summary>
        public bool IsNew { get; set; }

        /// <summary>Set by the window: estimated minutes since the server was created, or -1 if unknown.</summary>
        public double AgeMinutes { get; set; } = -1;

        /// <summary>Set by the window: the map or mode changed recently, so a new round has started.</summary>
        public bool IsNewRound { get; set; }

        public string AgeText => LobbyAge.Format(AgeMinutes);

        public bool IsPinned => IsFriend || IsFollowed;

        public string FriendTip => IsFriend ? "Hosted by your Steam friend " + FriendName : null;

        public string MapName => Catalog.MapName(Map);
        public string RegionName => Catalog.RegionName(Region);
        public string BotsText => Bots == "true" ? "On" : Bots == "false" ? "Off" : "—";
        public string SlotsText => Limit > 0 ? Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
        public string Link => JoinLink.Format(Id, Owner);
        public string PingText => Ping >= 0 ? Ping.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";

        /// <summary>Good, Fair, Poor or Unknown; drives the ping colour.</summary>
        public string PingLevel => Ping < 0 ? "Unknown" : Ping <= 70 ? "Good" : Ping <= 140 ? "Fair" : "Poor";
    }
}
