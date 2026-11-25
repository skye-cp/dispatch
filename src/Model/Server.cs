using System;
using System.ComponentModel;
using System.Globalization;

namespace Dispatch.Model
{
    /// <summary>
    /// One Bodycam server, as listed in Steam's public lobby list. Rows on screen stay the same objects
    /// across refreshes and are updated in place, so the list never rebuilds; changes raise notifications.
    /// </summary>
    internal sealed class Server : INotifyPropertyChanged
    {
        /// <summary>Ping not known (yet).</summary>
        public const int NoPing = -1;

        private string name = "";
        private string mode = "";
        private string map = "";
        private string bots = "";
        private string region = "";
        private int limit;
        private int ping = NoPing;
        private bool isFriend;
        private string friendName = "";
        private bool isFollowed;
        private bool isNew;
        private double ageMinutes = -1;
        private bool isNewRound;
        private bool isHighlighted;

        public event PropertyChangedEventHandler PropertyChanged;

        public string Id { get; set; } = "";
        public string Owner { get; set; } = "";

        public string Name
        {
            get => name;
            set => Set(ref name, value ?? "", nameof(Name));
        }

        public string Mode
        {
            get => mode;
            set => Set(ref mode, value ?? "", nameof(Mode));
        }

        public string Map
        {
            get => map;
            set => Set(ref map, value ?? "", nameof(Map), nameof(MapName));
        }

        public string Bots
        {
            get => bots;
            set => Set(ref bots, value ?? "", nameof(Bots), nameof(BotsText));
        }

        public string Region
        {
            get => region;
            set => Set(ref region, value ?? "", nameof(Region), nameof(RegionName));
        }

        public int Limit
        {
            get => limit;
            set => Set(ref limit, value, nameof(Limit), nameof(SlotsText));
        }

        /// <summary>Estimated round trip in ms through Steam's relays, or <see cref="NoPing"/>.</summary>
        public int Ping
        {
            get => ping;
            set => Set(ref ping, value, nameof(Ping), nameof(PingText), nameof(PingLevel));
        }

        /// <summary>Position in Steam's nearest-first answer.</summary>
        public int Rank { get; set; }

        public DateTime SeenUtc { get; set; }

        /// <summary>Set by the window: the host is one of the player's Steam friends.</summary>
        public bool IsFriend
        {
            get => isFriend;
            set => Set(ref isFriend, value, nameof(IsFriend), nameof(IsPinned), nameof(FriendTip));
        }

        /// <summary>Set by the window: the friend's Steam name, when <see cref="IsFriend"/>.</summary>
        public string FriendName
        {
            get => friendName;
            set => Set(ref friendName, value ?? "", nameof(FriendName), nameof(FriendTip));
        }

        /// <summary>Set by the window: the player follows this host.</summary>
        public bool IsFollowed
        {
            get => isFollowed;
            set => Set(ref isFollowed, value, nameof(IsFollowed), nameof(IsPinned));
        }

        /// <summary>Set by the window: the server appeared after the list first loaded.</summary>
        public bool IsNew
        {
            get => isNew;
            set => Set(ref isNew, value, nameof(IsNew));
        }

        /// <summary>Set by the window: estimated minutes since the server was created, or -1 if unknown.</summary>
        public double AgeMinutes
        {
            get => ageMinutes;
            set => Set(ref ageMinutes, value, nameof(AgeMinutes), nameof(AgeText));
        }

        /// <summary>Set by the window: the map or mode changed recently, so a new round has started.</summary>
        public bool IsNewRound
        {
            get => isNewRound;
            set => Set(ref isNewRound, value, nameof(IsNewRound));
        }

        /// <summary>Set by the window for a few seconds when a followed host's server appears: the row pulses.</summary>
        public bool IsHighlighted
        {
            get => isHighlighted;
            set => Set(ref isHighlighted, value, nameof(IsHighlighted));
        }

        /// <summary>Set by the window while the row animates out of the list.</summary>
        public bool IsLeaving { get; set; }

        public string MapName => Catalog.MapName(Map);
        public string RegionName => Catalog.RegionName(Region);
        public string BotsText => Bots == "true" ? "On" : Bots == "false" ? "Off" : "—";
        public string SlotsText => Limit > 0 ? Limit.ToString(CultureInfo.InvariantCulture) : "—";
        public string Link => JoinLink.Format(Id, Owner);

        /// <summary>A Steam 64-bit ID or lobby ID: digits only, within ulong.</summary>
        public static bool IsSteamId(string value) =>
            !string.IsNullOrEmpty(value) && value.Length <= 20 &&
            ulong.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0;
        public string PingText => Ping >= 0 ? Ping.ToString(CultureInfo.InvariantCulture) : "—";

        /// <summary>Good, Fair, Poor or Unknown; drives the ping colour.</summary>
        public string PingLevel => Ping < 0 ? "Unknown" : Ping <= 70 ? "Good" : Ping <= 140 ? "Fair" : "Poor";

        public string AgeText => LobbyAge.Format(AgeMinutes);
        public bool IsPinned => IsFriend || IsFollowed;
        public string FriendTip => IsFriend ? "Hosted by your Steam friend " + FriendName : null;

        /// <summary>Takes another snapshot of the same lobby's values; only real changes notify.</summary>
        public void CopyFrom(Server other)
        {
            Owner = other.Owner;
            Rank = other.Rank;
            SeenUtc = other.SeenUtc;
            Name = other.Name;
            Mode = other.Mode;
            Map = other.Map;
            Bots = other.Bots;
            Region = other.Region;
            Limit = other.Limit;
            Ping = other.Ping;
            IsFriend = other.IsFriend;
            FriendName = other.FriendName;
            IsFollowed = other.IsFollowed;
            IsNew = other.IsNew;
            AgeMinutes = other.AgeMinutes;
            IsNewRound = other.IsNewRound;
        }

        /// <summary>Sets a field and notifies the property and anything computed from it.</summary>
        private void Set<T>(ref T field, T value, params string[] names)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            var handler = PropertyChanged;
            if (handler == null)
            {
                return;
            }

            foreach (var n in names)
            {
                handler(this, new PropertyChangedEventArgs(n));
            }
        }
    }
}
