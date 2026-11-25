using System;
using System.Globalization;
using System.Linq;

namespace Dispatch.Model
{
    /// <summary>
    /// What Dispatch remembers between sessions, mapped to and from settings.ini in one place. The lists and
    /// records it reads are owned by their models (filters, follows, recent servers, age anchors); the rest are
    /// plain values here. No UI: the window applies them.
    /// </summary>
    internal sealed class Preferences
    {
        /// <summary>Settings keys. Changing one loses that setting for existing users.</summary>
        public static class Keys
        {
            public const string Modes = "modes";
            public const string Maps = "maps";
            public const string Bots = "bots";
            public const string Regions = "regions";
            public const string MaxPing = "maxPing";
            public const string AgeLimit = "ageLimit";
            public const string AgeAnchors = "ageAnchors";
            public const string Sort = "sort";
            public const string Descending = "descending";
            public const string Followed = "followed";
            public const string Recent = "recent";
            public const string AutoRefresh = "autoRefresh";
            public const string Sounds = "sounds";
            public const string CopyOnJoin = "copyOnJoin";
            public const string PingLocation = "pingLocation";
            public const string PingLocationTime = "pingLocationTime";
            public const string Left = "left";
            public const string Top = "top";
            public const string Width = "width";
            public const string Height = "height";
            public const string Maximized = "maximized";
            public const string UpdateCheck = "updateCheck";
            public const string UpdateTag = "updateTag";
            public const string UpdateUrl = "updateUrl";
            public const string UpdateCheckedAt = "updateCheckedAt";
        }

        public SortKey Sort { get; set; } = SortKey.Ping;
        public bool Descending { get; set; }
        public bool AutoRefresh { get; set; }
        public bool Sounds { get; set; } = true;
        public bool CopyOnJoin { get; set; } = true;
        public string PingLocation { get; set; }
        public DateTime PingLocationUtc { get; set; }

        /// <summary>Window placement; NaN when never saved.</summary>
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;
        public double Width { get; set; } = double.NaN;
        public double Height { get; set; } = double.NaN;
        public bool Maximized { get; set; }

        public static Preferences Read(Settings s, FilterState filters, FollowList follows, RecentList recent, LobbyAge age, DateTime nowUtc)
        {
            foreach (var v in s.GetList(Keys.Modes))
            {
                filters.Modes.Add(v);
            }

            foreach (var v in s.GetList(Keys.Maps))
            {
                filters.Maps.Add(v);
            }

            foreach (var v in s.GetList(Keys.Bots).Select(b => b.ToLowerInvariant()).Where(b => b == "true" || b == "false"))
            {
                filters.Bots.Add(v);
            }

            foreach (var v in s.GetList(Keys.Regions))
            {
                filters.Regions.Add(v);
            }

            // Only the values the filter menus offer; anything else (an edited file, an older version) means "any".
            var maxPing = (int)s.GetDouble(Keys.MaxPing, 0);
            filters.MaxPing = FilterState.PingLimits.Contains(maxPing) ? maxPing : 0;
            var ageLimit = (int)s.GetDouble(Keys.AgeLimit, 0);
            filters.AgeLimit = FilterState.AgeLimits.Contains(ageLimit) ? ageLimit : 0;

            age.Load(s.GetRecords(Keys.AgeAnchors), nowUtc);
            follows.Load(s.GetRecords(Keys.Followed));
            recent.Load(s.GetRecords(Keys.Recent));

            var p = new Preferences
            {
                AutoRefresh = s.Get(Keys.AutoRefresh) == "1",
                Sounds = s.Get(Keys.Sounds, "1") == "1",
                CopyOnJoin = s.Get(Keys.CopyOnJoin, "1") == "1",
                PingLocation = s.Get(Keys.PingLocation),
                Left = s.GetDouble(Keys.Left, double.NaN),
                Top = s.GetDouble(Keys.Top, double.NaN),
                Width = s.GetDouble(Keys.Width, double.NaN),
                Height = s.GetDouble(Keys.Height, double.NaN),
                Maximized = s.Get(Keys.Maximized) == "1",
            };

            if (Enum.TryParse(s.Get(Keys.Sort), out SortKey key) && Enum.IsDefined(typeof(SortKey), key))
            {
                p.Sort = key;
                p.Descending = s.Get(Keys.Descending) == "1";
            }

            if (long.TryParse(s.Get(Keys.PingLocationTime), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
                ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
            {
                p.PingLocationUtc = new DateTime(ticks, DateTimeKind.Utc);
            }

            return p;
        }

        public void Write(Settings s, FilterState filters, FollowList follows, RecentList recent, LobbyAge age)
        {
            s.SetList(Keys.Modes, filters.Modes);
            s.SetList(Keys.Maps, filters.Maps);
            s.SetList(Keys.Bots, filters.Bots);
            s.SetList(Keys.Regions, filters.Regions);
            s.SetDouble(Keys.MaxPing, filters.MaxPing);
            s.SetDouble(Keys.AgeLimit, filters.AgeLimit);
            s.SetRecords(Keys.AgeAnchors, age.ToRecords());
            s.Set(Keys.Sort, Sort.ToString());
            s.Set(Keys.Descending, Descending ? "1" : "0");
            s.SetRecords(Keys.Followed, follows.ToRecords());
            s.SetRecords(Keys.Recent, recent.ToRecords());
            s.Set(Keys.AutoRefresh, AutoRefresh ? "1" : "0");
            s.Set(Keys.Sounds, Sounds ? "1" : "0");
            s.Set(Keys.CopyOnJoin, CopyOnJoin ? "1" : "0");
            if (!string.IsNullOrEmpty(PingLocation))
            {
                s.Set(Keys.PingLocation, PingLocation);
                s.Set(Keys.PingLocationTime, PingLocationUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            if (!double.IsNaN(Width) && !double.IsNaN(Height))
            {
                s.SetDouble(Keys.Left, Left);
                s.SetDouble(Keys.Top, Top);
                s.SetDouble(Keys.Width, Width);
                s.SetDouble(Keys.Height, Height);
            }

            s.Set(Keys.Maximized, Maximized ? "1" : "0");
        }

        /// <summary>
        /// True when a saved position still puts enough of the window on the desktop to grab it: monitors may
        /// have been unplugged or rearranged since it was saved.
        /// </summary>
        public static bool IsOnScreen(double left, double top, double screenLeft, double screenTop, double screenWidth, double screenHeight) =>
            !double.IsNaN(left) && !double.IsNaN(top) &&
            left >= screenLeft - 40 && top >= screenTop - 10 &&
            left + 120 <= screenLeft + screenWidth &&
            top + 60 <= screenTop + screenHeight;
    }
}
