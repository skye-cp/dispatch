using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dispatch.Model;
using Dispatch.Steam;
using SortKey = Dispatch.Model.SortKey;

namespace Dispatch
{
    public partial class MainWindow : Window
    {
        private const int ClipboardUpdateMessage = 0x031D;
        private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan MessageFor = TimeSpan.FromSeconds(6);
        private static readonly int[] PingLimits = FilterState.PingLimits;
        private static readonly string[] FilterKinds = { "mode", "map", "bots", "region", "age", "ping" };

        private readonly ServerBrowser browser = new ServerBrowser();
        private readonly LobbyClient client = new LobbyClient();
        private readonly Settings settings;
        private readonly bool demo;
        private readonly DispatcherTimer debounce;
        private readonly DispatcherTimer messageTimer;
        private readonly DispatcherTimer clock;
        private readonly DispatcherTimer saveTimer;
        private readonly Storyboard progress;

        private SortKey sortKey = SortKey.Ping;
        private bool sortDescending;
        private CancellationTokenSource lookup;
        private int pendingRequests;
        private int doneRequests;
        private string lastError;
        private string lastCopied;
        private string regionChipKeys = "";
        private bool quiet;
        private DateTime launchConfirmUntil;

        private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan NewFor = TimeSpan.FromSeconds(90);
        private readonly FollowList follows = new FollowList();
        private readonly RecentList recent = new RecentList();
        private readonly DispatcherTimer watchTimer;
        private readonly Dictionary<string, Server> watchFound = new Dictionary<string, Server>();
        private static readonly TimeSpan GoneAfter = TimeSpan.FromMinutes(3);
        private readonly Dictionary<string, DateTime> followedSeen = new Dictionary<string, DateTime>();
        private bool followSeenReady;
        private DateTime? baselineUtc;
        private bool autoRefresh;
        private bool watching;
        private ReleaseInfo latestRelease;
        private int? playersOnline;
        private DateTime playersCheckedUtc = DateTime.MinValue;
        private static readonly TimeSpan NewRoundFor = TimeSpan.FromMinutes(5);
        private readonly LobbyAge lobbyAge = new LobbyAge();
        private readonly System.Collections.ObjectModel.ObservableCollection<Server> rows = new System.Collections.ObjectModel.ObservableCollection<Server>();
        private readonly Dictionary<string, Server> shown = new Dictionary<string, Server>();
        private readonly Sounds sounds = new Sounds();
        private int ticks;

        /// <param name="demo">Shows invented servers and leaves Steam and saved settings alone (for screenshots).</param>
        public MainWindow(bool demo = false)
        {
            this.demo = demo;
            settings = demo ? new Settings() : Settings.Load();
            InitializeComponent();
            List.ItemsSource = rows;

            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            debounce.Tick += (s, e) =>
            {
                debounce.Stop();
                _ = UpdateAsync(false);
            };
            messageTimer = new DispatcherTimer { Interval = MessageFor };
            messageTimer.Tick += (s, e) =>
            {
                messageTimer.Stop();
                ShowDefaultStatus();
            };
            clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (s, e) =>
            {
                if (!messageTimer.IsEnabled)
                {
                    ShowDefaultStatus();
                }

                // Ages tick on their own, between refreshes.
                if (++ticks % 30 == 0 && !Busy)
                {
                    Render();
                }
            };

            // Every minute: auto-refresh (if on), then a quiet check on followed hosts.
            watchTimer = new DispatcherTimer { Interval = WatchEvery };
            watchTimer.Tick += (s, e) => OnWatchTick();

            // Settings are saved shortly after every change, so a crash or forced close keeps them.
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            saveTimer.Tick += (s, e) =>
            {
                saveTimer.Stop();
                if (!demo)
                {
                    SaveSettings();
                }
            };

            var slide = new DoubleAnimation(-160, 1600, new Duration(TimeSpan.FromSeconds(1.2))) { RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(slide, ProgressBar);
            Storyboard.SetTargetProperty(slide, new PropertyPath(Canvas.LeftProperty));
            progress = new Storyboard();
            progress.Children.Add(slide);

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = "v" + version.Major + "." + version.Minor + "." + version.Build;

            RestoreSettings();
            BuildChips();
            UpdateHeaders();
            UpdateFilterSummary();

            Loaded += (s, e) =>
            {
                Motion.Fade(Frame, 0, 1, 260);
                clock.Start();
                watchTimer.Start();
                _ = UpdateAsync(false);
                _ = RefreshPlayersOnlineAsync();
                _ = CheckForUpdateAsync();
            };
            Activated += (s, e) => CheckClipboard();
            Closing += (s, e) =>
            {
                if (!demo)
                {
                    SaveSettings();
                }
            };
            StateChanged += (s, e) => OnWindowState();
            PreviewKeyDown += OnWindowKey;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            AddClipboardFormatListener(handle);
            CheckClipboard();
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == ClipboardUpdateMessage)
            {
                // Read after the copying app has finished with the clipboard.
                Dispatcher.BeginInvoke(new Action(CheckClipboard), DispatcherPriority.Background);
            }

            return IntPtr.Zero;
        }

        // ---------- data ----------

