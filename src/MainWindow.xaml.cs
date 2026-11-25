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
        private static readonly string[] FilterKinds = { "mode", "map", "bots", "region", "ping" };

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

        /// <param name="demo">Shows invented servers and leaves Steam and saved settings alone (for screenshots).</param>
        public MainWindow(bool demo = false)
        {
            this.demo = demo;
            settings = demo ? new Settings() : Settings.Load();
            InitializeComponent();

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
            };

            // Every minute: auto-refresh while focused (if on), otherwise a quiet check on followed hosts.
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
                clock.Start();
                watchTimer.Start();
                _ = UpdateAsync(false);
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
                    cts.Token);
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

                Render();
            }
        }

        private bool Busy => lookup != null && !lookup.IsCancellationRequested && ProgressTrack.Visibility == Visibility.Visible;

        private void Render()
        {
            var selected = (List.SelectedItem as Server)?.Owner;
            Annotate();
            var visible = browser.Visible(sortKey, sortDescending);
            List.ItemsSource = visible;
            if (selected != null)
            {
                List.SelectedItem = visible.FirstOrDefault(s => s.Owner == selected);
            }

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
            Status.Text = pool + " servers from Steam" + (updated.HasValue ? "  ·  updated " + Ago(DateTime.UtcNow - updated.Value) : "");
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

        private void UpdateFilterSummary()
        {
            var f = browser.Filters;
            var parts = new List<string>();
            if (f.Modes.Count > 0)
            {
                parts.Add(string.Join(", ", Catalog.Modes.Where(f.Modes.Contains)));
            }

            if (f.Maps.Count > 0)
            {
                parts.Add(string.Join(", ", f.Maps.Select(Catalog.MapName).OrderBy(m => m)));
            }

            if (f.Regions.Count > 0)
            {
                parts.Add(string.Join(", ", f.Regions.Select(Catalog.RegionName).Distinct().OrderBy(r => r)));
            }

            if (f.MaxPing > 0)
            {
                parts.Add("Ping ≤ " + f.MaxPing + " ms");
            }

            if (f.OneSidedBots != null)
            {
                parts.Add(f.OneSidedBots == "true" ? "Bots on" : "Bots off");
            }

            FilterSummary.Text = FilterPanel.Visibility == Visibility.Visible ? "" : parts.Count == 0 ? "All servers" : string.Join("  ·  ", parts);
            var active = f.ActiveCount + (f.IsHostSearch ? 1 : 0);
            ResetButton.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueSave();
        }

        private void OnToggleFilters(object sender, RoutedEventArgs e) => SetFiltersOpen(FilterPanel.Visibility != Visibility.Visible);

        private void SetFiltersOpen(bool open)
        {
            FilterPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            FilterChevron.Text = open ? "" : "";
            FilterToggle.ToolTip = open ? "Hide filters" : "Show filters";
            UpdateFilterSummary();
        }

        private void OnReset(object sender, RoutedEventArgs e) => ResetFilters();

        private void ResetFilters()
        {
            quiet = true;
            foreach (var panel in new[] { ModeChips, MapChips, RegionChips, PingChips, BotChips })
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
            HeadMode.Content = HeaderText(SortKey.Mode, "GAME MODE");
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
            var friends = client.Friends;
            var all = browser.All();
            foreach (var s in all)
            {
                s.IsFriend = friends.TryGetValue(s.Owner, out var friendName);
                s.FriendName = friendName ?? "";
                s.IsFollowed = follows.Contains(s.Owner);
                var seen = browser.FirstSeenUtc(s.Id);
                s.IsNew = baselineUtc.HasValue && seen > baselineUtc.Value && now - seen < NewFor;
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
            System.Media.SystemSounds.Exclamation.Play();
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

            if (autoRefresh && IsActive)
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
                ? "Auto-refresh on: every minute while Dispatch is in focus. New servers get a red mark."
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
            if (e.Key == Key.F5)
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
            recent.Load(settings.GetRecords("recent"));
            autoRefresh = settings.Get("autoRefresh") == "1";
            quiet = true;
            AutoToggle.IsChecked = autoRefresh;
            quiet = false;

            // The drawer always starts closed; the bar above the list summarises the active filters.
            SetFiltersOpen(false);

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
            settings.Set("sort", sortKey.ToString());
            settings.Set("descending", sortDescending ? "1" : "0");
            settings.SetRecords("followed", follows.ToRecords());
            settings.SetRecords("recent", recent.ToRecords());
            settings.Set("autoRefresh", autoRefresh ? "1" : "0");
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
