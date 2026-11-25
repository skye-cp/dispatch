using System;

namespace Dispatch.Model
{
    /// <summary>One Bodycam server, as listed in Steam's public lobby list.</summary>
    internal sealed class Server
    {
        public string Id { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Name { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Map { get; set; } = "";
        public string Bots { get; set; } = "";
        public string Region { get; set; } = "";
        public int Limit { get; set; }

        /// <summary>Position in Steam's nearest-first answer.</summary>
        public int Rank { get; set; }

        public DateTime SeenUtc { get; set; }

        public string MapName => Catalog.MapName(Map);
        public string RegionName => Catalog.RegionName(Region);
        public string BotsText => Bots == "true" ? "On" : Bots == "false" ? "Off" : "—";
        public string SlotsText => Limit > 0 ? Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
        public string Link => JoinLink.Format(Id, Owner);
    }
}
