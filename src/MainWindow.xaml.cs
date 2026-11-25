using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dispatch.Model;
using Dispatch.Steam;

namespace Dispatch
{
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan MessageFor = TimeSpan.FromSeconds(6);

        private readonly ServerBrowser browser = new ServerBrowser();
        private readonly LobbyClient client = new LobbyClient();
        private readonly Settings settings = Settings.Load();
        private readonly DispatcherTimer debounce;
        private readonly DispatcherTimer messageTimer;
        private readonly Storyboard progress;

        private SortKey sortKey = SortKey.Nearest;
        private bool sortDescending;
        private CancellationTokenSource lookup;
        private int pendingRequests;
        private int doneRequests;
        private string lastError;
        private string lastCopied;
        private string regionChipKeys = "";
        private bool quiet;
        private DateTime launchConfirmUntil;

        public MainWindow()
        {
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

            var slide = new DoubleAnimation(-160, 1600, new Duration(TimeSpan.FromSeconds(1.2))) { RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(slide, ProgressBar);
            Storyboard.SetTargetProperty(slide, new PropertyPath(Canvas.LeftProperty));
            progress = new Storyboard();
            progress.Children.Add(slide);

            RestoreSettings();
            BuildChips();
            UpdateHeaders();
            UpdateFilterSummary();

            Loaded += (s, e) => _ = UpdateAsync(false);
            Activated += (s, e) => CheckClipboard();
            Closing += (s, e) => SaveSettings();
            StateChanged += (s, e) => OnWindowState();
            PreviewKeyDown += OnWindowKey;
        }

        // ---------- data ----------

        private async Task UpdateAsync(bool force)
        {
            if (force)
            {
                browser.Invalidate();
            }

            var missing = browser.Missing(DateTime.UtcNow);
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
                await client.RunAsync(missing, (index, servers) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cts.IsCancellationRequested || index < 0 || index >= missing.Count)
                    {
                        return;
                    }

                    browser.Store(missing[index], servers, DateTime.UtcNow);
                    doneRequests++;
                    Render();
                })), cts.Token);
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
                Render();
            }
        }

        private void Render()
        {
            var selected = (List.SelectedItem as Server)?.Owner;
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
            else if (lookup != null && !lookup.IsCancellationRequested && doneRequests < pendingRequests)
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
            Status.Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush");
            if (lastError != null)
            {
                Status.Text = lastError;
                Status.Foreground = (System.Windows.Media.Brush)FindResource("RedHotBrush");
                return;
            }

            var busy = lookup != null && !lookup.IsCancellationRequested && doneRequests < pendingRequests && ProgressTrack.IsVisible;
            if (busy)
            {
                Status.Text = "Asking Steam…  " + doneRequests + " of " + pendingRequests + " searches";
                return;
            }

            var pool = browser.Pool().Count;
            var updated = browser.UpdatedUtc();
            Status.Text = pool + " servers from Steam" + (updated.HasValue ? "  ·  updated " + updated.Value.ToLocalTime().ToString("HH:mm:ss") : "");
        }

        private void Say(string text, bool problem = false)
        {
            Status.Text = text;
            Status.Foreground = (System.Windows.Media.Brush)FindResource(problem ? "RedHotBrush" : "TextBrush");
            messageTimer.Stop();
            messageTimer.Start();
        }

        // ---------- filters ----------

        private void BuildChips()
        {
            foreach (var mode in Catalog.Modes)
            {
                ModeChips.Children.Add(MakeChip(mode, mode, browser.Filters.Modes, mode == Catalog.Training
                    ? "Private shooting ranges. Hidden unless selected."
                    : null));
            }

            foreach (var map in Catalog.Maps.Concat(browser.Filters.Maps.Where(m => !Catalog.Maps.Contains(m))))
            {
                MapChips.Children.Add(MakeChip(Catalog.MapName(map), map, browser.Filters.Maps, null));
            }

            BotChips.Children.Add(MakeChip("On", "true", browser.Filters.Bots, "Servers that fill empty slots with bots"));
            BotChips.Children.Add(MakeChip("Off", "false", browser.Filters.Bots, "Servers without bots"));
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
            if (set == browser.Filters.Regions)
            {
                Render();
            }
            else
            {
                Render();
                debounce.Stop();
                debounce.Start();
            }
        }

        private void UpdateRegionChips()
        {
            var counts = browser.Regions();
            var codes = counts.Select(p => p.Key).ToList();
            codes.AddRange(browser.Filters.Regions.Where(r => !codes.Contains(r, StringComparer.OrdinalIgnoreCase)));
            var key = string.Join(",", codes) + "|" + string.Join(",", counts.Select(p => p.Value));
            if (key == regionChipKeys)
            {
                return;
            }

            regionChipKeys = key;
            quiet = true;
            RegionChips.Children.Clear();
            foreach (var code in codes)
            {
                var count = counts.FirstOrDefault(p => string.Equals(p.Key, code, StringComparison.OrdinalIgnoreCase)).Value;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = Catalog.RegionName(code) });
                content.Children.Add(new TextBlock { Text = count.ToString(), Margin = new Thickness(7, 0, 0, 0), Opacity = 0.6 });
                RegionChips.Children.Add(MakeChip(content, code, browser.Filters.Regions, "Relay region " + code.ToUpperInvariant()));
            }

            if (codes.Count == 0)
            {
                RegionChips.Children.Add(new TextBlock
                {
                    Text = "Regions appear once servers load.",
                    Foreground = (System.Windows.Media.Brush)FindResource("TextFaintBrush"),
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
                parts.Add(string.Join(", ", f.Regions.Select(Catalog.RegionName).OrderBy(r => r)));
            }

            if (f.OneSidedBots != null)
            {
                parts.Add(f.OneSidedBots == "true" ? "Bots on" : "Bots off");
            }

            FilterSummary.Text = FilterPanel.Visibility == Visibility.Visible ? "" : parts.Count == 0 ? "All servers" : string.Join("  ·  ", parts);
            var active = f.ActiveCount + (f.IsHostSearch ? 1 : 0);
            ResetButton.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnToggleFilters(object sender, RoutedEventArgs e) => SetFiltersOpen(FilterPanel.Visibility != Visibility.Visible);

        private void SetFiltersOpen(bool open)
        {
            FilterPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            FilterChevron.Text = open ? "" : "";
            UpdateFilterSummary();
        }

        private void OnReset(object sender, RoutedEventArgs e) => ResetFilters();

        private void ResetFilters()
        {
            quiet = true;
            foreach (var panel in new[] { ModeChips, MapChips, RegionChips, BotChips })
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
                var text = SearchBox.Text.Trim();
                if (text.Length == 0)
                {
                    return;
                }

                browser.Filters.ExactHost = text;
                UpdateFilterSummary();
                _ = UpdateAsync(true);
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

        private void OnClearSearch(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        private void OnSearchHost(object sender, RoutedEventArgs e)
        {
            if (List.SelectedItem is Server s)
            {
                SearchBox.Text = s.Name;
                browser.Filters.ExactHost = s.Name;
                UpdateFilterSummary();
                _ = UpdateAsync(true);
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
                browser.Filters.ExactHost = browser.Filters.Text.Trim();
                UpdateFilterSummary();
                _ = UpdateAsync(true);
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
            else if (!sortDescending)
            {
                sortDescending = true;
            }
            else
            {
                sortKey = SortKey.Nearest;
                sortDescending = false;
            }

            UpdateHeaders();
            Render();
        }

        private void UpdateHeaders()
        {
            HeadMode.Content = HeaderText(SortKey.Mode, "GAME MODE");
            HeadMap.Content = HeaderText(SortKey.Map, "MAP");
            HeadRegion.Content = HeaderText(SortKey.Region, "REGION");
            HeadBots.Content = HeaderText(SortKey.Bots, "BOT");
            HeadSlots.Content = HeaderText(SortKey.Slots, "SLOTS");
            HeadName.ToolTip = sortKey == SortKey.Nearest ? "Sorted nearest first, as Steam returns them" : null;
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
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem)
            {
                _ = JoinAsync();
            }
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

            if (!ConfirmLaunch())
            {
                return;
            }

            var link = await FreshLinkAsync(s);
            if (link != null && Open(link))
            {
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

            if (Enum.TryParse(settings.Get("sort"), out SortKey key))
            {
                sortKey = key;
            }

            sortDescending = settings.Get("descending") == "1";
            SetFiltersOpen(settings.Get("filters", "1") == "1");

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
            settings.Set("sort", sortKey.ToString());
            settings.Set("descending", sortDescending ? "1" : "0");
            settings.Set("filters", FilterPanel.Visibility == Visibility.Visible ? "1" : "0");
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