        private async Task UpdateAsync(bool force)
        {
            if (force)
            {
                browser.Invalidate();
            }

            var missing = browser.Missing(DateTime.UtcNow);
            if (demo)
            {
                foreach (var q in missing)
                {
                    browser.Store(q, DemoData.For(q, DateTime.UtcNow), DateTime.UtcNow);
                }

                missing.Clear();
            }

            Render();
            if (missing.Count == 0)
            {
                return;
            }

            lookup?.Cancel();
            var cts = new CancellationTokenSource();
            lookup = cts;
            pendingRequests = missing.Count;
            doneRequests = 0;
            lastError = null;
            SetBusy(true);
            ShowDefaultStatus();

            try
            {
                await client.RunAsync(
                    missing,
                    (index, servers) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (cts.IsCancellationRequested || index < 0 || index >= missing.Count)
                        {
                            return;
                        }

                        browser.Store(missing[index], servers, DateTime.UtcNow);
                        doneRequests++;
                        Render();
                    })),
                    pings => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        browser.ApplyPings(pings);
                        Render();
                    })),
                    cts.Token,
                    sampleAge: !browser.Filters.IsHostSearch);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (LocatorException e)
            {
                lastError = e.Message;
            }
            catch (LookupException e)
            {
                lastError = e.Message;
            }
            finally
            {
                if (lookup == cts)
                {
                    SetBusy(false);
                }
            }

            if (lookup == cts)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (baselineUtc == null && lastError == null)
                {
                    // Servers that show up after this first full load count as new.
                    baselineUtc = DateTime.UtcNow;
                }

                if (lastError == null && !demo && client.NewestCounter > 0)
                {
                    // The lookup's unfiltered sample: its newest server was created moments ago.
                    lobbyAge.AddAnchor(client.NewestCounter, DateTime.UtcNow);
                    QueueSave();
                }

                Render();
                _ = RefreshPlayersOnlineAsync();
            }
        }

        /// <summary>The app-wide "playing now" total, at most once a minute; keeps the last value if Steam doesn't answer.</summary>
        private async Task RefreshPlayersOnlineAsync()
        {
            if (DateTime.UtcNow - playersCheckedUtc < PlayerCount.Interval)
            {
                return;
            }

            playersCheckedUtc = DateTime.UtcNow;
            var count = demo ? DemoData.PlayersOnline : await PlayerCount.FetchAsync();
            if (count.HasValue)
            {
                playersOnline = count;
                if (!messageTimer.IsEnabled)
                {
                    ShowDefaultStatus();
                }
            }
        }

        private bool Busy => lookup != null && !lookup.IsCancellationRequested && ProgressTrack.Visibility == Visibility.Visible;

        private void Render()
        {
            Annotate();
            var visible = browser.Visible(sortKey, sortDescending);
            ApplyRows(visible);

            HeadName.Content = HeaderText(SortKey.Name, "SERVERS (" + visible.Count + ")");
            UpdateRegionChips();
            UpdateEmptyState(visible.Count);
            UpdateActions();
            if (!messageTimer.IsEnabled)
            {
                ShowDefaultStatus();
            }
        }

        private void UpdateEmptyState(int count)
        {
            EmptyAction.Visibility = Visibility.Collapsed;
            if (count > 0)
            {
                EmptyState.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyState.Visibility = Visibility.Visible;
            if (lastError != null)
            {
                EmptyText.Text = lastError;
                ShowEmptyAction("TRY AGAIN");
            }
            else if (Busy && doneRequests < pendingRequests)
            {
                EmptyText.Text = "Asking Steam for servers…";
            }
            else if (browser.Filters.IsHostSearch)
            {
                EmptyText.Text = "No server hosted by \"" + browser.Filters.ExactHost + "\" right now.\nHost names must match exactly.";
                ShowEmptyAction("CLEAR SEARCH");
            }
            else if (!string.IsNullOrWhiteSpace(browser.Filters.Text))
            {
                EmptyText.Text = "No listed server matches \"" + browser.Filters.Text.Trim() + "\".\nPress Enter to search every server for that exact host name.";
                ShowEmptyAction("SEARCH EVERY SERVER");
            }
            else
            {
                EmptyText.Text = "No servers match these filters.";
                ShowEmptyAction("RESET FILTERS");
            }
        }

        private void ShowEmptyAction(string text)
        {
            EmptyAction.Content = text;
            EmptyAction.Visibility = Visibility.Visible;
        }

        private void SetBusy(bool busy)
        {
            ProgressTrack.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            RefreshButton.IsEnabled = !busy;
            if (busy)
            {
                progress.Begin(this, true);
            }
            else
            {
                progress.Stop(this);
            }
        }

        // ---------- the list, updated in place ----------

        /// <summary>Above this many additions and removals at once (a filter change), skip the row animations.</summary>
        private const int MaxAnimatedChanges = 40;

        /// <summary>
        /// Turns the visible list into the rows on screen without rebuilding: rows stay the same objects and
        /// take new values in place, servers that left fade out, new ones grow in, and moved rows slide.
        /// </summary>
        private void ApplyRows(List<Server> target)
        {
            var wanted = new List<Server>(target.Count);
            var wantedIds = new HashSet<string>();
            var added = 0;
            foreach (var s in target)
            {
                wantedIds.Add(s.Id);
                if (shown.TryGetValue(s.Id, out var row))
                {
                    if (!ReferenceEquals(row, s))
                    {
                        row.CopyFrom(s);
                    }

                    row.IsLeaving = false;
                    wanted.Add(row);
                }
                else
                {
                    wanted.Add(s);
                    added++;
                }
            }

            var leaving = rows.Where(r => !wantedIds.Contains(r.Id) && !r.IsLeaving).ToList();
            var animate = Motion.Enabled && IsLoaded && rows.Count > 0 && added + leaving.Count <= MaxAnimatedChanges;
            var before = animate ? RowPositions() : null;

            foreach (var row in leaving)
            {
                if (animate && List.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item && item.IsVisible)
                {
                    row.IsLeaving = true;
                    Motion.Fade(item, 1, 0, 160);
                    Motion.ScaleHeight(item, 1, 0, 180, () =>
                    {
                        if (row.IsLeaving)
                        {
                            rows.Remove(row);
                            shown.Remove(row.Id);
                        }

                        item.Opacity = 1;
                        Motion.ScaleHeight(item, 1, 1, 0);
                    });
                }
                else
                {
                    rows.Remove(row);
                    shown.Remove(row.Id);
                }
            }

            // Put the wanted rows in order, leaving fading rows where they are.
            var entering = new List<Server>();
            var position = 0;
            foreach (var want in wanted)
            {
                var index = IndexOfActive(position);
                if (index < rows.Count && ReferenceEquals(rows[index], want))
                {
                    position++;
                    continue;
                }

                var current = rows.IndexOf(want);
                if (current >= 0)
                {
                    rows.Move(current, current < index ? index - 1 : index);
                }
                else
                {
                    rows.Insert(index, want);
                    shown[want.Id] = want;
                    entering.Add(want);
                }

                position++;
            }

            if (!animate)
            {
                return;
            }

            List.UpdateLayout();
            var after = RowPositions();
            foreach (var row in entering)
            {
                if (List.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
                {
                    Motion.ScaleHeight(item, 0, 1, 220);
                    Motion.Fade(item, 0, 1, 260);
                }
            }

            foreach (var pair in after)
            {
                if (before.TryGetValue(pair.Key, out var oldY) && !entering.Contains(pair.Key) &&
                    List.ItemContainerGenerator.ContainerFromItem(pair.Key) is ListBoxItem item)
                {
                    Motion.SlideFrom(item, oldY - pair.Value, 240);
                }
            }
        }

        /// <summary>Index in <see cref="rows"/> of the n-th row that isn't fading out.</summary>
        private int IndexOfActive(int n)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (!rows[i].IsLeaving && n-- == 0)
                {
                    return i;
                }
            }

            return rows.Count;
        }

        /// <summary>Where each on-screen row currently sits, for sliding moved rows into place.</summary>
        private Dictionary<Server, double> RowPositions()
        {
            var positions = new Dictionary<Server, double>();
            foreach (var row in rows)
            {
                if (List.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item && item.IsVisible)
                {
                    positions[row] = item.TranslatePoint(new Point(0, 0), List).Y;
                }
            }

            return positions;
        }

        // ---------- pull to refresh ----------

        /// <summary>Wheel travel past the edge, in wheel units (120 a notch), that triggers a refresh.</summary>
        private const double PullThreshold = 300;
        private const double PullMaxShift = 84;
        private static readonly TimeSpan PullGap = TimeSpan.FromMilliseconds(180);
        private ScrollViewer listScroll;
        private DispatcherTimer pullRelease;
        private double pull;
        private bool pullArmed;
        private DateTime lastWheelUtc;

        /// <summary>
        /// Scrolling on past the top or bottom pulls the list like a rubber band. Only a fresh gesture
        /// counts: scrolling hard into the edge never carries through, and pausing lets the pull go.
        /// </summary>
        private void OnListWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = listScroll ?? (listScroll = FindChild<ScrollViewer>(List));
            if (sv == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var freshGesture = now - lastWheelUtc > PullGap;
            lastWheelUtc = now;
            var atTop = sv.VerticalOffset <= 0.5;
            var atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 0.5;

            if (pull != 0)
            {
                // Mid-pull: pulling further or easing back, never scrolling.
                var next = pull + e.Delta;
                pull = Math.Sign(next) == Math.Sign(pull) ? next : 0;
                e.Handled = true;
                ShowPull();
                RestartPullRelease();
                return;
            }

            var intoEdge = (e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom);
            if (!intoEdge)
            {
                pullArmed = false;
                return;
            }

            e.Handled = true;
            if (freshGesture || pullArmed)
            {
                pullArmed = true;
                pull = e.Delta;
                ShowPull();
                RestartPullRelease();
            }
        }

        private void RestartPullRelease()
        {
            if (pullRelease == null)
            {
                pullRelease = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                pullRelease.Tick += (s, e) => ReleasePull();
            }

            pullRelease.Stop();
            pullRelease.Start();
        }

        private void ShowPull()
        {
            var amount = Math.Abs(pull);
            var shift = PullMaxShift * (1 - Math.Exp(-amount / 220));
            PullShift.BeginAnimation(TranslateTransform.YProperty, null);
            PullShift.Y = pull > 0 ? shift : -shift;
            var ready = amount >= PullThreshold;
            var label = ready ? "RELEASE TO REFRESH" : "PULL TO REFRESH";
            var progress = Math.Min(1, amount / PullThreshold);
            var (panel, text, icon) = pull > 0 ? (PullTop, PullTopText, PullTopIcon) : (PullBottom, PullBottomText, PullBottomIcon);
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = Math.Min(1, amount / 120);
            text.Text = label;
            text.Foreground = (Brush)FindResource(ready ? "TextBrush" : "TextDimBrush");
            ((RotateTransform)icon.RenderTransform).Angle = progress * 270;
        }

        private void ReleasePull()
        {
            pullRelease.Stop();
            var trigger = Math.Abs(pull) >= PullThreshold && !Busy;
            var panel = pull > 0 ? PullTop : PullBottom;
            var spring = new DoubleAnimation(0, TimeSpan.FromMilliseconds(trigger ? 320 : 220)) { EasingFunction = Motion.Out };
            PullShift.BeginAnimation(TranslateTransform.YProperty, spring);
            Motion.Fade(panel, panel.Opacity, 0, 260);
            pull = 0;
            pullArmed = false;
            if (trigger)
            {
                sounds.Play(Cue.Pull);
                _ = UpdateAsync(true);
            }
        }

        /// <summary>Stops Windows' "whole window bounces" effect when a touch scroll hits the edge.</summary>
        private void OnBoundaryFeedback(object sender, ManipulationBoundaryFeedbackEventArgs e) => e.Handled = true;

        private static T FindChild<T>(DependencyObject parent)
            where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                var deeper = FindChild<T>(child);
                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        // ---------- sounds ----------

        private void OnSoundToggle(object sender, RoutedEventArgs e)
        {
            sounds.Enabled = !sounds.Enabled;
            ShowSoundState();
            QueueSave();
            if (sounds.Enabled)
            {
                sounds.Play(Cue.Copy);
            }
        }

        private void ShowSoundState()
        {
            SoundButton.Content = sounds.Enabled ? "" : "";
            SoundButton.ToolTip = sounds.Enabled ? "Sounds on. Click to mute." : "Sounds muted. Click to turn on.";
        }

        // ---------- status ----------

        private void ShowDefaultStatus()
        {
            if (lastError != null)
            {
                Status.Text = lastError;
                Status.Foreground = (Brush)FindResource("RedHotBrush");
                return;
            }

            Status.Foreground = (Brush)FindResource("TextDimBrush");
            if (Busy)
            {
                Status.Text = doneRequests < pendingRequests
                    ? "Asking Steam…  " + doneRequests + " of " + pendingRequests + " searches"
                    : "Measuring ping through Steam's relays…";
                return;
            }

            var pool = browser.Pool().Count;
            var updated = browser.UpdatedUtc();
            var players = playersOnline.HasValue ? playersOnline.Value.ToString("N0", CultureInfo.CurrentCulture) + " playing now  ·  " : "";
            Status.Text = players + pool + " servers from Steam" + (updated.HasValue ? "  ·  updated " + Ago(DateTime.UtcNow - updated.Value) : "");
        }

        private static string Ago(TimeSpan age) =>
            age.TotalSeconds < 5 ? "just now"
            : age.TotalSeconds < 60 ? (int)age.TotalSeconds + " s ago"
            : age.TotalMinutes < 60 ? (int)age.TotalMinutes + " min ago"
            : "over an hour ago";

        private void Say(string text, bool problem = false)
        {
            Status.Text = text;
            Status.Foreground = (Brush)FindResource(problem ? "RedHotBrush" : "TextBrush");
            Motion.Fade(Status, 0, 1, 220);
            messageTimer.Stop();
            messageTimer.Start();
        }

        // ---------- filters ----------

        private void BuildChips()
        {
            foreach (var mode in Catalog.Modes)
            {
                ModeChips.Children.Add(MakeChip(mode, mode, browser.Filters.Modes, ModeTip(mode)));
            }

            foreach (var map in Catalog.Maps.Concat(browser.Filters.Maps.Where(m => !Catalog.Maps.Contains(m))))
            {
                MapChips.Children.Add(MakeChip(Catalog.MapName(map), map, browser.Filters.Maps, null));
            }

            foreach (var limit in PingLimits)
            {
                var chip = new ToggleButton
                {
                    Style = (Style)FindResource("Chip"),
                    Content = "≤ " + limit + " ms",
                    Tag = limit,
                    IsChecked = browser.Filters.MaxPing == limit,
                    ToolTip = "Hide servers with a higher ping",
                };
                chip.Checked += (s, e) => OnPingChip(limit, true);
                chip.Unchecked += (s, e) => OnPingChip(limit, false);
                PingChips.Children.Add(chip);
            }

            BotChips.Children.Add(MakeChip("On", "true", browser.Filters.Bots, "Servers that fill empty slots with bots"));
            BotChips.Children.Add(MakeChip("Off", "false", browser.Filters.Bots, "Servers without bots"));

            foreach (var limit in FilterState.AgeLimits)
            {
                var chip = new ToggleButton
                {
                    Style = (Style)FindResource("Chip"),
                    Content = FilterState.AgeLimitText(limit),
                    Tag = limit,
                    IsChecked = browser.Filters.AgeLimit == limit,
                    ToolTip = limit > 0 ? "Servers created recently, likely still filling up" : "Long-running servers, likely with a settled crowd",
                };
                chip.Checked += (s, e) => OnAgeChip(limit, true);
                chip.Unchecked += (s, e) => OnAgeChip(limit, false);
                AgeChips.Children.Add(chip);
            }
        }

        private static string ModeTip(string mode)
        {
            switch (mode)
            {
                case Catalog.Training:
                    return "Private shooting ranges. Hidden unless selected.";
                case "Wingman":
                    return "2v2. Matches can't be joined once they have started.";
                case "Zombie":
                    return "Co-op against zombies, up to 4 players.";
                default:
                    return null;
            }
        }

        private ToggleButton MakeChip(object content, string value, HashSet<string> set, string tip)
        {
            var chip = new ToggleButton { Style = (Style)FindResource("Chip"), Content = content, Tag = value, IsChecked = set.Contains(value) };
            if (tip != null)
            {
                chip.ToolTip = tip;
            }

            chip.Checked += (s, e) => OnChip(set, value, true);
            chip.Unchecked += (s, e) => OnChip(set, value, false);
            return chip;
        }

        private void OnChip(HashSet<string> set, string value, bool on)
        {
            if (quiet)
            {
                return;
            }

            if (on)
            {
                set.Add(value);
            }
            else
            {
                set.Remove(value);
            }

            UpdateFilterSummary();
            Render();
            if (set != browser.Filters.Regions)
            {
                // Mode, map and bots can need new Steam searches; wait for clicks to settle.
                debounce.Stop();
                debounce.Start();
            }
        }

        private void OnRegionChip(IEnumerable<string> codes, bool on)
        {
            if (quiet)
            {
                return;
            }

            foreach (var code in codes)
            {
                if (on)
                {
                    browser.Filters.Regions.Add(code);
                }
                else
                {
                    browser.Filters.Regions.Remove(code);
                }
            }

            UpdateFilterSummary();
            Render();
        }

        private void OnAgeChip(int limit, bool on)
        {
            if (quiet)
            {
                return;
            }

            browser.Filters.AgeLimit = on ? limit : 0;
            if (on)
            {
                AgePopup.IsOpen = false;
            }

            SyncChips();
            UpdateFilterSummary();
            Render();
        }

        private void OnPingChip(int limit, bool on)
        {
            if (quiet)
            {
                return;
            }

            quiet = true;
            foreach (var chip in PingChips.Children.OfType<ToggleButton>())
            {
                chip.IsChecked = on && (int)chip.Tag == limit;
            }

            quiet = false;
            browser.Filters.MaxPing = on ? limit : 0;
            if (on)
            {
                PingPopup.IsOpen = false;
            }

            UpdateFilterSummary();
            Render();
        }

        private void UpdateRegionChips()
        {
            var regions = browser.Regions();
            var codes = regions.Select(r => r.Code).ToList();
            codes.AddRange(browser.Filters.Regions.Where(r => !codes.Contains(r, StringComparer.OrdinalIgnoreCase)));
            var key = string.Join(",", codes) + "|" + string.Join(",", regions.Select(r => r.Count + ":" + r.Ping));
            if (key == regionChipKeys)
            {
                return;
            }

            regionChipKeys = key;
            quiet = true;
            RegionChips.Children.Clear();

            // One chip per city: Steam can run several relays in one place (sto, sto2).
            foreach (var city in codes.GroupBy(Catalog.RegionName, StringComparer.CurrentCultureIgnoreCase))
            {
                var members = city.ToList();
                var infos = regions.Where(r => members.Contains(r.Code, StringComparer.OrdinalIgnoreCase)).ToList();
                var count = infos.Sum(r => r.Count);
                var ping = infos.Where(r => r.Ping >= 0).Select(r => r.Ping).DefaultIfEmpty(Server.NoPing).Min();
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = city.Key });
                content.Children.Add(new TextBlock { Text = count.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(7, 0, 0, 0), Opacity = 0.6 });
                var chip = new ToggleButton
                {
                    Style = (Style)FindResource("Chip"),
                    Content = content,
                    Tag = members,
                    IsChecked = members.Any(browser.Filters.Regions.Contains),
                    ToolTip = "Steam relay " + string.Join(", ", members.Select(c => c.ToUpperInvariant())) + (ping >= 0 ? "  ·  best ping " + ping + " ms" : ""),
                };
                chip.Checked += (s, e) => OnRegionChip(members, true);
                chip.Unchecked += (s, e) => OnRegionChip(members, false);
                RegionChips.Children.Add(chip);
            }

            if (codes.Count == 0)
            {
                RegionChips.Children.Add(new TextBlock
                {
                    Text = "Regions appear once servers load.",
                    Foreground = (Brush)FindResource("TextFaintBrush"),
                    Margin = new Thickness(0, 4, 0, 10),
                });
            }

            quiet = false;
        }

        /// <summary>Shows each category's selection on its filter-bar button, red-outlined when active.</summary>
        private void UpdateFilterSummary()
        {
            var f = browser.Filters;
            ShowFilter(ModeButton, ModeValue, Catalog.Modes.Where(f.Modes.Contains).ToList());
            ShowFilter(MapButton, MapValue, f.Maps.Select(Catalog.MapName).OrderBy(m => m).ToList());
            ShowFilter(RegionButton, RegionValue, f.Regions.Select(Catalog.RegionName).Distinct().OrderBy(r => r).ToList());
            ShowFilter(AgeButton, AgeValue, f.AgeLimit != 0 ? new List<string> { FilterState.AgeLimitText(f.AgeLimit) } : new List<string>());
            ShowFilter(PingButton, PingValue, f.MaxPing > 0 ? new List<string> { "≤ " + f.MaxPing + " ms" } : new List<string>());
            ShowFilter(BotButton, BotValue, f.OneSidedBots != null ? new List<string> { f.OneSidedBots == "true" ? "On" : "Off" } : new List<string>());
            var active = f.ActiveCount + (f.IsHostSearch ? 1 : 0);
            ResetButton.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueSave();
        }

        private static void ShowFilter(Button button, TextBlock value, List<string> selected)
        {
            value.Text = selected.Count == 0 ? "Any" : selected.Count <= 2 ? string.Join(", ", selected) : selected[0] + " +" + (selected.Count - 1);
            button.Tag = selected.Count > 0 ? "on" : null;
        }

        private readonly Dictionary<Popup, DateTime> popupClosedUtc = new Dictionary<Popup, DateTime>();

        private Popup PopupFor(Button button) =>
            button == ModeButton ? ModePopup : button == MapButton ? MapPopup : button == RegionButton ? RegionPopup
            : button == AgeButton ? AgePopup : button == PingButton ? PingPopup : BotPopup;

        private void OnFilterButton(object sender, RoutedEventArgs e)
        {
            var popup = PopupFor((Button)sender);

            // Clicking the button of an open dropdown closes it (the click itself already dismissed it).
            if (popupClosedUtc.TryGetValue(popup, out var closed) && DateTime.UtcNow - closed < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            popup.IsOpen = true;
            if (popup.Child is FrameworkElement panel)
            {
                Motion.Fade(panel, 0, 1, 140);
                Motion.SlideFrom(panel, -6, 160);
            }
        }

        private void OnFilterPopupClosed(object sender, EventArgs e) => popupClosedUtc[(Popup)sender] = DateTime.UtcNow;

        private void CloseFilterPopups()
        {
            foreach (var p in new[] { ModePopup, MapPopup, RegionPopup, AgePopup, PingPopup, BotPopup })
            {
                p.IsOpen = false;
            }
        }

        private void OnClearCategory(object sender, RoutedEventArgs e)
        {
            var f = browser.Filters;
            var kind = (string)((Button)sender).Tag;
            switch (kind)
            {
                case "mode":
                    f.Modes.Clear();
                    break;
                case "map":
                    f.Maps.Clear();
                    break;
                case "region":
                    f.Regions.Clear();
                    break;
                case "age":
                    f.AgeLimit = 0;
                    break;
                case "ping":
                    f.MaxPing = 0;
                    break;
                case "bot":
                    f.Bots.Clear();
                    break;
            }

            CloseFilterPopups();
            SyncChips();
            UpdateFilterSummary();
            Render();
            if (kind == "mode" || kind == "map" || kind == "bot")
            {
                debounce.Stop();
                debounce.Start();
            }
        }
        private void OnReset(object sender, RoutedEventArgs e) => ResetFilters();

        private void ResetFilters()
        {
            quiet = true;
            foreach (var panel in new[] { ModeChips, MapChips, RegionChips, PingChips, BotChips, AgeChips })
            {
                foreach (var chip in panel.Children.OfType<ToggleButton>())
                {
                    chip.IsChecked = false;
                }
            }

            browser.Filters.Reset();
            SearchBox.Text = "";
            quiet = false;
            UpdateFilterSummary();
            _ = UpdateAsync(false);
        }

        // ---------- search ----------

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            var f = browser.Filters;
            f.Text = SearchBox.Text;
            ClearSearch.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (quiet)
            {
                return;
            }

            if (f.IsHostSearch && !string.Equals(SearchBox.Text.Trim(), f.ExactHost, StringComparison.Ordinal))
            {
                f.ExactHost = "";
                UpdateFilterSummary();
                _ = UpdateAsync(false);
                return;
            }

            UpdateFilterSummary();
            Render();
        }

        private void OnSearchKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SearchEveryServer(SearchBox.Text);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (SearchBox.Text.Length > 0)
                {
                    SearchBox.Text = "";
                }
                else
                {
                    List.Focus();
                }
            }
            else if (e.Key == Key.Down && List.Items.Count > 0)
            {
                e.Handled = true;
                List.SelectedIndex = Math.Max(0, List.SelectedIndex);
                FocusSelectedRow();
            }
        }

        private void SearchEveryServer(string name)
        {
            var text = (name ?? "").Trim();
            if (text.Length == 0)
            {
                return;
            }

            browser.Filters.ExactHost = text;
            UpdateFilterSummary();
            _ = UpdateAsync(true);
        }

        private void OnClearSearch(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        private void OnSearchHost(object sender, RoutedEventArgs e)
        {
            if (List.SelectedItem is Server s)
            {
                quiet = true;
                SearchBox.Text = s.Name;
                quiet = false;
                SearchEveryServer(s.Name);
            }
        }

        private void OnEmptyAction(object sender, RoutedEventArgs e)
        {
            if (lastError != null)
            {
                _ = UpdateAsync(true);
            }
            else if (browser.Filters.IsHostSearch)
            {
                SearchBox.Text = "";
            }
            else if (!string.IsNullOrWhiteSpace(browser.Filters.Text))
            {
                SearchEveryServer(browser.Filters.Text);
            }
            else
            {
                ResetFilters();
            }
        }

        // ---------- sorting ----------

        private void OnSort(object sender, RoutedEventArgs e)
        {
            var key = (SortKey)Enum.Parse(typeof(SortKey), (string)((Button)sender).Tag);
            if (sortKey != key)
            {
                sortKey = key;
                sortDescending = false;
            }
            else if (!sortDescending && key != SortKey.Ping)
            {
                sortDescending = true;
            }
            else
            {
                // Third click (or a second on ping) returns to the default: lowest ping first.
                sortKey = SortKey.Ping;
                sortDescending = false;
            }

            UpdateHeaders();
            Render();
            QueueSave();
        }

        private void UpdateHeaders()
        {
            HeadPing.Content = HeaderText(SortKey.Ping, "PING");
            HeadMode.Content = HeaderText(SortKey.Mode, "MODE");
            HeadAge.Content = HeaderText(SortKey.Age, "AGE");
            HeadMap.Content = HeaderText(SortKey.Map, "MAP");
            HeadRegion.Content = HeaderText(SortKey.Region, "REGION");
            HeadBots.Content = HeaderText(SortKey.Bots, "BOT");
            HeadSlots.Content = HeaderText(SortKey.Slots, "SLOTS");
        }

        private string HeaderText(SortKey key, string label) =>
            sortKey == key ? label + (sortDescending ? "  ▼" : "  ▲") : label;

        // ---------- actions ----------

        private void UpdateActions()
        {
            var any = List.SelectedItem != null;
            JoinButton.IsEnabled = any;
            CopyButton.IsEnabled = any;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

        private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // A double-click on a filterable value is two filter toggles, never a join.
            if (e.OriginalSource is DependencyObject d && FilterCell(d) == null && ItemsControl.ContainerFromElement(List, d) is ListBoxItem)
            {
                _ = JoinAsync();
            }
        }

        // ---------- click to filter ----------

        private static FrameworkElement FilterCell(DependencyObject d)
        {
            while (d != null && !(d is ListBoxItem))
            {
                if (d is FrameworkElement fe && fe.Tag is string kind && (FilterKinds.Contains(kind) || kind == "star"))
                {
                    return fe;
                }

                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }

            return null;
        }

        private void OnCellClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement cell && cell.DataContext is Server s)
            {
                ToggleFilter((string)cell.Tag, s);
            }
        }

        private void OnCellTip(object sender, ToolTipEventArgs e)
        {
            if (!(sender is FrameworkElement cell) || !(cell.DataContext is Server s) || FilterLabel((string)cell.Tag, s) == null)
            {
                e.Handled = true;
                return;
            }

            cell.ToolTip = FilterAction((string)cell.Tag, s);
        }

        /// <summary>What a click on this value filters by, or null when it can't filter.</summary>
        private static string FilterLabel(string kind, Server s)
        {
            switch (kind)
            {
                case "mode":
                    return s.Mode.Length > 0 ? s.Mode : null;
                case "map":
                    return s.Map.Length > 0 ? s.MapName : null;
                case "bots":
                    return s.Bots == "true" ? "bots on" : s.Bots == "false" ? "bots off" : null;
                case "region":
                    return s.Region.Length > 0 ? s.RegionName : null;
                case "age":
                    var age = FilterState.AgeLimitFor(s.AgeMinutes);
                    return age != 0 ? "age " + FilterState.AgeLimitText(age).ToLowerInvariant() : null;
                case "ping":
                    var limit = FilterState.LimitFor(s.Ping);
                    return limit > 0 ? "ping ≤ " + limit + " ms" : null;
                default:
                    return null;
            }
        }

        private bool IsFilterActive(string kind, Server s)
        {
            var f = browser.Filters;
            switch (kind)
            {
                case "mode":
                    return f.Modes.Contains(s.Mode);
                case "map":
                    return f.Maps.Contains(s.Map);
                case "bots":
                    return f.OneSidedBots == s.Bots;
                case "region":
                    return f.Regions.Contains(s.Region);
                case "age":
                    return f.AgeLimit != 0 && f.AgeLimit == FilterState.AgeLimitFor(s.AgeMinutes);
                case "ping":
                    return f.MaxPing > 0 && f.MaxPing == FilterState.LimitFor(s.Ping);
                default:
                    return false;
            }
        }

        private string FilterAction(string kind, Server s)
        {
            var label = FilterLabel(kind, s);
            return IsFilterActive(kind, s) ? "Remove the " + label + " filter" : "Filter by " + label;
        }

        private void ToggleFilter(string kind, Server s)
        {
            var label = FilterLabel(kind, s);
            if (label == null)
            {
                return;
            }

            var f = browser.Filters;
            var removing = IsFilterActive(kind, s);
            switch (kind)
            {
                case "mode":
                    Toggle(f.Modes, s.Mode, removing);
                    break;
                case "map":
                    Toggle(f.Maps, s.Map, removing);
                    break;
                case "bots":
                    f.Bots.Clear();
                    if (!removing)
                    {
                        f.Bots.Add(s.Bots);
                    }

                    break;
                case "region":
                    // One city can have several relays (sto, sto2): toggle them together, like the chip.
                    var codes = browser.Regions().Select(r => r.Code).Append(s.Region)
                        .Where(c => Catalog.RegionName(c) == s.RegionName).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var code in codes)
                    {
                        Toggle(f.Regions, code, removing);
                    }

                    break;
                case "age":
                    f.AgeLimit = removing ? 0 : FilterState.AgeLimitFor(s.AgeMinutes);
                    break;
                case "ping":
                    f.MaxPing = removing ? 0 : FilterState.LimitFor(s.Ping);
                    break;
            }

            SyncChips();
            UpdateFilterSummary();
            Render();
            Say(removing ? "Removed the " + label + " filter." : "Filtering by " + label + ". Click it again, or Reset, to undo.");
            if (kind == "mode" || kind == "map" || kind == "bots")
            {
                debounce.Stop();
                debounce.Start();
            }
        }

        private static void Toggle(HashSet<string> set, string value, bool remove)
        {
            if (remove)
            {
                set.Remove(value);
            }
            else
            {
                set.Add(value);
            }
        }

        /// <summary>Makes every chip show the current filters after a change made elsewhere.</summary>
        private void SyncChips()
        {
            var f = browser.Filters;
            quiet = true;
            foreach (var chip in ModeChips.Children.OfType<ToggleButton>())
            {
                chip.IsChecked = f.Modes.Contains((string)chip.Tag);
            }

            foreach (var chip in MapChips.Children.OfType<ToggleButton>())
            {
                chip.IsChecked = f.Maps.Contains((string)chip.Tag);
            }

            foreach (var chip in BotChips.Children.OfType<ToggleButton>())
            {
                chip.IsChecked = f.Bots.Contains((string)chip.Tag);
            }

            foreach (var chip in PingChips.Children.OfType<ToggleButton>())
            {
                chip.IsChecked = (int)chip.Tag == f.MaxPing;
            }

            foreach (var chip in AgeChips.Children.OfType<ToggleButton>())
            {
                chip.IsChecked = (int)chip.Tag == f.AgeLimit;
            }

            foreach (var chip in RegionChips.Children.OfType<ToggleButton>())
            {
                chip.IsChecked = chip.Tag is List<string> codes && codes.Any(f.Regions.Contains);
            }

            quiet = false;
        }

        private void OnRowMenuOpening(object sender, ContextMenuEventArgs e)
        {
            foreach (var old in RowMenu.Items.OfType<FrameworkElement>().Where(i => "filter".Equals(i.Tag)).ToList())
            {
                RowMenu.Items.Remove(old);
            }

            if (!(List.SelectedItem is Server s))
            {
                return;
            }

            var follow = new MenuItem { Header = s.IsFollowed ? "Unfollow host" : "Follow host", Tag = "filter" };
            follow.Click += (o, a) => ToggleFollow(s);
            RowMenu.Items.Add(follow);

            var separator = new Separator { Tag = "filter" };
            RowMenu.Items.Add(separator);
            foreach (var kind in FilterKinds)
            {
                if (FilterLabel(kind, s) == null)
                {
                    continue;
                }

                var item = new MenuItem { Header = FilterAction(kind, s), Tag = "filter" };
                var k = kind;
                item.Click += (o, a) => ToggleFilter(k, s);
                RowMenu.Items.Add(item);
            }
        }

        // ---------- friends, follows, new servers ----------

        /// <summary>Marks friends' and followed hosts' servers and new arrivals, then checks for followed hosts coming online.</summary>
        private void Annotate()
        {
            var now = DateTime.UtcNow;
            var friends = demo ? DemoData.Friends : client.Friends;
            var all = browser.All();
            foreach (var s in all)
            {
                s.IsFriend = friends.TryGetValue(s.Owner, out var friendName);
                s.FriendName = friendName ?? "";
                s.IsFollowed = follows.Contains(s.Owner);
                var seen = browser.FirstSeenUtc(s.Id);
                s.IsNew = baselineUtc.HasValue && seen > baselineUtc.Value && now - seen < NewFor;
                s.AgeMinutes = demo ? DemoData.AgeFor(s.Id) : lobbyAge.AgeMinutes(LobbyAge.CounterOf(s.Id), now) ?? -1;
                s.IsNewRound = demo ? DemoData.IsNewRound(s.Id) : now - browser.RoundStartUtc(s.Id) < NewRoundFor;
            }

            follows.Refresh(all);
            if (!Busy && !watching)
            {
                // Only on complete results: a refresh in progress briefly empties the list.
                CheckFollowedOnline(all.Concat(watchFound.Values), now);
            }
        }

        /// <summary>
        /// Alerts when a followed host's server appears after being gone for a while. Hosts already
        /// online at start, or seen within the last few minutes, don't alert.
        /// </summary>
        private void CheckFollowedOnline(IEnumerable<Server> servers, DateTime now)
        {
            if (baselineUtc == null)
            {
                return;
            }

            var online = servers.Where(s => follows.Contains(s.Owner)).GroupBy(s => s.Owner).Select(g => g.First()).ToList();
            var appeared = online.Where(s => !followedSeen.TryGetValue(s.Owner, out var last) || now - last > GoneAfter).ToList();
            foreach (var s in online)
            {
                followedSeen[s.Owner] = now;
            }

            if (followSeenReady && appeared.Count > 0)
            {
                Alert(appeared);
            }

            followSeenReady = true;
        }

        private void Alert(List<Server> appeared)
        {
            var first = appeared[0];
            var text = appeared.Count == 1
                ? "★ " + first.Name + " is hosting " + first.Mode + " on " + first.MapName + "."
                : "★ " + string.Join(", ", appeared.Select(s => s.Name)) + " are hosting.";
            Say(text);
            sounds.Play(Cue.Alert);
            if (!IsActive)
            {
                FlashTaskbar();
            }
        }

        private void OnStarClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is Server s)
            {
                ToggleFollow(s);
            }
        }

        private void ToggleFollow(Server s)
        {
            var on = follows.Toggle(s.Owner, s.Name);
            if (on)
            {
                // Already online when followed: no alert for it now.
                followedSeen[s.Owner] = DateTime.UtcNow;
            }

            Render();
            QueueSave();
            Say(on
                ? "Following " + s.Name + ". Their server stays on top, and Dispatch alerts you when it appears."
                : "Stopped following " + s.Name + ".");
        }

        private async void OnWatchTick()
        {
            if (Busy || watching)
            {
                return;
            }

            if (autoRefresh)
            {
                await UpdateAsync(true);
            }

            if (follows.Hosts.Count > 0 && !demo && !Busy)
            {
                await WatchAsync();
            }
        }

        /// <summary>A quiet check of followed hosts only, so alerts work without refreshing the whole list.</summary>
        private async Task WatchAsync()
        {
            watching = true;
            try
            {
                var queries = follows.Hosts.Take(10).Select(h => LobbyQuery.HostId(h.Owner)).ToList();
                var found = new Dictionary<string, Server>();
                await client.RunAsync(queries, (i, servers) =>
                {
                    lock (found)
                    {
                        foreach (var s in servers)
                        {
                            found[s.Owner] = s;
                        }
                    }
                }, null, CancellationToken.None);
                watchFound.Clear();
                foreach (var p in found)
                {
                    watchFound[p.Key] = p.Value;
                }

                Render();
            }
            catch (Exception e) when (e is LookupException || e is LocatorException)
            {
                // The next tick tries again; nothing to tell the user.
            }
            finally
            {
                watching = false;
            }
        }

        private void OnAutoChanged(object sender, RoutedEventArgs e)
        {
            if (quiet)
            {
                return;
            }

            autoRefresh = AutoToggle.IsChecked == true;
            QueueSave();
            Say(autoRefresh
                ? "Auto-refresh on: every minute while Dispatch is open. New servers get a red mark."
                : "Auto-refresh off.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        private void FlashTaskbar()
        {
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(FlashInfo)),
                Window = new WindowInteropHelper(this).Handle,
                Flags = 0x02 | 0x0C, // taskbar button, until the window comes to the front
                Count = 0,
                Timeout = 0,
            };
            FlashWindowEx(ref info);
        }

        // ---------- recent servers ----------

        private void OnRecent(object sender, RoutedEventArgs e)
        {
            RecentMenu.Items.Clear();
            if (recent.Items.Count == 0)
            {
                RecentMenu.Items.Add(new MenuItem { Header = "No recent servers yet. Servers you join appear here.", IsEnabled = false });
            }

            foreach (var r in recent.Items)
            {
                var item = new MenuItem
                {
                    Header = r.Name + "   ·   " + r.Mode + " · " + Catalog.MapName(r.Map),
                    InputGestureText = Ago(DateTime.UtcNow - r.JoinedUtc),
                };
                var entry = r;
                item.Click += (o, a) => _ = RejoinAsync(entry);
                RecentMenu.Items.Add(item);
            }

            RecentMenu.PlacementTarget = RecentButton;
            RecentMenu.IsOpen = true;
        }

        /// <summary>Rejoins by host: a host keeps their server across map changes, and re-hosting gives a new lobby, so ask Steam first.</summary>
        private async Task RejoinAsync(RecentServer r)
        {
            if (demo)
            {
                Say("Demo mode: joining is off.");
                return;
            }

            if (!ConfirmLaunch())
            {
                return;
            }

            Say("Looking for " + r.Name + "'s server…");
            string link;
            Server current = null;
            try
            {
                var now = await client.FetchAsync(LobbyQuery.HostId(r.Owner), CancellationToken.None);
                current = now.FirstOrDefault();
                if (current == null)
                {
                    Say(r.Name + " is not hosting right now.", true);
                    return;
                }

                link = current.Link;
            }
            catch (Exception e) when (e is LookupException || e is LocatorException)
            {
                link = r.Link;
            }

            if (Open(link))
            {
                if (current != null)
                {
                    recent.Add(current, DateTime.UtcNow);
                    QueueSave();
                }

                sounds.Play(Cue.Join);
                Say("Rejoining " + r.Name + "…  watch the game.");
            }
        }

        // ---------- update check ----------

        /// <summary>At most every 12 hours; silent on any failure, and works only once the repository is public.</summary>
        private async Task CheckForUpdateAsync()
        {
            if (demo || settings.Get("updateCheck", "1") == "0")
            {
                return;
            }

            var current = Assembly.GetExecutingAssembly().GetName().Version;
            if (UpdateCheck.TryVersion(settings.Get("updateTag"), out var cached))
            {
                ShowUpdate(new ReleaseInfo(cached, settings.Get("updateTag"), settings.Get("updateUrl")), current);
            }

            long.TryParse(settings.Get("updateCheckedAt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks);
            if (DateTime.UtcNow - new DateTime(Math.Max(0, ticks), DateTimeKind.Utc) < UpdateCheck.Interval)
            {
                return;
            }

            var latest = await UpdateCheck.LatestAsync();
            settings.Set("updateCheckedAt", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            if (latest != null)
            {
                settings.Set("updateTag", latest.Tag);
                settings.Set("updateUrl", latest.Url);
                ShowUpdate(latest, current);
            }

            QueueSave();
        }

        private void ShowUpdate(ReleaseInfo latest, Version current)
        {
            var newer = UpdateCheck.IsNewer(latest, current) && latest.Url.StartsWith("https://github.com/", StringComparison.Ordinal);
            latestRelease = newer ? latest : null;
            UpdateLink.Content = newer ? "Update available: " + latest.Tag : "";
            UpdateLink.ToolTip = newer ? "Open the release page on GitHub" : null;
            UpdateLink.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnUpdate(object sender, RoutedEventArgs e)
        {
            if (latestRelease != null)
            {
                Open(latestRelease.Url);
            }
        }

        private void QueueSave()
        {
            if (saveTimer == null)
            {
                return;
            }

            saveTimer.Stop();
            saveTimer.Start();
        }

        private void OnListKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = JoinAsync();
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                _ = CopyLinkAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                List.SelectedItem = null;
            }
        }

        private void OnWindowKey(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (e.Key == Key.Escape && new[] { ModePopup, MapPopup, RegionPopup, AgePopup, PingPopup, BotPopup }.Any(p => p.IsOpen))
            {
                e.Handled = true;
                CloseFilterPopups();
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = UpdateAsync(true);
            }
            else if (ctrl && e.Key == Key.F)
            {
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
            }
            else if (ctrl && shift && e.Key == Key.V)
            {
                e.Handled = true;
                JoinClipboard();
            }
        }

        private void OnJoin(object sender, RoutedEventArgs e) => _ = JoinAsync();

        private void OnCopyLink(object sender, RoutedEventArgs e) => _ = CopyLinkAsync();

        private void OnCopyName(object sender, RoutedEventArgs e)
        {
            if (List.SelectedItem is Server s && SetClipboard(s.Name))
            {
                Say("Host name copied.");
            }
        }

        private void OnJoinClipboard(object sender, RoutedEventArgs e) => JoinClipboard();

        private async Task JoinAsync()
        {
            if (!(List.SelectedItem is Server s))
            {
                Say("Select a server first.");
                return;
            }

            if (demo)
            {
                Say("Demo mode: joining is off.");
                return;
            }

            if (!ConfirmLaunch())
            {
                return;
            }

            var link = await FreshLinkAsync(s);
            if (link != null && Open(link))
            {
                recent.Add(s, DateTime.UtcNow);
                QueueSave();
                sounds.Play(Cue.Join);
                Say("Joining " + s.Name + "…  watch the game.");
            }
        }

        private async Task CopyLinkAsync()
        {
            if (!(List.SelectedItem is Server s))
            {
                Say("Select a server first.");
                return;
            }

            var link = await FreshLinkAsync(s);
            if (link != null && SetClipboard(link))
            {
                lastCopied = link;
                ClipboardButton.Visibility = Visibility.Collapsed;
                sounds.Play(Cue.Copy);
                Say("Invite link for " + s.Name + " copied. Your friend opens it with Bodycam running.");
            }
        }

        private void JoinClipboard()
        {
            if (!JoinLink.TryParse(GetClipboard(), out var link))
            {
                Say("No Bodycam invite link on the clipboard.", true);
                return;
            }

            if (ConfirmLaunch() && Open(link))
            {
                sounds.Play(Cue.Join);
                Say("Joining from the pasted link…  watch the game.");
            }
        }

        /// <summary>Re-checks a server that was listed a while ago, since hosts close and lobbies change.</summary>
        private async Task<string> FreshLinkAsync(Server s)
        {
            if (DateTime.UtcNow - s.SeenUtc < FreshFor)
            {
                return s.Link;
            }

            Say("Checking " + s.Name + " with Steam…");
            try
            {
                var now = await client.FetchAsync(LobbyQuery.HostId(s.Owner), CancellationToken.None);
                var match = now.FirstOrDefault(x => x.Id == s.Id) ?? now.FirstOrDefault();
                if (match == null)
                {
                    Say(s.Name + " is no longer hosting. Refresh and pick another server.", true);
                    return null;
                }

                return match.Link;
            }
            catch (Exception e) when (e is LookupException || e is LocatorException)
            {
                Say("Steam could not re-check the server, so the last known link is used.");
                return s.Link;
            }
        }

        /// <summary>Links opened with the game closed failed in testing, so a cold launch asks first.</summary>
        private bool ConfirmLaunch()
        {
            if (IsGameRunning() || DateTime.UtcNow < launchConfirmUntil)
            {
                launchConfirmUntil = DateTime.MinValue;
                return true;
            }

            launchConfirmUntil = DateTime.UtcNow.AddSeconds(8);
            Say("Bodycam isn't running. Joins work best from the main menu. Press again to launch anyway.", true);
            return false;
        }

        private static bool IsGameRunning()
        {
            var processes = Process.GetProcesses();
            try
            {
                return processes.Any(p => p.ProcessName.StartsWith("Bodycam", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                foreach (var p in processes)
                {
                    p.Dispose();
                }
            }
        }

        private bool Open(string link)
        {
            try
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
                return true;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Say("Could not open the Steam link: " + e.Message, true);
                return false;
            }
        }

        // ---------- clipboard ----------

        private void CheckClipboard()
        {
            var has = JoinLink.TryParse(GetClipboard(), out var link) && link != lastCopied;
            ClipboardButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string GetClipboard()
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                return null;
            }
        }

        private bool SetClipboard(string text)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (COMException)
                {
                    Thread.Sleep(40);
                }
            }

            Say("The clipboard is busy. Try again.", true);
            return false;
        }

        // ---------- window ----------

        private void FocusSelectedRow()
        {
            List.UpdateLayout();
            (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as ListBoxItem)?.Focus();
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnMaximize(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void OnWindowState()
        {
            var max = WindowState == WindowState.Maximized;
            Frame.Margin = max ? new Thickness(7) : new Thickness(0);
            Frame.BorderThickness = max ? new Thickness(0) : new Thickness(1);
            MaxButton.Content = max ? "" : "";
            MaxButton.ToolTip = max ? "Restore" : "Maximize";
        }

        private void OnRefresh(object sender, RoutedEventArgs e) => _ = UpdateAsync(true);

        private void RestoreSettings()
        {
            var f = browser.Filters;
            foreach (var m in settings.GetList("modes"))
            {
                f.Modes.Add(m);
            }

            foreach (var m in settings.GetList("maps"))
            {
                f.Maps.Add(m);
            }

            foreach (var b in settings.GetList("bots"))
            {
                f.Bots.Add(b);
            }

            foreach (var r in settings.GetList("regions"))
            {
                f.Regions.Add(r);
            }

            f.MaxPing = PingLimits.Contains((int)settings.GetDouble("maxPing", 0)) ? (int)settings.GetDouble("maxPing", 0) : 0;
            f.AgeLimit = FilterState.AgeLimits.Contains((int)settings.GetDouble("ageLimit", 0)) ? (int)settings.GetDouble("ageLimit", 0) : 0;
            lobbyAge.Load(settings.GetRecords("ageAnchors"), DateTime.UtcNow);

            if (Enum.TryParse(settings.Get("sort"), out SortKey key) && key != SortKey.Nearest)
            {
                sortKey = key;
                sortDescending = settings.Get("descending") == "1";
            }

            client.Location = settings.Get("pingLocation");
            if (long.TryParse(settings.Get("pingLocationTime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0)
            {
                client.LocationUtc = new DateTime(ticks, DateTimeKind.Utc);
            }

            follows.Load(settings.GetRecords("followed"));
            if (demo)
            {
                follows.Toggle(DemoData.FollowedOwner, DemoData.FollowedName);
            }
            recent.Load(settings.GetRecords("recent"));
            autoRefresh = settings.Get("autoRefresh") == "1";
            sounds.Enabled = settings.Get("sounds", "1") == "1";
            ShowSoundState();
            quiet = true;
            AutoToggle.IsChecked = autoRefresh;
            quiet = false;


            var width = settings.GetDouble("width", Width);
            var height = settings.GetDouble("height", Height);
            var left = settings.GetDouble("left", double.NaN);
            var top = settings.GetDouble("top", double.NaN);
            Width = Math.Max(MinWidth, Math.Min(width, SystemParameters.VirtualScreenWidth));
            Height = Math.Max(MinHeight, Math.Min(height, SystemParameters.VirtualScreenHeight));
            if (!double.IsNaN(left) && !double.IsNaN(top) &&
                left >= SystemParameters.VirtualScreenLeft - 40 && top >= SystemParameters.VirtualScreenTop - 10 &&
                left + 120 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                top + 60 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }

            if (settings.Get("maximized") == "1")
            {
                WindowState = WindowState.Maximized;
            }
        }

        private void SaveSettings()
        {
            var f = browser.Filters;
            settings.SetList("modes", f.Modes);
            settings.SetList("maps", f.Maps);
            settings.SetList("bots", f.Bots);
            settings.SetList("regions", f.Regions);
            settings.SetDouble("maxPing", f.MaxPing);
            settings.SetDouble("ageLimit", f.AgeLimit);
            settings.SetRecords("ageAnchors", lobbyAge.ToRecords());
            settings.Set("sort", sortKey.ToString());
            settings.Set("descending", sortDescending ? "1" : "0");
            settings.SetRecords("followed", follows.ToRecords());
            settings.SetRecords("recent", recent.ToRecords());
            settings.Set("autoRefresh", autoRefresh ? "1" : "0");
            settings.Set("sounds", sounds.Enabled ? "1" : "0");
            if (!string.IsNullOrEmpty(client.Location))
            {
                settings.Set("pingLocation", client.Location);
                settings.Set("pingLocationTime", client.LocationUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            settings.SetDouble("left", bounds.Left);
            settings.SetDouble("top", bounds.Top);
            settings.SetDouble("width", bounds.Width);
            settings.SetDouble("height", bounds.Height);
            settings.Set("maximized", WindowState == WindowState.Maximized ? "1" : "0");
            settings.Save();
        }
    }
}
