using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KaruCloud
{
    /// <summary>SoundCloud-style waveform: bars above the line, a faded mirror below, orange up to the playhead. Click to seek.</summary>
    public sealed class WaveformView : FrameworkElement
    {
        static SolidColorBrush Frozen(string hex) { var b = Ui.B(hex); b.Freeze(); return b; }
        static readonly SolidColorBrush Played = Frozen("#FFFF5500"), PlayedDim = Frozen("#80FF5500"), Rest = Frozen("#E6FFFFFF"), RestDim = Frozen("#4DFFFFFF");

        float[]? samples;
        float max = 1;
        double progress;
        public event Action<double>? Seeked;

        public WaveformView()
        {
            Height = 96;
            Cursor = Cursors.Hand;
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            MouseLeftButtonUp += (_, e) =>
            {
                if (ActualWidth <= 0) return;
                Seeked?.Invoke(Math.Max(0, Math.Min(1, e.GetPosition(this).X / ActualWidth)));
            };
        }

        public void SetSamples(float[] s)
        {
            samples = s;
            max = s.Length > 0 ? s.Max() : 1;
            if (max <= 0) max = 1;
            InvalidateVisual();
        }

        public void SetProgress(double p)
        {
            p = Math.Max(0, Math.Min(1, p));
            if (Math.Abs(p - progress) < 0.0004) return;
            progress = p;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // so empty gaps are clickable too
            const double barW = 2, gap = 1;
            int n = (int)(w / (barW + gap));
            if (n < 1) return;
            double baseY = h * 0.68;
            for (int i = 0; i < n; i++)
            {
                double v = 0.07; // no waveform data: a thin line, still shows progress
                if (samples != null && samples.Length > 0)
                {
                    int a = (int)((long)i * samples.Length / n);
                    int b = Math.Max(a + 1, (int)((long)(i + 1) * samples.Length / n));
                    float sum = 0; int c = 0;
                    for (int k = a; k < b && k < samples.Length; k++) { sum += samples[k]; c++; }
                    v = c > 0 ? (sum / c) / max : 0;
                }
                bool done = (i + 0.5) / n <= progress;
                double up = Math.Max(1.5, v * (baseY - 2));
                double down = Math.Max(1, v * (h - baseY - 2) * 0.8);
                double x = i * (barW + gap);
                dc.DrawRectangle(done ? Played : Rest, null, new Rect(x, baseY - up, barW, up));
                dc.DrawRectangle(done ? PlayedDim : RestDim, null, new Rect(x, baseY + 1, barW, down));
            }
        }
    }

    public sealed partial class MainWindow
    {
        // ================= state of the song / profile pages =================
        sealed class SubState
        {
            public List<ItemVM> Rows = new List<ItemVM>();
            public string? Next;
            public bool Loaded, Loading;
            public string Msg = "";
        }

        readonly ScrollViewer detailScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };
        PaneTab? detailTab;
        WaveformView? detailWave;
        ScTrack? detailTrack;
        TextBlock? detailPlayGlyph, detailPosText;
        Action? detailRefresh;

        // live comments: avatars under the waveform, a bubble that shows each comment while the song passes its time, and the comment list
        readonly Dictionary<long, List<ScComment>> commentCache = new Dictionary<long, List<ScComment>>();
        List<ScComment> liveComments = new List<ScComment>();
        Border? liveBubble, liveAvatar;
        TextBlock? liveName, liveBody;
        long liveShownId;
        Action? commentsReload;
        Func<string, Task<bool>>? commentPoster; // set by the comments section, used by the comment box in the song header
        readonly DispatcherTimer commentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };

        HashSet<long> repostedIds = new HashSet<long>();
        readonly Dictionary<long, int> likeDelta = new Dictionary<long, int>();
        readonly Dictionary<long, int> repostDelta = new Dictionary<long, int>();
        readonly Dictionary<long, bool> followState = new Dictionary<long, bool>();

        /// <summary>Everything in the profile sidebar below the counters. Each part loads on its own, so one refused route never empties the rest.</summary>
        sealed class ProfileSide
        {
            public List<ScWebProfile> Links = new List<ScWebProfile>();
            public List<ScTrack> Likes = new List<ScTrack>();
            public List<ScUser> Fans = new List<ScUser>();
            public List<ScUser> Followers = new List<ScUser>();
            public List<ScUser> Followings = new List<ScUser>();
            public List<ScComment> Comments = new List<ScComment>();
        }

        static readonly (string Key, string Label)[] ProfileTabs =
        {
            ("all", "All"), ("popular", "Popular tracks"), ("tracks", "Tracks"), ("albums", "Albums"), ("playlists", "Playlists"), ("reposts", "Reposts"),
            ("likes", "Likes"), ("followers", "Followers"), ("following", "Following"),
        };

        void ClearDetail()
        {
            detailTab = null; detailWave = null; detailTrack = null;
            detailPlayGlyph = null; detailPosText = null; detailRefresh = null;
            liveBubble = null; liveAvatar = null; liveName = null; liveBody = null; liveComments = new List<ScComment>(); commentsReload = null; commentPoster = null;
            detailScroll.Content = null;
        }

        // ================= clicks in the plain song lists =================
        /// <summary>Title / cover / artist open the song or profile (like soundcloud.com); heart and repost work right in the row.</summary>
        void OnListClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string? tag = null;
                ListBoxItem? item = null;
                for (var d = e.OriginalSource as DependencyObject; d != null; d = ParentOf(d))
                {
                    if (tag == null && d is FrameworkElement fe && fe.Tag is string s && (s == "title" || s == "art" || s == "artist" || s == "like" || s == "repost" || s == "play")) tag = s;
                    if (d is ListBoxItem lbi) { item = lbi; break; }
                    if (ReferenceEquals(d, list)) break;
                }
                if (tag == null || item == null || item.DataContext is not ItemVM it) return;
                e.Handled = true;
                RowAction(it, tag);
            }
            catch (Exception ex) { Store.Log("list click", ex); }
        }

        static DependencyObject? ParentOf(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }

        void RowAction(ItemVM it, string tag)
        {
            switch (tag)
            {
                case "like": ToggleLikeOf(it.Track); break;
                case "repost": ToggleRepostOf(it.Track); break;
                case "play": PlayItem(it); break;
                case "artist":
                    OpenArtist(it.Track?.User ?? it.User ?? it.Playlist?.User);
                    break;
                default: // title, art
                    if (it.Kind == "header") return;
                    if (it.Kind == "more") { ActivateItem(it); return; }
                    OpenCard(it);
                    break;
            }
        }

        // ================= like / repost / follow state =================
        /// <summary>Repaint every heart / repost icon (list rows, song page, player bar) for this track.</summary>
        void FlagsChanged(long trackId)
        {
            foreach (var t in tabs)
                foreach (var r in t.Rows)
                    if (r.Track != null && r.Track.Id == trackId) r.RefreshFlags();
            detailRefresh?.Invoke();
            UpdateTransport();
        }

        void RefreshAllFlags()
        {
            foreach (var t in tabs)
                foreach (var r in t.Rows)
                    if (r.Track != null) r.RefreshFlags();
            detailRefresh?.Invoke();
            UpdateTransport();
        }

        async Task RefreshRepostedIdsAsync(string forAccount)
        {
            try
            {
                var (set, complete) = await api.GetRepostedIdsAsync();
                if (cfg.Current != forAccount || set == null) return;
                if (complete) repostedIds.Clear();
                foreach (var x in set) repostedIds.Add(x);
                RefreshAllFlags();
            }
            catch { }
        }

        // ---- who you follow ----
        // One sync reads your whole Following list from your account (like liked / reposted tracks), so every button, pill and
        // the player-bar icon agree with soundcloud.com. A failed read is never taken as "not following", and a follow / unfollow
        // you just did stays in place even if the list SoundCloud returns a moment later is still the old one.
        bool followSynced;
        Task? followSync;
        readonly Dictionary<long, (bool On, DateTime At)> followTouched = new Dictionary<long, (bool, DateTime)>();

        bool IsFollowed(long id) => followState.TryGetValue(id, out var v) && v;

        /// <summary>Records a follow / unfollow made here (or known for sure from a list) so it survives a later sync.</summary>
        void SetFollowLocal(long id, bool on, bool byUser = true)
        {
            followState[id] = on;
            if (byUser) followTouched[id] = (on, DateTime.UtcNow);
        }

        Task EnsureFollowSyncAsync(bool force = false)
        {
            var a = api.Active;
            if (a == null) return Task.CompletedTask;
            if (!force && followSynced) return Task.CompletedTask;
            if (followSync != null && !followSync.IsCompleted) return followSync;
            return followSync = SyncFollowingAsync(a.Id);
        }

        async Task SyncFollowingAsync(string forAccount)
        {
            try
            {
                var set = await api.GetFollowingIdsAsync();
                if (set == null || cfg.Current != forAccount) return;
                bool hadState = followState.Count > 0;
                followState.Clear();
                foreach (var id in set) followState[id] = true;
                // changes made in the last minute win over the list (SoundCloud's list can lag a few seconds behind)
                var cutoff = DateTime.UtcNow.AddSeconds(-60);
                foreach (var kv in followTouched.ToList())
                {
                    if (kv.Value.At < cutoff) { followTouched.Remove(kv.Key); continue; }
                    followState[kv.Key] = kv.Value.On;
                }
                followSynced = true;
                if (hadState) RepaintFollow();
                else UpdateTransport();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Store.Log("following sync", ex); }
        }

        /// <summary>Buttons built before the sync finished repaint from the fresh state.</summary>
        void RepaintFollow()
        {
            UpdateTransport();
            var t = active;
            if (t == null) return;
            if (ReferenceEquals(cardTab, t)) RebuildCardPage(t);
            else if (ReferenceEquals(detailTab, t) && detailScroll.Visibility == Visibility.Visible) RenderBody(t);
        }

        /// <summary>The Following list changed: reload it next time it is opened (and now, if it is open).</summary>
        void FollowListChanged()
        {
            foreach (var t in tabs.Where(x => x.Kind == "following"))
            {
                t.Loaded = false;
                if (t == active) _ = LoadAsync(t, false);
            }
        }

        async Task LoadFollowStateAsync(long id, Action paint)
        {
            try
            {
                await EnsureFollowSyncAsync();
                if (followSynced)
                {
                    if (!followState.ContainsKey(id)) followState[id] = false;
                    paint();
                    return;
                }
                // the full list couldn't be read: ask about this one person (an unclear answer changes nothing)
                var one = await api.IsFollowingAsync(id);
                if (one != null) { followState[id] = one.Value; paint(); }
            }
            catch { }
        }

        Border MakeFollowBtn(ScUser u)
        {
            var label = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border
            {
                Height = 32, MinWidth = 96, Padding = new Thickness(16, 0, 16, 0), CornerRadius = new CornerRadius(16), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1), Child = label, Margin = new Thickness(0, 0, 8, 8),
            };
            void Paint()
            {
                bool f = followState.TryGetValue(u.Id, out var v) && v;
                label.Text = f ? "Following" : "Follow";
                label.Foreground = f ? Brushes.White : Brushes.Black;
                b.Background = f ? Brushes.Transparent : Brushes.White;
                b.BorderBrush = f ? Ui.B("#66FFFFFF") : Brushes.White;
            }
            Paint();
            if (!followState.ContainsKey(u.Id)) _ = LoadFollowStateAsync(u.Id, Paint);
            b.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                await SetFollowAsync(u, !IsFollowed(u.Id));
                Paint();
            };
            return b;
        }

        // ================= playback hooks for the song page =================
        void PlayDetailTrack(PaneTab t, ScTrack tr)
        {
            if (current != null && current.Id == tr.Id && (player.HasTrack || player.IsLoading)) { TogglePlay(); return; }
            var row = t.Rows.FirstOrDefault(r => r.Track != null && r.Track.Id == tr.Id);
            if (row != null) PlayFromList(t.Rows, row);
            else { queue = new List<ScTrack> { tr }; qi = 0; _ = PlayCurrentAsync(); }
        }

        void SeekDetail(PaneTab t, ScTrack tr, double frac)
        {
            if (current != null && current.Id == tr.Id && player.HasTrack)
            {
                var d = player.Duration;
                if (d.TotalSeconds <= 0) return;
                var pos = TimeSpan.FromSeconds(d.TotalSeconds * Math.Max(0, Math.Min(1, frac)));
                player.Seek(pos);
                UpdateDetailProgress(pos, d);
                UpdatePresence();
            }
            else PlayDetailTrack(t, tr);
        }

        void UpdateDetailProgress(TimeSpan p, TimeSpan d)
        {
            if (detailWave == null || detailTrack == null || current == null || current.Id != detailTrack.Id || d.TotalSeconds <= 0) return;
            detailWave.SetProgress(p.TotalSeconds / d.TotalSeconds);
            if (detailPosText != null) detailPosText.Text = Ui.Fmt(p);
            UpdateLiveComment(p);
        }

        /// <summary>Shows the comment whose time the song just passed (for a few seconds), like the website's waveform.</summary>
        void UpdateLiveComment(TimeSpan p)
        {
            if (liveBubble == null || liveName == null || liveBody == null || liveAvatar == null) return;
            double ms = p.TotalMilliseconds;
            ScComment? hit = null;
            foreach (var c in liveComments)
            {
                double ts = c.Timestamp ?? 0;
                if (ts > ms) break;
                if (ms - ts < 6000) hit = c;
            }
            if (hit == null) { liveBubble.Opacity = 0; return; }
            if (liveShownId != hit.Id)
            {
                liveShownId = hit.Id;
                liveName.Text = hit.User?.Username ?? "";
                liveBody.Text = (hit.Body ?? "").Replace("\r", " ").Replace("\n", " ");
                liveAvatar.Background = Ui.B("#333333");
                string url = hit.User?.AvatarUrl ?? "";
                if (url.Length > 0) _ = LoadArtIntoAsync(url, liveAvatar, "t200x200", 52);
            }
            liveBubble.Opacity = 1;
        }

        void ResetDetailProgress()
        {
            detailWave?.SetProgress(0);
            if (liveBubble != null) liveBubble.Opacity = 0;
            if (detailPosText != null) detailPosText.Text = "0:00";
            UpdateDetailPlayGlyph();
        }

        void UpdateDetailPlayGlyph()
        {
            detailPlaylistSync?.Invoke();
            if (detailPlayGlyph == null || detailTrack == null) return;
            bool me = player.IsPlaying && current != null && current.Id == detailTrack.Id;
            detailPlayGlyph.Text = me ? "\uE769" : "\uE768";
        }

        async Task LoadWaveAsync(WaveformView wave, ScTrack tr)
        {
            try { var s = await api.GetWaveformAsync(tr.WaveformUrl); if (s != null) wave.SetSamples(s); }
            catch { }
        }

        // ================= small building blocks =================
        static TextBlock Txt(string text, double size, string hex, bool bold = false) => new TextBlock
        {
            Text = text, FontSize = size, Foreground = Ui.B(hex),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        static TextBlock Link(string text, double size, string hex, Action click, bool bold = false)
        {
            var tb = Txt(text, size, hex, bold);
            tb.Cursor = Cursors.Hand; tb.HorizontalAlignment = HorizontalAlignment.Left;
            tb.Tag = click; // a middle click runs the same action, but in a background tab
            tb.MouseEnter += (_, _) => tb.TextDecorations = TextDecorations.Underline;
            tb.MouseLeave += (_, _) => tb.TextDecorations = null;
            tb.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return tb;
        }

        /// <summary>Text you can select with the mouse and copy (Ctrl+C or right-click -> Copy): a read-only TextBox dressed as a TextBlock.</summary>
        static TextBox SelText(string text, double size, string hex, bool bold = false)
        {
            var tb = new TextBox
            {
                Text = text, FontSize = size, Foreground = Ui.B(hex), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                IsReadOnly = true, IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Cursor = Cursors.IBeam, FocusVisualStyle = null,
                SelectionBrush = Ui.B("#FF5500"), SelectionOpacity = 0.55, AcceptsReturn = true, IsTabStop = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            // a TextBox swallows the mouse wheel; hand it on so the page still scrolls over selectable text
            tb.PreviewMouseWheel += (_, e) =>
            {
                if (tb.Parent is UIElement up)
                {
                    e.Handled = true;
                    up.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = tb });
                }
            };
            return tb;
        }

        Border ArtBox(string url, double size, bool circle, string token, int decode)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(circle ? size / 2 : 8), Background = Ui.B("#262626") };
            if (url.Length > 0) _ = LoadArtIntoAsync(url, b, token, decode);
            return b;
        }

        async Task LoadArtIntoAsync(string url, Border target, string token, int decode)
        {
            await artGate.WaitAsync();
            try
            {
                var img = await ArtCache.GetAsync(ArtCache.Size(url, token), decode);
                if (img != null) target.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            catch { }
            finally { artGate.Release(); }
        }

        async Task LoadBannerAsync(string url, Border target)
        {
            await artGate.WaitAsync();
            try
            {
                var img = await ArtCache.GetAsync(url, 1200);
                if (img != null) target.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            catch { }
            finally { artGate.Release(); }
        }

        Border IconBtn(string glyph, string tip, Action click, double size = 30)
        {
            var b = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip,
                Child = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 14, Foreground = Ui.B("#cccccc"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(b, "#22FFFFFF");
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return b;
        }

        /// <summary>Rounded outline button with an optional icon and text (like / repost / share ... on the song page).</summary>
        sealed class Pill
        {
            public readonly Border Root;
            readonly TextBlock glyph = new TextBlock(), label = new TextBlock();

            public Pill(string g, string text, Action click)
            {
                glyph.FontFamily = Ui.Icons; glyph.FontSize = 13; glyph.VerticalAlignment = VerticalAlignment.Center;
                label.FontSize = 12.5; label.FontWeight = FontWeights.SemiBold; label.VerticalAlignment = VerticalAlignment.Center;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(glyph); sp.Children.Add(label);
                Root = new Border
                {
                    Height = 32, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(16),
                    BorderThickness = new Thickness(1), BorderBrush = Ui.B("#44FFFFFF"), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = sp,
                };
                Ui.Hover(Root, "#22FFFFFF");
                Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
                Set(g, text, "#e6e6e6");
            }

            public void Set(string g, string text, string hex)
            {
                var fg = Ui.B(hex);
                glyph.Text = g; glyph.Foreground = fg;
                glyph.Visibility = g.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                label.Text = text; label.Foreground = fg;
                label.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                label.Margin = new Thickness(g.Length == 0 || text.Length == 0 ? 0 : 7, 0, 0, 0);
            }
        }

        static int Delta(Dictionary<long, int> d, long id) => d.TryGetValue(id, out var v) ? v : 0;

        static void CopyLink(string? url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Clipboard.SetText(url); } catch { }
        }

        /// <summary>Main column + side column; the side column drops below on a narrow window.</summary>
        void TwoCol(StackPanel page, FrameworkElement main, FrameworkElement side)
        {
            var g = new Grid();
            g.Children.Add(main); g.Children.Add(side);
            bool? last = null;
            void Lay()
            {
                double w = detailScroll.ViewportWidth > 0 ? detailScroll.ViewportWidth : (detailScroll.ActualWidth > 0 ? detailScroll.ActualWidth : 900);
                bool wide = w >= 780;
                if (last == wide) return;
                last = wide;
                g.ColumnDefinitions.Clear(); g.RowDefinitions.Clear();
                if (wide)
                {
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
                    Grid.SetRow(main, 0); Grid.SetColumn(main, 0);
                    Grid.SetRow(side, 0); Grid.SetColumn(side, 1);
                    main.Margin = new Thickness(0, 0, 22, 0); side.Margin = new Thickness(0);
                }
                else
                {
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(side, 0); Grid.SetColumn(side, 0);
                    Grid.SetRow(main, 1); Grid.SetColumn(main, 0);
                    side.Margin = new Thickness(0, 0, 0, 18); main.Margin = new Thickness(0);
                }
            }
            Lay();
            page.SizeChanged += (_, _) => Lay();
            page.Children.Add(g);
        }

        UIElement BuildDetail(PaneTab t)
        {
            detailWave = null; detailTrack = null; detailPlayGlyph = null; detailPosText = null; detailRefresh = null; detailPlaylistSync = null;
            liveBubble = null; liveAvatar = null; liveName = null; liveBody = null; liveComments = new List<ScComment>(); commentsReload = null;
            return t.Kind == "track" ? BuildTrackDetail(t) : t.Kind == "playlist" ? BuildPlaylistDetail(t) : BuildUserDetail(t);
        }

        // ================= rows (songs) and cards (playlists / people) =================
        FrameworkElement BuildTrackRow(ItemVM it, Action<ItemVM> play, List<Action> refreshers)
        {
            var tr = it.Track!;
            var row = new Border { Height = 58, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(6, 0, 6, 0) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var art = ArtBox(it.ArtUrl, 44, false, "t200x200", 96);
            art.HorizontalAlignment = HorizontalAlignment.Left; art.VerticalAlignment = VerticalAlignment.Center; art.Cursor = Cursors.Hand;
            art.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenTrack(tr); };
            art.Tag = (Action)(() => OpenTrack(tr));
            var playOv = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Ui.B("#FFFF5500"), Cursor = Cursors.Hand, ToolTip = "Play",
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed,
                Child = new TextBlock { Text = "\uE768", FontFamily = Ui.Icons, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 0, 0) },
            };
            playOv.MouseLeftButtonUp += (_, e) => { e.Handled = true; play(it); };

            var title = Link(it.Title, 13.5, "#F2F2F2", () => OpenTrack(tr), true);
            title.Foreground = it.TitleBrush;
            cardTitles.Add((it, title));
            var artist = Link(it.Artist, 12, "#8A8A8A", () => OpenArtist(tr.User));
            artist.Margin = new Thickness(0, 2, 0, 0);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(title); texts.Children.Add(artist);
            Grid.SetColumn(texts, 1);

            var plays = Txt((tr.PlaybackCount ?? 0) > 0 ? "▶ " + Ui.Count(tr.PlaybackCount ?? 0) : "", 11.5, "#777777");
            plays.VerticalAlignment = VerticalAlignment.Center; plays.Margin = new Thickness(8, 0, 6, 0);
            Grid.SetColumn(plays, 2);
            var like = IconBtn("\uEB51", "Like", () => ToggleLikeOf(tr));
            var repost = IconBtn("\uE8EE", "Repost", () => ToggleRepostOf(tr));
            Grid.SetColumn(like, 3); Grid.SetColumn(repost, 4);
            var dur = Txt(it.DurationText, 12, "#777777");
            dur.VerticalAlignment = VerticalAlignment.Center; dur.Margin = new Thickness(6, 0, 4, 0);
            Grid.SetColumn(dur, 5);

            void Sync()
            {
                bool l = likedIds.Contains(tr.Id), r = repostedIds.Contains(tr.Id);
                var lt = (TextBlock)like.Child; lt.Text = l ? "\uEB52" : "\uEB51"; lt.Foreground = Ui.B(l ? "#FF7A3D" : "#cccccc");
                var rt = (TextBlock)repost.Child; rt.Foreground = Ui.B(r ? "#FF7A3D" : "#cccccc");
            }
            Sync();
            refreshers.Add(Sync);

            g.Children.Add(art); g.Children.Add(playOv); g.Children.Add(texts); g.Children.Add(plays);
            g.Children.Add(like); g.Children.Add(repost); g.Children.Add(dur);
            row.Child = g;
            row.MouseEnter += (_, _) => { row.Background = Ui.B("#1AFFFFFF"); playOv.Visibility = Visibility.Visible; };
            row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; playOv.Visibility = Visibility.Collapsed; };
            row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; play(it); } };
            row.MouseRightButtonUp += (_, e) => { if (ShowItemMenu(it, row, PlacementMode.MousePoint)) e.Handled = true; };
            return row;
        }

        /// <summary>Headers become section titles, songs become rows, playlists / people become cards.</summary>
        UIElement BuildItems(IList<ItemVM> rows, Action<ItemVM> play, List<Action> refreshers)
        {
            var root = new StackPanel();
            StackPanel? trackBox = null;
            WrapPanel? cardBox = null;
            foreach (var r in rows)
            {
                if (r.Kind == "more") continue;
                if (r.Kind == "header")
                {
                    trackBox = null; cardBox = null;
                    root.Children.Add(new TextBlock
                    {
                        Text = r.Title, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, root.Children.Count == 0 ? 0 : 18, 0, 10),
                    });
                    continue;
                }
                if (r.Kind == "track" && r.Track != null)
                {
                    if (trackBox == null) { trackBox = new StackPanel(); root.Children.Add(trackBox); cardBox = null; }
                    trackBox.Children.Add(BuildTrackRow(r, play, refreshers));
                }
                else
                {
                    if (cardBox == null) { cardBox = new WrapPanel(); root.Children.Add(cardBox); trackBox = null; }
                    cardBox.Children.Add(BuildCard(r));
                }
            }
            return root;
        }

        Border MoreBtn(Action click)
        {
            var b = new Border
            {
                Height = 34, Width = 140, CornerRadius = new CornerRadius(17), Background = Ui.B("#1FFFFFFF"), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock { Text = "Load more", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(b, "#33FFFFFF");
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return b;
        }

        // ================= song page =================
        UIElement BuildTrackDetail(PaneTab t)
        {
            var tr = t.DTrack!;
            detailTrack = tr;
            var refreshers = new List<Action>();
            detailRefresh = () => { foreach (var a in refreshers.ToList()) a(); };
            var page = new StackPanel { Margin = new Thickness(20, 14, 20, 28) };

            // ---- hero like soundcloud.com: the artist's banner (or a gradient) behind play + title, the waveform and one action bar ----
            string bannerUrl = tr.BannerUrl;
            bool hasBanner = bannerUrl.Length > 0;
            var card = new Border
            {
                CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 18), ClipToBounds = true, MinHeight = 330,
                Background = new LinearGradientBrush(Ui.B("#FF3C3C41").Color, Ui.B("#FF1F1F22").Color, 45),
            };
            var heroLayers = new Grid();
            var bg = new Border { CornerRadius = new CornerRadius(14), Background = Brushes.Transparent };
            if (hasBanner) _ = LoadBannerAsync(bannerUrl, bg);
            var shade = new Border { CornerRadius = new CornerRadius(14), IsHitTestVisible = false };
            if (hasBanner)
                shade.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops = { new GradientStop(Color.FromArgb(0xA0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0x30, 0, 0, 0), 0.45), new GradientStop(Color.FromArgb(0xD0, 0, 0, 0), 1) },
                };
            heroLayers.Children.Add(bg); heroLayers.Children.Add(shade);

            var hero = new Grid { Margin = new Thickness(22, 20, 22, 16) };
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 play + title
            hero.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 1 waveform
            hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 comment avatars
            hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 3 times
            hero.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4 action bar

            // play + title + artist
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var playGlyph = new TextBlock { FontFamily = Ui.Icons, FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            detailPlayGlyph = playGlyph;
            var playBtn = new Border
            {
                Width = 58, Height = 58, CornerRadius = new CornerRadius(29), Background = Ui.B("#8C000000"), BorderBrush = Ui.B("#E6FFFFFF"), BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand, Child = playGlyph, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0), ToolTip = "Play / pause",
            };
            Ui.Hover(playBtn, "#CC000000");
            playBtn.MouseLeftButtonUp += (_, _) => PlayDetailTrack(t, tr);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(SelText(tr.Title, 25, "#ffffff", true));
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            meta.Children.Add(Link(tr.ArtistName, 13.5, "#e0e0e0", () => OpenArtist(tr.User)));
            string ago = Ui.Ago(tr.CreatedAt);
            if (ago.Length > 0) meta.Children.Add(Txt("   ·   " + ago, 12.5, "#b4b4b4"));
            info.Children.Add(meta);
            Grid.SetColumn(playBtn, 0); Grid.SetColumn(info, 1);
            top.Children.Add(playBtn); top.Children.Add(info);
            Grid.SetRow(top, 0); hero.Children.Add(top);

            // waveform (with the live comment bubble floating over its top-left corner)
            var wave = new WaveformView { Height = 110, VerticalAlignment = VerticalAlignment.Bottom };
            detailWave = wave;
            wave.Seeked += f => SeekDetail(t, tr, f);
            _ = LoadWaveAsync(wave, tr);
            var bubble = new Border
            {
                Height = 34, CornerRadius = new CornerRadius(17), Background = Ui.B("#99000000"), Padding = new Thickness(5, 0, 14, 0),
                Margin = new Thickness(0, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, MaxWidth = 560, Opacity = 0, IsHitTestVisible = false,
            };
            var bubbleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var bubbleAv = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.B("#333333") };
            var bubbleName = Txt("", 12, "#ffffff", true); bubbleName.Margin = new Thickness(8, 0, 6, 0); bubbleName.VerticalAlignment = VerticalAlignment.Center;
            var bubbleText = Txt("", 12.5, "#e0e0e0"); bubbleText.VerticalAlignment = VerticalAlignment.Center; bubbleText.MaxWidth = 380;
            bubbleRow.Children.Add(bubbleAv); bubbleRow.Children.Add(bubbleName); bubbleRow.Children.Add(bubbleText);
            bubble.Child = bubbleRow;
            liveBubble = bubble; liveAvatar = bubbleAv; liveName = bubbleName; liveBody = bubbleText; liveShownId = 0;
            var waveHost = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            waveHost.Children.Add(wave); waveHost.Children.Add(bubble);
            Grid.SetRow(waveHost, 1); hero.Children.Add(waveHost);

            var strip = new Canvas { Height = 22, Margin = new Thickness(0, 4, 0, 0) };
            strip.SizeChanged += (_, _) => PositionCommentStrip(strip);
            Grid.SetRow(strip, 2); hero.Children.Add(strip);
            var times = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            var pos = Txt("0:00", 11, "#c8c8c8");
            detailPosText = pos;
            var dur = Txt(Ui.Fmt(TimeSpan.FromMilliseconds(tr.Duration)), 11, "#c8c8c8");
            dur.HorizontalAlignment = HorizontalAlignment.Right;
            times.Children.Add(pos); times.Children.Add(dur);
            Grid.SetRow(times, 3); hero.Children.Add(times);

            // cover art on the right, only when the song has no banner (a banner already is the picture)
            Border? art = null;
            if (!hasBanner)
            {
                art = ArtBox(ItemVM.BestArt(tr.ArtworkUrl, tr.User?.AvatarUrl), 200, false, "t500x500", 400);
                art.VerticalAlignment = VerticalAlignment.Top; art.Margin = new Thickness(20, 0, 0, 0);
                Grid.SetColumn(art, 1); Grid.SetRow(art, 0); Grid.SetRowSpan(art, 4);
                hero.Children.Add(art);
            }

            // action bar: like (with count) | write a comment | share, link, repost, add to queue, more
            (Border Root, TextBlock Glyph, TextBlock Label) HeroBtn(string glyph, string label, string tip, Action click)
            {
                bool hasLabel = label.Length > 0;
                var gl = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 14, Foreground = Ui.B("#f0f0f0"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                var lb = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("#f0f0f0"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), Visibility = hasLabel ? Visibility.Visible : Visibility.Collapsed };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(gl); sp.Children.Add(lb);
                var b = new Border
                {
                    Height = 36, MinWidth = 36, CornerRadius = new CornerRadius(18), Padding = new Thickness(hasLabel ? 13 : 0, 0, hasLabel ? 15 : 0, 0), Margin = new Thickness(6, 0, 0, 0),
                    BorderThickness = new Thickness(1), BorderBrush = Ui.B("#40FFFFFF"), Background = Ui.B("#B3141414"), Cursor = Cursors.Hand, ToolTip = tip, Child = sp,
                };
                Ui.Hover(b, "#D92A2A2A");
                b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
                return (b, gl, lb);
            }

            var bar = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var likeB = HeroBtn("\uEB51", Ui.Count(tr.LikesCount ?? 0), "Like", () => ToggleLikeOf(tr));
            likeB.Root.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(likeB.Root, 0); bar.Children.Add(likeB.Root);

            var cin = new TextBox
            {
                Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, SelectionBrush = Ui.B("#FF5500"), MaxLength = 300,
            };
            var chint = Txt("Write a comment", 13, "#b8b8b8");
            chint.IsHitTestVisible = false; chint.VerticalAlignment = VerticalAlignment.Center; chint.Margin = new Thickness(2, 0, 0, 0);
            cin.TextChanged += (_, _) => chint.Visibility = cin.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var sendGlyph = new TextBlock { Text = "\uE724", FontFamily = Ui.Icons, FontSize = 14, Foreground = Ui.B("#c8c8c8"), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Post comment", Margin = new Thickness(8, 0, 0, 0) };
            var myAv = ArtBox(api.Active?.AvatarUrl ?? "", 24, true, "t200x200", 48);
            myAv.Margin = new Thickness(0, 0, 10, 0); myAv.VerticalAlignment = VerticalAlignment.Center;
            var cgrid = new Grid();
            cgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(myAv, 0); Grid.SetColumn(sendGlyph, 2);
            var cmid = new Grid();
            cmid.Children.Add(chint); cmid.Children.Add(cin);
            Grid.SetColumn(cmid, 1);
            cgrid.Children.Add(myAv); cgrid.Children.Add(cmid); cgrid.Children.Add(sendGlyph);
            var cwrap = new Border
            {
                Height = 36, CornerRadius = new CornerRadius(18), Background = Ui.B("#B3141414"), BorderBrush = Ui.B("#40FFFFFF"), BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 0, 14, 0), Child = cgrid, Margin = new Thickness(0, 0, 2, 0),
            };
            Grid.SetColumn(cwrap, 1); bar.Children.Add(cwrap);

            async void SendHero()
            {
                string text = cin.Text.Trim();
                if (text.Length == 0 || commentPoster == null) return;
                cin.IsEnabled = false;
                try { if (await commentPoster(text)) cin.Text = ""; }
                finally { cin.IsEnabled = true; cin.Focus(); }
            }
            sendGlyph.MouseLeftButtonUp += (_, e) => { e.Handled = true; SendHero(); };
            cin.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SendHero(); } };

            var icons = new StackPanel { Orientation = Orientation.Horizontal };
            var shareB = HeroBtn("\uE72D", "", "Share (opens the song on SoundCloud)", () => OpenUrl(tr.PermalinkUrl));
            var linkB = HeroBtn("\uE71B", "", "Copy link", () => { CopyLink(tr.PermalinkUrl); ShowToast("Link copied", true); });
            var repB = HeroBtn("\uE8EE", "", "Repost", () => ToggleRepostOf(tr));
            Border queueRoot = null!;
            var queueB = HeroBtn("\uE710", "", "Add to playlist", () => ShowAddToPlaylist(tr, queueRoot));
            queueRoot = queueB.Root;
            Border moreRoot = null!;
            var moreB = HeroBtn("\uE712", "", "More", () => ShowQueueRowMenu(tr, moreRoot));
            moreRoot = moreB.Root;
            foreach (var ib in new[] { shareB.Root, linkB.Root, repB.Root, queueB.Root, moreB.Root }) icons.Children.Add(ib);
            Grid.SetColumn(icons, 2); bar.Children.Add(icons);
            Grid.SetRow(bar, 4); Grid.SetColumnSpan(bar, 2); hero.Children.Add(bar);

            heroLayers.Children.Add(hero);
            card.Child = heroLayers;
            page.Children.Add(card);
            if (art != null) page.SizeChanged += (_, _) => art.Visibility = page.ActualWidth < 700 ? Visibility.Collapsed : Visibility.Visible;

            // ---- side: artist + counters ----
            var side = new StackPanel();
            var artist = t.DUser ?? tr.User;
            if (artist != null) side.Children.Add(BuildArtistCard(artist));
            var stats = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock Stat(int col, string label, long value)
            {
                var num = new TextBlock { Text = Ui.Count(value), Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
                var box = new Border
                {
                    CornerRadius = new CornerRadius(10), Background = Ui.B("#1A1A1A"), Padding = new Thickness(4, 10, 4, 10), Margin = new Thickness(col == 0 ? 0 : 5, 0, col == 2 ? 0 : 5, 0),
                    Child = new StackPanel { Children = { num, new TextBlock { Text = label, Foreground = Ui.B("#808080"), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) } } },
                };
                Grid.SetColumn(box, col); stats.Children.Add(box);
                return num;
            }
            var playsNum = Stat(0, "PLAYS", tr.PlaybackCount ?? 0);
            var likesNum = Stat(1, "LIKES", tr.LikesCount ?? 0);
            var repsNum = Stat(2, "REPOSTS", tr.RepostsCount ?? 0);
            side.Children.Add(stats);

            void Sync()
            {
                bool l = likedIds.Contains(tr.Id), r = repostedIds.Contains(tr.Id);
                long lc = Math.Max(0, (tr.LikesCount ?? 0) + Delta(likeDelta, tr.Id));
                long rc = Math.Max(0, (tr.RepostsCount ?? 0) + Delta(repostDelta, tr.Id));
                likeB.Glyph.Text = l ? "\uEB52" : "\uEB51";
                likeB.Glyph.Foreground = Ui.B(l ? "#FF7A3D" : "#f0f0f0");
                likeB.Label.Text = Ui.Count(lc);
                likeB.Label.Foreground = Ui.B(l ? "#FF7A3D" : "#f0f0f0");
                repB.Glyph.Foreground = Ui.B(r ? "#FF7A3D" : "#f0f0f0");
                repB.Root.ToolTip = (r ? "Reposted" : "Repost") + " (" + Ui.Count(rc) + ")";
                likesNum.Text = Ui.Count(lc); repsNum.Text = Ui.Count(rc);
            }
            Sync();
            refreshers.Add(Sync);

            // ---- side: related tracks, playlists that contain the song ----
            side.Children.Add(BuildSideRelated(t, tr));
            side.Children.Add(BuildInPlaylists(tr));

            // ---- main: details (collapsed, "Show more"), comments ----
            var main = new StackPanel();
            bool hasDesc = !string.IsNullOrWhiteSpace(tr.Description);
            bool hasGenre = !string.IsNullOrWhiteSpace(tr.Genre);
            if (hasDesc || hasGenre)
            {
                main.Children.Add(SectionHead("Details", null, null));
                if (hasGenre)
                    main.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(12), Background = Ui.B("#262626"), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10),
                        Child = Txt("# " + tr.Genre!.Trim(), 12, "#cfcfcf"),
                    });
                if (hasDesc)
                {
                    string d = tr.Description!.Trim();
                    if (d.Length > 4000) d = d.Substring(0, 4000) + "…";
                    var body = SelText(d, 13, "#cfcfcf"); body.MaxHeight = 78;
                    var host = new Border { ClipToBounds = true, Child = body, Margin = new Thickness(0, 6, 0, 0) };
                    bool longText = d.Length > 220 || d.Count(c => c == '\n') >= 3;
                    main.Children.Add(host);
                    if (longText)
                    {
                        var toggle = new TextBlock { Text = "Show more  ⌄", Foreground = Ui.B("#e0e0e0"), FontSize = 12, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                        bool open = false;
                        toggle.MouseLeftButtonUp += (_, e) =>
                        {
                            e.Handled = true; open = !open;
                            body.MaxHeight = open ? double.PositiveInfinity : 78;
                            toggle.Text = open ? "Show less  ⌃" : "Show more  ⌄";
                        };
                        main.Children.Add(toggle);
                    }
                    main.Children.Add(new Border { Height = 18 });
                }
            }
            main.Children.Add(BuildCommentsSection(t, tr, strip));

            TwoCol(page, main, side);
            UpdateDetailPlayGlyph();
            if (player.HasTrack) UpdateDetailProgress(player.Position, player.Duration);
            return page;
        }

        // ================= song page: section headers and side modules =================
        /// <summary>"DETAILS", "RELATED TRACKS"...: small caps title, optional "See all" on the right, a thin rule underneath.</summary>
        UIElement SectionHead(string title, string? rightText, Action? rightClick)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Foreground = Ui.B("#f0f0f0"), FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            if (rightText != null)
            {
                var r = rightClick != null ? Link(rightText, 11, "#9a9a9a", rightClick, true) : Txt(rightText, 11, "#9a9a9a", true);
                r.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(r, 1); g.Children.Add(r);
            }
            return new Border { BorderBrush = Ui.B("#2c2c2c"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Margin = new Thickness(0, 0, 0, 6), Child = g };
        }

        UIElement BuildSideRelated(PaneTab t, ScTrack tr)
        {
            var box = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
            var rows = t.Rows.Where(r => r.Kind == "track" && r.Track != null && r.Track.Id != tr.Id).ToList();
            var list = new StackPanel();
            bool all = false;
            void Fill()
            {
                list.Children.Clear();
                if (rows.Count == 0) { list.Children.Add(Txt("Loading related tracks…", 12, "#707070")); return; }
                foreach (var r in rows.Take(all ? 30 : 4)) list.Children.Add(SideTrackRow(t, r));
            }
            Action? toggle = null;
            var head = new ContentControl();
            void Head()
            {
                head.Content = SectionHead("Related tracks", rows.Count > 4 ? (all ? "Show less" : "See all") : null, toggle);
            }
            toggle = () => { all = !all; Fill(); Head(); };
            Head(); Fill();
            box.Children.Add(head); box.Children.Add(list);
            return box;
        }

        FrameworkElement SideTrackRow(PaneTab t, ItemVM it)
        {
            var tr = it.Track!;
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(0, 4, 4, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var art = ArtBox(it.ArtUrl, 40, false, "t200x200", 80);
            art.CornerRadius = new CornerRadius(4);
            var tx = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var title = Txt(it.Title, 12.5, "#F2F2F2", true);
            title.Foreground = it.TitleBrush; cardTitles.Add((it, title));
            tx.Children.Add(title);
            tx.Children.Add(Txt(it.Artist, 11.5, "#8A8A8A"));
            Grid.SetColumn(tx, 1);
            g.Children.Add(art); g.Children.Add(tx);
            row.Child = g;
            row.MouseEnter += (_, _) => row.Background = Ui.B("#1AFFFFFF");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenTrack(tr); };
            row.Tag = (Action)(() => OpenTrack(tr));
            row.MouseRightButtonUp += (_, e) => { if (ShowItemMenu(it, row, PlacementMode.MousePoint)) e.Handled = true; };
            row.ToolTip = "Open  (right-click for more)";
            return row;
        }

        readonly Dictionary<long, List<ScPlaylist>> trackPlaylistCache = new Dictionary<long, List<ScPlaylist>>();

        /// <summary>"In playlists": up to three covers of playlists that contain this song.</summary>
        UIElement BuildInPlaylists(ScTrack tr)
        {
            var box = new StackPanel { Margin = new Thickness(0, 22, 0, 0), Visibility = Visibility.Collapsed };
            var grid = new UniformGrid { Columns = 3 };
            var headHost = new ContentControl();
            box.Children.Add(headHost); box.Children.Add(grid);
            bool all = false;
            List<ScPlaylist> items = new List<ScPlaylist>();
            Action? toggle = null;
            void Fill()
            {
                grid.Children.Clear();
                headHost.Content = SectionHead("In playlists", items.Count > 3 ? (all ? "Show less" : "See all") : null, toggle);
                var shown = items.Take(all ? 6 : 3).ToList();
                grid.Columns = 3;
                foreach (var pl in shown)
                {
                    var p = pl;
                    var card = new StackPanel { Margin = new Thickness(0, 4, 6, 6), Cursor = Cursors.Hand };
                    var cover = ArtBox(p.ArtworkUrl ?? p.User?.AvatarUrl ?? "", 74, false, "t200x200", 150);
                    cover.CornerRadius = new CornerRadius(6); cover.HorizontalAlignment = HorizontalAlignment.Left;
                    card.Children.Add(cover);
                    var nm = Txt(p.Title, 11.5, "#ffffff", true); nm.Margin = new Thickness(0, 5, 0, 0); nm.MaxWidth = 78;
                    card.Children.Add(nm);
                    var ow = Txt(p.User?.Username ?? "", 10.5, "#8a8a8a"); ow.MaxWidth = 78;
                    card.Children.Add(ow);
                    card.MouseLeftButtonUp += (_, e) => { e.Handled = true; NavigateHere("playlist", Cut(p.Title, 24), p.Key); };
                    card.Tag = (Action)(() => NavigateHere("playlist", Cut(p.Title, 24), p.Key));
                    card.ToolTip = p.Title;
                    grid.Children.Add(card);
                }
            }
            toggle = () => { all = !all; Fill(); };
            async System.Threading.Tasks.Task LoadAsync()
            {
                try
                {
                    if (!trackPlaylistCache.TryGetValue(tr.Id, out var got))
                    {
                        got = await api.GetTrackPlaylistsAsync(tr.Id);
                        trackPlaylistCache[tr.Id] = got;
                    }
                    items = got.Where(x => x.Id != 0 && !string.IsNullOrEmpty(x.Title)).ToList();
                    if (items.Count == 0 || detailTrack == null || detailTrack.Id != tr.Id) return;
                    Fill();
                    box.Visibility = Visibility.Visible;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            _ = LoadAsync();
            return box;
        }

        // ================= comments =================
        void PositionCommentStrip(Canvas strip)
        {
            double w = strip.ActualWidth;
            if (w <= 0) return;
            foreach (var child in strip.Children)
                if (child is Border b && b.Tag is double f) Canvas.SetLeft(b, Math.Max(0, Math.Min(w - b.Width, f * w - b.Width / 2)));
        }

        /// <summary>Puts the timed comments' avatars under the waveform and hands the list to the live bubble.</summary>
        void ApplyLiveComments(PaneTab t, ScTrack tr, Canvas strip, List<ScComment> all)
        {
            liveComments = all.Where(c => c.Timestamp != null && c.Timestamp >= 0).OrderBy(c => c.Timestamp).ToList();
            strip.Children.Clear();
            var shown = liveComments;
            if (shown.Count > 60)
            {
                double step = shown.Count / 60.0;
                shown = Enumerable.Range(0, 60).Select(i => liveComments[(int)(i * step)]).ToList();
            }
            double dur = Math.Max(1, tr.Duration);
            foreach (var c in shown)
            {
                double ts = c.Timestamp ?? 0;
                double frac = Math.Min(1, ts / dur);
                var dot = ArtBox(c.User?.AvatarUrl ?? "", 18, true, "t200x200", 36);
                dot.Cursor = Cursors.Hand;
                dot.Tag = frac;
                dot.ToolTip = (c.User?.Username ?? "") + "  " + Ui.Fmt(TimeSpan.FromMilliseconds(ts)) + "\n" + (c.Body ?? "");
                dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; SeekDetail(t, tr, frac); };
                strip.Children.Add(dot);
            }
            PositionCommentStrip(strip);
            liveShownId = 0;
        }

        UIElement BuildCommentsSection(PaneTab t, ScTrack tr, Canvas strip)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            var head = new TextBlock { Text = "COMMENTS", Foreground = Ui.B("#f0f0f0"), FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            var sortLbl = new TextBlock { Text = "Newest", Foreground = Ui.B("#9a9a9a"), FontSize = 11, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(sortLbl, 1);
            headGrid.Children.Add(head); headGrid.Children.Add(sortLbl);
            box.Children.Add(new Border { BorderBrush = Ui.B("#2c2c2c"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Margin = new Thickness(0, 0, 0, 12), Child = headGrid });
            bool newest = true;
            var lastAll = new List<ScComment>();

            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            box.Children.Add(list);

            void Render(List<ScComment> all)
            {
                lastAll = all;
                head.Text = all.Count == 0 ? "COMMENTS" : (Ui.Count(all.Count) + (all.Count == 1 ? " COMMENT" : " COMMENTS"));
                ApplyLiveComments(t, tr, strip, all);
                list.Children.Clear();
                var ordered = newest ? all.OrderByDescending(x => x.CreatedAt ?? "") : all.OrderBy(x => x.CreatedAt ?? "");
                foreach (var c in ordered.Take(50)) list.Children.Add(CommentRow(t, tr, c));
            }

            async Task LoadAsync()
            {
                try
                {
                    var all = await api.GetCommentsAsync(tr.Id);
                    commentCache[tr.Id] = all;
                    if (detailTrack == null || detailTrack.Id != tr.Id) return;
                    Render(all);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (!commentCache.ContainsKey(tr.Id) && detailTrack != null && detailTrack.Id == tr.Id)
                    {
                        list.Children.Clear();
                        list.Children.Add(Txt("Couldn't load comments right now.", 12, "#808080"));
                    }
                }
            }

            async Task<bool> PostText(string text)
            {
                text = text.Trim();
                if (text.Length == 0) return false;
                string forAccount = cfg.Current;
                string busyKey = forAccount + ":comment:" + tr.Id;
                if (!writeBusy.Add(busyKey)) return false; // the same comment is already being sent
                var me = api.Active; // captured now, so the bubble below credits whoever actually posted it even if the account switches before this finishes
                long ts = -1;
                if (current != null && current.Id == tr.Id && player.HasTrack) ts = (long)player.Position.TotalMilliseconds;
                try
                {
                    await api.PostCommentAsync(tr.Id, text, ts, tr.PermalinkUrl);
                    if (cfg.Current != forAccount) return true; // posted fine, but another account is active now: don't touch this view with it
                    var mine = new ScComment
                    {
                        Id = -DateTime.UtcNow.Ticks, Body = text, Timestamp = ts >= 0 ? ts : (long?)null, CreatedAt = DateTime.UtcNow.ToString("o"),
                        User = new ScUser { Id = me?.UserId ?? 0, Username = me?.Name ?? "You", AvatarUrl = me?.AvatarUrl },
                    };
                    if (!commentCache.TryGetValue(tr.Id, out var all)) { all = new List<ScComment>(); commentCache[tr.Id] = all; }
                    all.Add(mine);
                    if (detailTrack != null && detailTrack.Id == tr.Id) Render(all);
                    ShowToast("Comment posted", true);
                    return true;
                }
                catch (Exception ex)
                {
                    Store.Log("comment " + tr.Id, ex);
                    ShowToast(ex.Message);
                    return false;
                }
                finally { writeBusy.Remove(busyKey); }
            }
            commentPoster = PostText;

            sortLbl.MouseLeftButtonUp += (_, e) => { e.Handled = true; newest = !newest; sortLbl.Text = newest ? "Newest" : "Oldest"; Render(lastAll); };
            commentsReload = () => { _ = LoadAsync(); };
            if (commentCache.TryGetValue(tr.Id, out var cached)) Render(cached);
            _ = LoadAsync();
            return box;
        }

        UIElement CommentRow(PaneTab t, ScTrack tr, ScComment c)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var av = ArtBox(c.User?.AvatarUrl ?? "", 36, true, "t200x200", 72);
            av.VerticalAlignment = VerticalAlignment.Top; av.Cursor = Cursors.Hand;
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(c.User); };
            var tx = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(Link(c.User?.Username ?? "", 12.5, "#ffffff", () => OpenArtist(c.User), true));
            if (c.Timestamp != null)
            {
                long ts = c.Timestamp.Value;
                line.Children.Add(Txt("  at  ", 11.5, "#808080"));
                var chip = new Border { CornerRadius = new CornerRadius(8), Background = Ui.B("#2A3F77"), Padding = new Thickness(6, 1, 6, 1), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = Ui.Fmt(TimeSpan.FromMilliseconds(ts)), Foreground = Ui.B("#A9C0FF"), FontSize = 11, FontWeight = FontWeights.SemiBold } };
                chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; SeekDetail(t, tr, tr.Duration > 0 ? (double)ts / tr.Duration : 0); };
                line.Children.Add(chip);
            }
            string ago = Ui.Ago(c.CreatedAt);
            if (ago.Length > 0) line.Children.Add(Txt("   ·   " + ago, 11.5, "#808080"));
            if (c.User != null && tr.User != null && c.User.Id == tr.User.Id) line.Children.Add(Txt("   ·   artist", 11.5, "#FF7A3D", true));
            tx.Children.Add(line);
            { var cb = SelText(c.Body ?? "", 13, "#d0d0d0"); cb.Margin = new Thickness(0, 3, 0, 0); tx.Children.Add(cb); }
            Grid.SetColumn(av, 0); Grid.SetColumn(tx, 1);
            g.Children.Add(av); g.Children.Add(tx);
            return g;
        }

        UIElement BuildArtistCard(ScUser u)
        {
            var box = new Border { CornerRadius = new CornerRadius(14), Background = Ui.B("#1A1A1A"), Padding = new Thickness(16) };
            var sp = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var av = ArtBox(u.AvatarUrl ?? "", 64, true, "t200x200", 128);
            av.Cursor = Cursors.Hand; av.ToolTip = "Open profile";
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(u); };
            var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), MaxWidth = 150 };
            tx.Children.Add(Link(u.Username ?? "", 15, "#ffffff", () => OpenArtist(u), true));
            string counts = Ui.Count(u.Followers) + " followers" + (u.TrackCount > 0 ? "  ·  " + u.TrackCount + " tracks" : "");
            tx.Children.Add(new TextBlock { Text = counts, Foreground = Ui.B("#8a8a8a"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
            head.Children.Add(av); head.Children.Add(tx);
            sp.Children.Add(head);
            if (u.Id != (api.Active?.UserId ?? 0))
            {
                var btns = new WrapPanel { Margin = new Thickness(0, 14, 0, -8) };
                btns.Children.Add(MakeFollowBtn(u));
                btns.Children.Add(new Pill("", "Profile", () => OpenArtist(u)).Root);
                sp.Children.Add(btns);
            }
            box.Child = sp;
            return box;
        }

        // ================= profile page =================
        async Task LoadUserPageAsync(PaneTab t, int g)
        {
            string id = Uri.EscapeDataString(t.Param);
            var userTask = api.GetUserAsync(t.Param);
            var tracksTask = api.GetEntryPageAsync("/users/" + id + "/tracks?limit=10");
            var u = await userTask;
            if (g != t.Gen) return;
            if (u == null) throw new ApiException(404, "This profile isn't available.");
            t.DUser = u;
            t.Sub = "all";
            t.Subs.Clear();
            if (t.PendingSub != null) { t.Sub = t.PendingSub; t.PendingSub = null; EnsureSubLoaded(t); }
            try
            {
                var page = await tracksTask;
                if (g != t.Gen) return;
                var tmp = new List<ItemVM>();
                AddEntryRowsTo(tmp, false, page.Collection);
                if (tmp.Count > 0)
                {
                    t.Rows.Add(ItemVM.Header("Recent"));
                    foreach (var r in tmp) t.Rows.Add(r);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            if (t == active) ShowMessage(t); // header and recent tracks first, playlists / albums follow
            _ = LoadProfileSideAsync(t, g, u);
            await AddProfileExtrasAsync(t, g);
            t.Next = null;
        }

        readonly Dictionary<long, ProfileSide> sideCache = new Dictionary<long, ProfileSide>();
        readonly HashSet<string> deadSideParts = new HashSet<string>(); // parts SoundCloud answered 400 / 404 once: not asked again this session

        async Task LoadProfileSideAsync(PaneTab t, int g, ScUser u)
        {
            long id = u.Id;
            if (sideCache.TryGetValue(id, out var cached))
            {
                t.Side = cached;
                if (g == t.Gen && t == active && t.Sub == "all") RefreshDetailOf(t);
                return;
            }
            var side = new ProfileSide();
            t.Side = side;
            // one request after the other (a profile used to fire six at once, which looks like a bot to SoundCloud and is slower to settle)
            async Task Part(string name, Func<Task> work)
            {
                if (g != t.Gen || deadSideParts.Contains(name)) return;
                try { await work(); }
                catch (OperationCanceledException) { }
                catch (ApiException ex) when (ex.Status == 400 || ex.Status == 404) { deadSideParts.Add(name); Store.Log("profile side " + name + " " + id, ex); }
                catch (Exception ex) { Store.Log("profile side " + name + " " + id, ex); }
                if (g == t.Gen && t == active && t.Sub == "all") RefreshDetailOf(t);
            }
            await Part("followers", async () => side.Followers = await api.GetUserListAsync("/users/" + id + "/followers?limit=12"));
            await Part("likes", async () => side.Likes = (await api.GetTrackPageAsync("/users/" + id + "/track_likes?limit=3")).Collection);
            await Part("fans", async () => side.Fans = await api.GetUserListAsync("/users/" + id + "/relatedartists?limit=3"));
            await Part("following", async () => side.Followings = await api.GetUserListAsync("/users/" + id + "/followings?limit=3"));
            await Part("links", async () => side.Links = await api.GetWebProfilesAsync(id));
            await Part("comments", async () => side.Comments = await api.GetUserCommentsAsync(id, 3));
            if (g == t.Gen) sideCache[id] = side;
        }

        static string? SubUrl(PaneTab t, string key)
        {
            string id = Uri.EscapeDataString(t.Param);
            return key switch
            {
                "popular" => "/users/" + id + "/toptracks?limit=30",
                "tracks" => "/users/" + id + "/tracks?limit=30",
                "albums" => "/users/" + id + "/albums?limit=30",
                "playlists" => "/users/" + id + "/playlists_without_albums?limit=30",
                "reposts" => "/stream/users/" + id + "/reposts?limit=30",
                "likes" => "/users/" + id + "/track_likes?limit=30",
                "followers" => "/users/" + id + "/followers?limit=30",
                "following" => "/users/" + id + "/followings?limit=30",
                _ => null,
            };
        }

        static SubState GetSub(PaneTab t, string key)
        {
            if (!t.Subs.TryGetValue(key, out var s)) { s = new SubState(); t.Subs[key] = s; }
            return s;
        }

        void EnsureSubLoaded(PaneTab t)
        {
            if (t.Sub == "all") return;
            var st = GetSub(t, t.Sub);
            if (!st.Loaded && !st.Loading) _ = LoadSubAsync(t, t.Sub, false);
        }

        void RefreshDetailOf(PaneTab t)
        {
            if (t != active) return;
            ShowMessage(t);
            MarkPlaying();
        }

        async Task LoadSubAsync(PaneTab t, string key, bool append)
        {
            var st = GetSub(t, key);
            if (st.Loading) return;
            string? url = append ? st.Next : SubUrl(t, key);
            if (url == null) return;
            int g = t.Gen;
            st.Loading = true;
            if (!append) { st.Rows.Clear(); st.Next = null; st.Msg = "Loading…"; }
            RefreshDetailOf(t);
            try
            {
                var page = await api.GetEntryPageAsync(url);
                if (g != t.Gen) return;
                AddEntryRowsTo(st.Rows, false, page.Collection);
                st.Next = page.NextHref;
                st.Loaded = true;
                st.Msg = st.Rows.Count == 0 ? "Nothing here." : "";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { st.Msg = ex.Message; st.Loaded = true; }
            finally { st.Loading = false; }
            RefreshDetailOf(t);
        }

        void OpenProfileSub(PaneTab t, string key)
        {
            t.Sub = key;
            EnsureSubLoaded(t);
            ShowMessage(t);
            MarkPlaying();
            detailScroll.ScrollToTop();
        }

        Border ProfileTab(PaneTab t, string key, string label)
        {
            bool on = t.Sub == key;
            var b = new Border
            {
                Padding = new Thickness(2, 8, 2, 8), Margin = new Thickness(0, 0, 18, 0), BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = on ? Brushes.White : Brushes.Transparent, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = Txt(label, 14, on ? "#ffffff" : "#9a9a9a", on),
            };
            b.MouseLeftButtonUp += (_, _) => { t.Sub = key; EnsureSubLoaded(t); ShowMessage(t); MarkPlaying(); };
            return b;
        }

        UIElement BuildProfileContent(PaneTab t, List<Action> refreshers)
        {
            if (t.Sub == "all")
            {
                var rows = t.Rows.Where(r => r.Kind != "more").ToList();
                if (rows.Count == 0) return Txt("Nothing here yet.", 13, "#808080");
                return BuildItems(rows, it => PlayFromList(t.Rows, it), refreshers);
            }
            var st = GetSub(t, t.Sub);
            string key = t.Sub;
            var box = new StackPanel();
            if (st.Rows.Count == 0)
            {
                var m = Txt(st.Msg.Length > 0 ? st.Msg : "Loading…", 13, "#808080");
                m.Margin = new Thickness(0, 16, 0, 0);
                box.Children.Add(m);
                return box;
            }
            box.Children.Add(BuildItems(st.Rows, it => PlayFromList(st.Rows, it), refreshers));
            if (st.Next != null) box.Children.Add(MoreBtn(() => _ = LoadSubAsync(t, key, true)));
            return box;
        }

        /// <summary>"icon 1.2K   icon 340": the icons use Segoe MDL2 Assets, the numbers the normal font (that font has no digits, which showed as boxes).</summary>
        static TextBlock IconStats(double size, params (string Glyph, string Text)[] items)
        {
            var tb = new TextBlock { FontSize = size, Foreground = Ui.B("#8a8a8a"), TextTrimming = TextTrimming.CharacterEllipsis };
            for (int i = 0; i < items.Length; i++)
            {
                tb.Inlines.Add(new System.Windows.Documents.Run(items[i].Glyph) { FontFamily = new FontFamily("Segoe MDL2 Assets") });
                tb.Inlines.Add(new System.Windows.Documents.Run(" " + items[i].Text + (i < items.Length - 1 ? "     " : "")));
            }
            return tb;
        }

        // ---- profile sidebar sections (like soundcloud.com's right column) ----
        static Border SideRule() => new Border { Height = 1, Background = Ui.B("#262626"), Margin = new Thickness(0, 16, 0, 0) };

        StackPanel SideSection(string title, string? viewAllLabel, Action? viewAll)
        {
            var sec = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            var ttl = new TextBlock { Text = title, Foreground = Ui.B("#c8c8c8"), FontSize = 11, FontWeight = FontWeights.Bold };
            if (viewAll != null)
            {
                ttl.Cursor = Cursors.Hand; ttl.Tag = viewAll; ttl.HorizontalAlignment = HorizontalAlignment.Left;
                ttl.MouseEnter += (_, _) => ttl.Foreground = Brushes.White;
                ttl.MouseLeave += (_, _) => ttl.Foreground = Ui.B("#c8c8c8");
                ttl.MouseLeftButtonUp += (_, e) => { e.Handled = true; viewAll(); };
            }
            head.Children.Add(ttl);
            if (viewAll != null)
            {
                var va = Link(viewAllLabel ?? "View all", 11, "#8a8a8a", viewAll);
                va.HorizontalAlignment = HorizontalAlignment.Right;
                head.Children.Add(va);
            }
            sec.Children.Add(head);
            return sec;
        }

        UIElement SideUserRow(ScUser who, bool withFollow)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var av = ArtBox(who.AvatarUrl ?? "", 40, true, "t200x200", 80);
            av.Cursor = Cursors.Hand;
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(who); };
            var tx = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(Link(who.Username ?? "", 13, "#ffffff", () => OpenArtist(who), true));
            if (who.Verified == true) nameRow.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Ui.B("#4A9BFF"), FontSize = 11, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            tx.Children.Add(nameRow);
            var cntLine = who.TrackCount > 0 ? IconStats(11, ("\uE77B", Ui.Count(who.Followers)), ("\uE8D6", who.TrackCount.ToString())) : IconStats(11, ("\uE77B", Ui.Count(who.Followers)));
            tx.Children.Add(cntLine);
            Grid.SetColumn(av, 0); Grid.SetColumn(tx, 1);
            g.Children.Add(av); g.Children.Add(tx);
            if (withFollow && who.Id != (api.Active?.UserId ?? 0))
            {
                var fb = MakeFollowBtn(who);
                fb.Height = 28; fb.MinWidth = 78; fb.Padding = new Thickness(12, 0, 12, 0); fb.Margin = new Thickness(0);
                if (fb.Child is TextBlock fl) fl.FontSize = 12;
                fb.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(fb, 2); g.Children.Add(fb);
            }
            return g;
        }

        void AddProfileSide(StackPanel side, PaneTab t, ProfileSide ps, ScUser u)
        {
            // links (Spotify, Insta, ...)
            if (ps.Links.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
                foreach (var w in ps.Links.Take(8))
                {
                    string url = w.Url ?? "";
                    string name = !string.IsNullOrWhiteSpace(w.Title) ? w.Title! : (w.Network ?? url);
                    var l = Link(name, 12.5, "#cfcfcf", () => OpenUrl(url));
                    l.Margin = new Thickness(0, 0, 14, 4); l.ToolTip = url;
                    wrap.Children.Add(l);
                }
                side.Children.Add(wrap);
            }
            side.Children.Add(SideRule());

            // likes
            if ((u.LikesCount ?? 0) > 0 || ps.Likes.Count > 0)
            {
                var sec = SideSection(Ui.Count(u.LikesCount ?? ps.Likes.Count) + " LIKES", "View all", () => OpenProfileSub(t, "likes"));
                foreach (var tr in ps.Likes)
                {
                    var tr0 = tr;
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 10), Cursor = Cursors.Hand };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var art = ArtBox(tr.ArtworkUrl ?? tr.User?.AvatarUrl ?? "", 48, false, "t200x200", 96);
                    art.CornerRadius = new CornerRadius(4);
                    var tx = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    tx.Children.Add(Link(tr.User?.Username ?? "", 11.5, "#9a9a9a", () => OpenArtist(tr0.User)));
                    tx.Children.Add(Link(tr.Title, 12.5, "#ffffff", () => OpenTrack(tr0), true));
                    var meta = IconStats(10.5, ("\uE768", Ui.Count(tr.PlaybackCount ?? 0)), ("\uEB51", Ui.Count(tr.LikesCount ?? 0)), ("\uE8EE", Ui.Count(tr.RepostsCount ?? 0)), ("\uE8BD", Ui.Count(tr.CommentCount ?? 0)));
                    meta.Margin = new Thickness(0, 2, 0, 0);
                    tx.Children.Add(meta);
                    Grid.SetColumn(art, 0); Grid.SetColumn(tx, 1);
                    g.Children.Add(art); g.Children.Add(tx);
                    art.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenTrack(tr0); };
                    art.Tag = (Action)(() => OpenTrack(tr0));
                    sec.Children.Add(g);
                }
                if (ps.Likes.Count == 0) sec.Children.Add(Txt("Loading…", 12, "#707070"));
                side.Children.Add(sec);
            }

            // fans also like
            if (ps.Fans.Count > 0)
            {
                var sec = SideSection("FANS ALSO LIKE", null, null);
                foreach (var f in ps.Fans) sec.Children.Add(SideUserRow(f, true));
                side.Children.Add(sec);
            }

            // followers: avatar grid
            if (u.Followers > 0)
            {
                var sec = SideSection(Ui.Count(u.Followers) + " FOLLOWERS", "View all", () => OpenProfileSub(t, "followers"));
                var wrap = new WrapPanel();
                foreach (var f in ps.Followers)
                {
                    var f0 = f;
                    var av = ArtBox(f.AvatarUrl ?? "", 40, true, "t200x200", 80);
                    av.Margin = new Thickness(0, 0, 6, 6); av.Cursor = Cursors.Hand; av.ToolTip = f.Username;
                    av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(f0); };
                    wrap.Children.Add(av);
                }
                sec.Children.Add(wrap);
                side.Children.Add(sec);
            }

            // following
            if ((u.Following ?? 0) > 0 && ps.Followings.Count > 0)
            {
                var sec = SideSection(Ui.Count(u.Following ?? 0) + " FOLLOWING", "View all", () => OpenProfileSub(t, "following"));
                foreach (var f in ps.Followings) sec.Children.Add(SideUserRow(f, true));
                side.Children.Add(sec);
            }

            // latest comments
            if (ps.Comments.Count > 0)
            {
                var sec = SideSection("LATEST COMMENTS", "View all", () => OpenComments(u));
                foreach (var c in ps.Comments)
                {
                    var c0 = c;
                    var box = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                    string ago = Ui.Ago(c.CreatedAt);
                    var head = new WrapPanel();
                    if (c.Track != null && c.Track.Title.Length > 0)
                    {
                        head.Children.Add(Txt("on ", 11.5, "#8a8a8a"));
                        head.Children.Add(Link(c.Track.Title, 11.5, "#ffffff", () => OpenTrack(c0.Track), true));
                    }
                    if (ago.Length > 0) head.Children.Add(Txt("   " + ago, 11, "#707070"));
                    box.Children.Add(head);
                    var body = SelText("“" + (c.Body ?? "") + "”", 12.5, "#d0d0d0");
                    body.Margin = new Thickness(0, 3, 0, 0);
                    box.Children.Add(body);
                    sec.Children.Add(box);
                }
                side.Children.Add(sec);
            }
        }

        /// <summary>Followers / Following / Likes of a profile: a small header, three tabs and a grid of round pictures (like soundcloud.com).</summary>
        UIElement BuildUserSubPage(PaneTab t, ScUser u, List<Action> refreshers)
        {
            var page = new StackPanel { Margin = new Thickness(20, 14, 20, 28) };
            string name = u.Username ?? "";
            string title = t.Sub switch { "followers" => "Followers of " + name, "following" => "Following of " + name, _ => "Likes of " + name };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var av = ArtBox(u.AvatarUrl ?? "", 84, true, "t200x200", 168);
            av.Cursor = Cursors.Hand; av.ToolTip = "Back to the profile";
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenProfileSub(t, "all"); };
            var ttl = new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            head.Children.Add(av); head.Children.Add(ttl);
            page.Children.Add(head);

            var tabsRow = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var key in new[] { "likes", "following", "followers" })
                tabsRow.Children.Add(ProfileTab(t, key, key == "likes" ? "Likes" : key == "following" ? "Following" : "Followers"));
            page.Children.Add(tabsRow);

            if (t.Sub == "likes") { var body = BuildProfileContent(t, refreshers); ((FrameworkElement)body).Margin = new Thickness(0, 14, 0, 0); page.Children.Add(body); return page; }

            var st = GetSub(t, t.Sub);
            string sub = t.Sub;
            if (st.Rows.Count == 0)
            {
                var m = Txt(st.Msg.Length > 0 ? st.Msg : "Loading…", 13, "#808080");
                m.Margin = new Thickness(0, 18, 0, 0);
                page.Children.Add(m);
                return page;
            }
            var wrap = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
            foreach (var r in st.Rows) if (r.User != null) wrap.Children.Add(PersonCard(r.User));
            page.Children.Add(wrap);
            if (st.Next != null) page.Children.Add(MoreBtn(() => _ = LoadSubAsync(t, sub, true)));
            return page;
        }

        /// <summary>A round picture, the name and the follower count; the follow button shows while the mouse is over the card.</summary>
        UIElement PersonCard(ScUser who)
        {
            var sp = new StackPanel { Width = 164, Margin = new Thickness(0, 0, 14, 22), Background = Brushes.Transparent };
            var av = ArtBox(who.AvatarUrl ?? "", 144, true, "t200x200", 288);
            av.Cursor = Cursors.Hand; av.HorizontalAlignment = HorizontalAlignment.Center;
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(who); };
            sp.Children.Add(av);
            var nm = new TextBlock { Text = who.Username ?? "", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 156, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand };
            nm.MouseEnter += (_, _) => nm.TextDecorations = TextDecorations.Underline;
            nm.MouseLeave += (_, _) => nm.TextDecorations = null;
            nm.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(who); };
            sp.Children.Add(nm);
            var cnt = IconStats(11, ("\uE77B", Ui.Count(who.Followers) + " followers"));
            cnt.HorizontalAlignment = HorizontalAlignment.Center; cnt.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(cnt);
            if (who.Id != (api.Active?.UserId ?? 0) && who.Id != 0)
            {
                var fb = MakeFollowBtn(who);
                fb.HorizontalAlignment = HorizontalAlignment.Center; fb.Margin = new Thickness(0, 8, 0, 0); fb.Opacity = 0;
                sp.MouseEnter += (_, _) => fb.Opacity = 1;
                sp.MouseLeave += (_, _) => fb.Opacity = 0;
                sp.Children.Add(fb);
            }
            return sp;
        }

        UIElement BuildUserDetail(PaneTab t)
        {
            var u = t.DUser!;
            var refreshers = new List<Action>();
            detailRefresh = () => { foreach (var a in refreshers.ToList()) a(); };
            if (t.Sub == "followers" || t.Sub == "following" || t.Sub == "likes") return BuildUserSubPage(t, u, refreshers);
            var page = new StackPanel { Margin = new Thickness(20, 14, 20, 28) };

            // ---- banner, avatar, name ----
            var bannerGrid = new Grid { Height = 200 };
            var banner = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = new LinearGradientBrush(Ui.B("#FF4E4E55").Color, Ui.B("#FF9B9BA2").Color, 0),
            };
            if (u.BannerUrl.Length > 0) _ = LoadBannerAsync(u.BannerUrl, banner);
            bannerGrid.Children.Add(banner);
            var avatar = ArtBox(u.AvatarUrl ?? "", 150, true, "t500x500", 300);
            avatar.HorizontalAlignment = HorizontalAlignment.Left; avatar.VerticalAlignment = VerticalAlignment.Center; avatar.Margin = new Thickness(28, 0, 0, 0);
            bannerGrid.Children.Add(avatar);
            var nameBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(200, 0, 16, 0) };
            nameBox.Children.Add(new Border
            {
                Background = Ui.B("#CC000000"), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = u.Username ?? "", Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis },
            });
            var place = string.Join(", ", new[] { u.City, u.CountryCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
            string line2 = !string.IsNullOrWhiteSpace(u.FullName) && !string.Equals(u.FullName, u.Username, StringComparison.OrdinalIgnoreCase) ? u.FullName! : "";
            if (place.Length > 0) line2 = line2.Length > 0 ? line2 + "  ·  " + place : place;
            if (line2.Length > 0)
                nameBox.Children.Add(new Border
                {
                    Background = Ui.B("#CC000000"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0),
                    Child = Txt(line2, 12.5, "#e0e0e0"),
                });
            bannerGrid.Children.Add(nameBox);
            page.Children.Add(bannerGrid);

            // ---- tabs (left) and buttons (right) on one row, like soundcloud.com ----
            var bar = new Grid { Margin = new Thickness(0, 12, 0, 6) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tabsRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var (key, label) in ProfileTabs)
                if (key != "likes" && key != "followers" && key != "following") tabsRow.Children.Add(ProfileTab(t, key, label));
            Grid.SetColumn(tabsRow, 0); bar.Children.Add(tabsRow);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            btns.Children.Add(new Pill("\uE93E", "Station", () => StartStation("soundcloud:artist-stations:" + u.Id, u.Username ?? "artist")).Root);
            if (u.Id != (api.Active?.UserId ?? 0)) btns.Children.Add(MakeFollowBtn(u));
            btns.Children.Add(new Pill("\uE72D", "Share", () => { CopyLink(u.PermalinkUrl); ShowToast("Link copied", true); }).Root);
            if (!string.IsNullOrEmpty(u.PermalinkUrl)) btns.Children.Add(new Pill("\uE8A7", "SoundCloud", () => OpenUrl(u.PermalinkUrl)).Root);
            Grid.SetColumn(btns, 1); bar.Children.Add(btns);
            page.Children.Add(bar);

            // ---- side: counters and bio ----
            var side = new StackPanel();
            var stats = new Grid();
            for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Stat(int col, string label, long value, string key)
            {
                var sp = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Show " + label.ToLowerInvariant() };
                var lb = new TextBlock { Text = label, Foreground = Ui.B("#9a9a9a"), FontSize = 12, FontWeight = FontWeights.SemiBold };
                var num = new TextBlock { Text = Ui.Count(value), Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
                sp.Children.Add(lb); sp.Children.Add(num);
                sp.MouseEnter += (_, _) => { lb.Foreground = Brushes.White; num.Foreground = Ui.B("#FF7A3D"); num.TextDecorations = TextDecorations.Underline; };
                sp.MouseLeave += (_, _) => { lb.Foreground = Ui.B("#9a9a9a"); num.Foreground = Brushes.White; num.TextDecorations = null; };
                sp.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenProfileSub(t, key); };
                Grid.SetColumn(sp, col); stats.Children.Add(sp);
            }
            Stat(0, "Followers", u.Followers, "followers");
            Stat(1, "Following", u.Following ?? 0, "following");
            Stat(2, "Tracks", u.TrackCount, "tracks");
            side.Children.Add(stats);
            if (!string.IsNullOrWhiteSpace(u.Description))
            {
                string d = u.Description!.Trim();
                if (d.Length > 800) d = d.Substring(0, 800) + "…";
                { var bio = SelText(d, 13, "#cfcfcf"); bio.Margin = new Thickness(0, 14, 0, 0); side.Children.Add(bio); }
            }
            if (t.Side != null) AddProfileSide(side, t, t.Side, u);
            else if ((u.LikesCount ?? 0) > 0)
            {
                var likesLine = Txt("♥  " + Ui.Count(u.LikesCount ?? 0) + " likes", 12.5, "#8a8a8a");
                likesLine.Margin = new Thickness(0, 14, 0, 0);
                side.Children.Add(likesLine);
            }

            var main = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            main.Children.Add(BuildProfileContent(t, refreshers));
            TwoCol(page, main, side);
            return page;
        }
    }
}
