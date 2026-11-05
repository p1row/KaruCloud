using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace KaruCloud
{
    public sealed partial class MainWindow : Window
    {
        const double NavH = 44;

        const string ItemStyleXaml = """
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
  <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="ListBoxItem">
        <Border x:Name="b" Background="Transparent" CornerRadius="14" Padding="8,0" Margin="0,1">
          <ContentPresenter/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="#1FFFFFFF"/></Trigger>
          <Trigger Property="IsSelected" Value="True"><Setter TargetName="b" Property="Background" Value="#26FFFFFF"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
""";

        const string ItemTemplateXaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid x:Name="row" Height="54" Background="Transparent">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="54"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <Border Width="40" Height="40" CornerRadius="10" Background="#2A2A2A" HorizontalAlignment="Left" Tag="art" Cursor="{Binding TitleCursor}">
      <Border CornerRadius="10">
        <Border.Background><ImageBrush ImageSource="{Binding Art}" Stretch="UniformToFill"/></Border.Background>
      </Border>
    </Border>
    <Border x:Name="playOv" Width="26" Height="26" CornerRadius="13" Background="#FFFF5500" HorizontalAlignment="Left" Margin="7,0,0,0" Visibility="Collapsed" Tag="play" Cursor="Hand" ToolTip="Play">
      <TextBlock Text="&#xE768;" FontFamily="Segoe MDL2 Assets" FontSize="10" Foreground="White" HorizontalAlignment="Center" VerticalAlignment="Center" Margin="1,0,0,0"/>
    </Border>
    <StackPanel Grid.Column="1" VerticalAlignment="Center">
      <TextBlock x:Name="titleTb" Tag="title" Text="{Binding Title}" Foreground="{Binding TitleBrush}" FontSize="13" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" HorizontalAlignment="Left" Cursor="{Binding TitleCursor}"/>
      <TextBlock x:Name="artistTb" Tag="artist" Text="{Binding Artist}" Foreground="#8A8A8A" FontSize="12" TextTrimming="CharacterEllipsis" Margin="0,2,0,0" HorizontalAlignment="Left" Cursor="{Binding TitleCursor}"/>
    </StackPanel>
    <TextBlock Grid.Column="2" Tag="like" Visibility="{Binding ActionsVis}" Text="{Binding LikeGlyph}" Foreground="{Binding LikeBrush}" FontFamily="Segoe MDL2 Assets" FontSize="15" Width="32" Height="32" Padding="0,8,0,0" TextAlignment="Center" Background="Transparent" VerticalAlignment="Center" Cursor="Hand" ToolTip="Like"/>
    <TextBlock Grid.Column="3" Tag="repost" Visibility="{Binding ActionsVis}" Text="&#xE8EE;" Foreground="{Binding RepostBrush}" FontFamily="Segoe MDL2 Assets" FontSize="15" Width="32" Height="32" Padding="0,8,0,0" TextAlignment="Center" Background="Transparent" VerticalAlignment="Center" Cursor="Hand" ToolTip="Repost"/>
    <TextBlock Grid.Column="4" Text="{Binding DurationText}" Foreground="#777777" FontSize="12" Margin="6,0,6,0" VerticalAlignment="Center"/>
  </Grid>
  <DataTemplate.Triggers>
    <MultiDataTrigger>
      <MultiDataTrigger.Conditions>
        <Condition Binding="{Binding IsMouseOver, ElementName=row}" Value="True"/>
        <Condition Binding="{Binding CanPlay}" Value="True"/>
      </MultiDataTrigger.Conditions>
      <Setter TargetName="playOv" Property="Visibility" Value="Visible"/>
    </MultiDataTrigger>
    <Trigger SourceName="titleTb" Property="IsMouseOver" Value="True"><Setter TargetName="titleTb" Property="TextDecorations" Value="Underline"/></Trigger>
    <Trigger SourceName="artistTb" Property="IsMouseOver" Value="True"><Setter TargetName="artistTb" Property="TextDecorations" Value="Underline"/></Trigger>
  </DataTemplate.Triggers>
</DataTemplate>
""";

        // One tab = one list (likes, playlists, recent, a search, or one playlist's tracks).
        sealed class PaneTab
        {
            public int Id;
            public string Acct = "";
            public string Kind = "likes"; // home | feed | trending | top | stations | likes | playlists | albums | following | reposts | mytracks | recent | queue | search* | user | playlist
            public string Title = "";
            public string Param = "";
            public ObservableCollection<ItemVM> Rows = new ObservableCollection<ItemVM>();
            public string? Next;
            public bool Loaded;
            public string Message = "";
            public int Gen;
            public DateTime LastActive = DateTime.UtcNow;
            public bool AutoPlay; // start playing the first track as soon as the list has loaded (stations)
            public ScTrack? DTrack; // "track" tabs: the song shown on its own page
            public ScUser? DUser;   // "user" tabs: the profile; "track" tabs: the artist
            public ScPlaylist? DPlaylist; // "playlist" tabs: the playlist shown as a page (stations get a made-up one, see IsStation)
            public string Filter = "";    // likes page: filter box text
            public bool GridView = true;  // likes page: artwork grid (true) or list (false)
            public bool ShowReposts = true; // feed page: show reposts
            public string Sub = "all"; // profile sub-tab: all | popular | tracks | albums | playlists | reposts
            public Dictionary<string, SubState> Subs = new Dictionary<string, SubState>();
            public ProfileSide? Side;      // "user" tabs: the sidebar (links, likes, fans also like, followers, following, comments)
            public Dictionary<string, long> Facets = new Dictionary<string, long>(); // search pages: totals per kind (track / user / playlist / album)
            public bool IsStation;       // "playlist" tabs that are stations: shown with the station page
            public ScTrack? StationSeed; // track stations: the song the station was made from
            public ScUser? StationArtist; // artist stations: the artist
            public List<ScComment> Comments = new List<ScComment>(); // "usercomments" tabs: the comments shown (t.DUser = the person)
            public string? PendingSub; // "user" tabs: open this profile tab (likes / following / followers) once the profile has loaded
            public List<TabEntry> Back = new List<TabEntry>(); // pages this tab showed before (a link click replaces the page, like a browser)
            public List<TabEntry> Fwd = new List<TabEntry>();
        }

        sealed class TabEntry { public string Kind = ""; public string Title = ""; public string Param = ""; }

        sealed class SavedTab { public string Acct { get; set; } = ""; public string Kind { get; set; } = "likes"; public string Title { get; set; } = ""; public string Param { get; set; } = ""; }
        sealed class SavedState { public List<SavedTab> Tabs { get; set; } = new List<SavedTab>(); public int Active { get; set; } }

        readonly Config cfg = Store.Load<Config>("config.json");
        readonly ScApi api;
        readonly Player player;
        readonly DiscordRpc rpc = new DiscordRpc();
        readonly WinForms.NotifyIcon tray = new WinForms.NotifyIcon();
        SettingsWindow? settingsWin;
        TrayFlyout? flyout;
        LoginWindow? loginWin;
        bool quitting, inited;
        HashSet<long> likedIds = new HashSet<long>();

        readonly List<PaneTab> tabs = new List<PaneTab>();
        PaneTab? active;
        int nextId = 1;

        // window frame (transparent window, rounded outer border)
        const double Radius = 22;
        readonly Border outer = new Border();
        readonly Border headerBorder = new Border();
        readonly Border barBorder = new Border();

        // header
        readonly Grid rootGrid = new Grid();
        readonly StackPanel tabStrip = new StackPanel { Orientation = Orientation.Horizontal };
        readonly ScrollViewer tabScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        readonly TextBlock acctText = new TextBlock { Foreground = Ui.B("#eeeeee"), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        readonly TextBlock maxText = new TextBlock { Text = "\uE922" };
        readonly TextBox searchBox = new TextBox();
        readonly TextBlock searchHint = new TextBlock();
        Border acctBtn = new Border();
        Border plusBtn = new Border();
        // right-hand cluster like soundcloud.com: profile picture, bell, envelope, more
        // The header shows your SoundCloud profile photo (the silhouette is the placeholder until it loads, and the icon of
        // the profile entries in the sidebar / tabs). Set to false for the plain silhouette in the header.
        const bool PhotoInHeader = true;
        const string SilhouetteData =
            "M 288,302 A 202,202 0 1 0 692,302 A 202,202 0 1 0 288,302 Z " +
            "M 337,488 Q 490,596 640,488 C 730,535 795,640 795,750 C 795,830 650,880 490,880 C 330,880 185,830 185,750 C 185,640 250,535 337,488 Z";
        static UIElement MakeSilhouette() => new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(SilhouetteData), Fill = Ui.B("#E6E6E6"), Stretch = Stretch.Uniform, Width = 19, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        /// <summary>The silhouette as a small text-like icon: it follows the TextBlock's Foreground, so sidebar / tab highlighting still recolors it.</summary>
        static TextBlock SilhouetteGlyph(double height, double width, Brush fg, Thickness margin)
        {
            var tb = new TextBlock { Width = width, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = margin };
            var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(SilhouetteData), Stretch = Stretch.Uniform, Height = height, Width = height * 0.86 };
            path.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = tb });
            tb.Inlines.Add(new System.Windows.Documents.InlineUIContainer(path) { BaselineAlignment = BaselineAlignment.Center });
            return tb;
        }
        readonly Border avatarBox = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Brushes.Transparent };
        readonly StackPanel acctPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        readonly Border bellDot = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(6), Background = Ui.B("#FF5500"), BorderBrush = Ui.B("#141414"), BorderThickness = new Thickness(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        readonly Border mailDot = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(6), Background = Ui.B("#FF5500"), BorderBrush = Ui.B("#141414"), BorderThickness = new Thickness(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        readonly DispatcherTimer badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(4) };
        Popup? popup;

        // body
        readonly ListBox list = new ListBox();
        readonly TextBlock message = new TextBlock();

        // sidebar (the pages soundcloud.com has in its menus)
        // Kind "#" is a section header (its Title is the heading). "profile" opens your own profile.
        static readonly (string Kind, string Title, string Glyph)[] Nav =
        {
            ("#", "Discover", ""),
            ("home", "Home", "⌂"), ("feed", "Feed", "≋"), ("stations", "Stations", "◎"),
            ("#", "Library", ""),
            ("likes", "Likes", "♥"), ("playlists", "Playlists", "☰"), ("albums", "Albums", "▣"), ("reposts", "Reposts", "↻"), ("following", "Following", "☻"),
            ("#", "Listening", ""),
            ("recent", "History", "↺"), ("queue", "Queue", "≣"),
        };
        readonly Dictionary<string, Border> navRows = new Dictionary<string, Border>();
        readonly TextBlock autoplayText = new TextBlock();
        string searchScope = "all"; // all | tracks | playlists | people
        readonly TextBlock scopeChip = new TextBlock();
        static readonly HashSet<string> SearchKinds = new HashSet<string> { "search", "searchtracks", "searchplaylists", "searchalbums", "searchusers" };
        bool autoplaying;

        // player bar
        readonly Border artInner = new Border();
        readonly ImageBrush artBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
        readonly TextBlock titleText = new TextBlock();
        readonly TextBlock artistText = new TextBlock();
        readonly TextBlock attribText = new TextBlock();
        readonly Image attribLogo = new Image();
        readonly TextBlock playGlyph = new TextBlock();
        readonly TextBlock shuffleGlyph = new TextBlock();
        readonly TextBlock repeatGlyph = new TextBlock();
        readonly TextBlock artistFollowGlyph = new TextBlock();
        readonly TextBlock heartGlyph = new TextBlock();
        readonly TextBlock repostGlyph = new TextBlock();
        readonly TextBlock volGlyph = new TextBlock();
        readonly TextBlock posText = new TextBlock();
        readonly TextBlock durText = new TextBlock();
        readonly TextBlock toast = new TextBlock();
        MiniSlider seek = new MiniSlider(1000, 0);
        MiniSlider volSlider = new MiniSlider(100, 80);

        // playback state
        List<ScTrack> queue = new List<ScTrack>();
        int qi = -1;
        ScTrack? current;
        int failStreak;
        readonly Random rng = new Random();

        readonly DispatcherTimer uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer presenceTimer = new DispatcherTimer();
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

        public bool StartMinimized => cfg.Settings.StartMinimized;

        public MainWindow()
        {
            urlHandler = OpenInApp;
            cfg.Normalize();
            api = new ScApi(cfg, () => Store.Save("config.json", cfg));
            api.HistorySynced += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var t in Mine().Where(x => x.Kind == "recent")) t.Loaded = false; // History reloads next time it's opened
            }));
            api.Notice += m => Dispatcher.BeginInvoke(new Action(() => ShowToast(m, true)));
            player = new Player(api);
            ArtCache.Init(api);
            ItemVM.IsLiked = id => likedIds.Contains(id);
            ItemVM.IsReposted = id => repostedIds.Contains(id);
            if (!cfg.Accounts.Any(a => a.Id == cfg.Current)) cfg.Current = cfg.Accounts.FirstOrDefault()?.Id ?? "";

            Title = "KaruCloud";
            Width = 1000; Height = 680; MinWidth = 720; MinHeight = 500;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Icon = LoadIconFrame();

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = NavH,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(NavH) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var nav = BuildHeader();
            var body = BuildBody();
            var bar = BuildPlayerBar();
            Grid.SetRow(nav, 0); Grid.SetRow(body, 1); Grid.SetRow(bar, 2);
            rootGrid.Children.Add(nav); rootGrid.Children.Add(body); rootGrid.Children.Add(bar);
            outer.Background = Ui.B("#121212");
            outer.BorderBrush = Ui.B("#2EFFFFFF");
            outer.BorderThickness = new Thickness(1);
            outer.Child = rootGrid;
            Content = outer;
            ApplyRadius(Radius);
            HookScrolling();

            Loaded += async (_, _) => { try { await OnFirstShownAsync(); } catch (Exception ex) { Store.Log("first shown", ex); } };
            tabScroll.SizeChanged += (_, _) => RefreshTabs();

            player.Loading += () => { titleText.Opacity = 0.6; UpdateTransport(); };
            player.Started += OnPlayerStarted;
            player.Ended += () => Dispatcher.InvokeAsync(() => Next(false));
            player.Failed += OnPlayerFailed;
            rpc.Changed += () => Dispatcher.InvokeAsync(() => settingsWin?.Refresh());

            uiTimer.Tick += (_, _) => UiTick();
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); WriteTabs(); };
            presenceTimer.Tick += (_, _) => { presenceTimer.Stop(); FlushPresence(); };
            toastTimer.Tick += (_, _) => { toastTimer.Stop(); toast.Visibility = Visibility.Collapsed; };
            uiTimer.Start();
            // The bell / envelope dots: one tiny request every 2 minutes, only while the window is actually showing.
            badgeTimer.Tick += async (_, _) => { if (IsVisible && IsActive && WindowState != WindowState.Minimized) await RefreshBadgesAsync(); }; // only while you're actually looking at the window
            Activated += async (_, _) => { if (DateTime.UtcNow - lastBadges > TimeSpan.FromSeconds(90)) await RefreshBadgesAsync(); };
            badgeTimer.Start();
            // new comments on the song page you're looking at show up by themselves (one request a minute while it plays)
            commentTimer.Tick += (_, _) => { if (commentsReload != null && IsVisible && WindowState != WindowState.Minimized && player.IsPlaying) commentsReload(); };
            commentTimer.Start();
            IsVisibleChanged += (_, _) => { if (IsVisible) _ = RefreshBadgesAsync(); };

            RestoreTabs();
            BuildTray();
            ApplySettings();
            RefreshAccountButton();
            UpdateNowPlayingUi();
        }

        // ================= helpers =================
        static ImageSource LoadIconFrame()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico")!;
            return BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);

        /// <summary>Big rounded window corners (the window itself is transparent; this border is what you see).</summary>
        void ApplyRadius(double r)
        {
            outer.CornerRadius = new CornerRadius(r);
            double ri = Math.Max(0, r - 1);
            headerBorder.CornerRadius = new CornerRadius(ri, ri, 0, 0);
            barBorder.CornerRadius = new CornerRadius(0, 0, ri, ri);
            // the close button's red hover follows the window's top-right curve instead of sticking out as a square
            if (closeBtn != null) closeBtn.CornerRadius = new CornerRadius(0, ri, 0, 0);
        }

        // Links never launch the default browser: soundcloud.com links open as a tab in KaruCloud, anything else is copied.
        static Action<string>? urlHandler;
        bool bgOpen; // set while a middle click is being handled: tabs open in the background instead of taking over

        static void OpenUrl(string? url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://")) return;
            urlHandler?.Invoke(url);
        }

        async void OpenInApp(string url)
        {
            bool bg = bgOpen;
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
                string host = uri.Host.ToLowerInvariant();
                bool sc = host == "soundcloud.com" || host.EndsWith(".soundcloud.com", StringComparison.Ordinal);
                if (!sc || uri.AbsolutePath.Trim('/').Length == 0)
                {
                    CopyLink(url);
                    ShowToast("Link copied (KaruCloud doesn't open your browser)", true);
                    return;
                }
                var r = await api.ResolveAsync(url);
                bgOpen = bg;
                try
                {
                    if (r.Track != null) OpenTrack(r.Track);
                    else if (r.User != null) OpenArtist(r.User);
                    else if (r.Playlist != null) NavigateHere("playlist", Cut(r.Playlist.Title, 24), r.Playlist.Key);
                    else { CopyLink(url); ShowToast("Couldn't open this link here - it was copied instead", true); }
                }
                finally { bgOpen = false; }
            }
            catch (Exception ex)
            {
                Store.Log("open link", ex);
                CopyLink(url);
                ShowToast("Couldn't open this link here - it was copied instead", true);
            }
        }

        /// <summary>The thing a middle click would open: a link (its Tag is the click action) or a song's title / cover / artist in a list.</summary>
        bool TryMiddleTarget(MouseButtonEventArgs e, out Action? act)
        {
            act = null;
            string? tag = null;
            ListBoxItem? item = null;
            for (var d = e.OriginalSource as DependencyObject; d != null; d = ParentOf(d))
            {
                if (d is FrameworkElement fe && act == null && tag == null)
                {
                    if (fe.Tag is Action a) act = a;
                    else if (fe.Tag is string s && (s == "title" || s == "art" || s == "artist")) tag = s;
                }
                if (d is ListBoxItem lbi) { item = lbi; break; }
                if (ReferenceEquals(d, list)) break;
            }
            if (act == null && tag != null && item?.DataContext is ItemVM it && it.Kind != "header" && it.Kind != "more")
            {
                string tg = tag;
                act = () => RowAction(it, tg);
            }
            return act != null;
        }

        protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseUp(e);
            if (AutoScrollMouseUp(e)) return;
            if (e.ChangedButton != MouseButton.Middle || e.Handled) return;
            if (!TryMiddleTarget(e, out var act)) return;
            e.Handled = true;
            bgOpen = true;
            try { act!(); }
            catch (Exception ex) { Store.Log("middle click", ex); }
            finally { bgOpen = false; }
        }

        void InNewTab(Action act)
        {
            bgOpen = true;
            try { act(); }
            catch (Exception ex) { Store.Log("open in new tab", ex); }
            finally { bgOpen = false; }
        }

        List<PaneTab> Mine() => tabs.Where(t => t.Acct == cfg.Current).ToList();

        /// <summary>Open a song's page inside KaruCloud (the song plus related tracks) instead of the browser.</summary>
        void OpenTrack(ScTrack? tr)
        {
            if (tr == null || tr.Id == 0) return;
            string id = tr.Id.ToString();
            string title = Cut(tr.Title.Length > 0 ? tr.Title : "Track", 24);
            if (bgOpen)
            {
                var ex = Mine().FirstOrDefault(t => t.Kind == "track" && t.Param == id);
                if (ex != null) { ActivateTab(ex); return; }
                AddTab("track", title, id);
                return;
            }
            NavigateHere("track", title, id);
        }

        /// <summary>Open an artist's profile inside KaruCloud.</summary>
        void OpenArtist(ScUser? u)
        {
            if (u == null || u.Id == 0) return;
            string id = u.Id.ToString();
            string title = Cut(u.Username ?? "Profile", 24);
            if (bgOpen)
            {
                var ex = Mine().FirstOrDefault(t => t.Kind == "user" && t.Param == id);
                if (ex != null) { ActivateTab(ex); return; }
                AddTab("user", title, id);
                return;
            }
            NavigateHere("user", title, id);
        }

        // ================= header (flat, auto-hiding tab strip) =================
        Border BuildHeader()
        {
            var g = new Grid();
            for (int i = 0; i < 6; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) };
            left.Children.Add(new Image { Source = Icon, Width = 22, Height = 22 });

            // profile picture + chevron (opens the account menu)
            avatarBox.Child = MakeSilhouette();
            acctPanel.Children.Add(avatarBox);
            acctPanel.Children.Add(new TextBlock { Text = "\uE70D", FontFamily = Ui.Icons, FontSize = 10, Foreground = Ui.B("#bbbbbb"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            acctBtn = new Border
            {
                Height = 32, Padding = new Thickness(3, 0, 10, 0), CornerRadius = new CornerRadius(16),
                Background = Brushes.Transparent, ToolTip = "Account", Cursor = Cursors.Hand, Child = acctPanel,
            };
            Ui.Hover(acctBtn, "#22FFFFFF");
            acctBtn.MouseLeftButtonUp += (_, _) => ShowAccountMenu();
            WindowChrome.SetIsHitTestVisibleInChrome(acctBtn, true);

            tabScroll.Content = tabStrip;
            tabScroll.VerticalAlignment = VerticalAlignment.Center;
            tabScroll.Margin = new Thickness(6, 0, 6, 0);

            // search box
            searchBox.Background = Brushes.Transparent; searchBox.Foreground = Brushes.White; searchBox.CaretBrush = Brushes.White;
            searchBox.BorderThickness = new Thickness(0); searchBox.Padding = new Thickness(0); searchBox.FontSize = 12.5;
            searchBox.VerticalContentAlignment = VerticalAlignment.Center; searchBox.SelectionBrush = Ui.B("#FF5500");
            searchHint.Text = "Search SoundCloud"; searchHint.Foreground = Ui.B("#707070"); searchHint.FontSize = 12.5;
            searchHint.IsHitTestVisible = false; searchHint.VerticalAlignment = VerticalAlignment.Center;
            searchBox.TextChanged += (_, _) => searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            searchBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SearchFromBox(); e.Handled = true; } };
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            scopeChip.Text = "All ▾"; scopeChip.Foreground = Ui.B("#9a9a9a"); scopeChip.FontSize = 11.5; scopeChip.Cursor = Cursors.Hand;
            scopeChip.VerticalAlignment = VerticalAlignment.Center; scopeChip.Margin = new Thickness(6, 0, 0, 0);
            scopeChip.MouseLeftButtonUp += (_, _) => ShowScopeMenu();
            Grid.SetColumnSpan(searchHint, 1);
            Grid.SetColumn(scopeChip, 1);
            sg.Children.Add(searchHint); sg.Children.Add(searchBox); // the "All" scope chip was removed from the box (scope is still switchable on the results page)
            var searchWrap = new Border
            {
                Width = 250, Height = 28, CornerRadius = new CornerRadius(14), Background = Ui.B("#1AFFFFFF"),
                Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(4, 0, 6, 0), Child = sg,
            };
            WindowChrome.SetIsHitTestVisibleInChrome(searchWrap, true);

            plusBtn = MakeBtn("+", ShowNewTabMenu, "New tab (Ctrl+T)"); // kept only as a menu anchor; not shown in the header any more
            var bell = DotButton("\uE7ED", "Notifications", bellDot, () => OpenInbox(false), out bellBtnRef);
            var mail = DotButton("\uE715", "Messages", mailDot, () => OpenInbox(true), out mailBtnRef);
            var more = IconOnlyButton("\uE713", "Settings", () => OpenSettings(), out moreBtnRef);
            var userBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            userBar.Children.Add(acctBtn); userBar.Children.Add(bell); userBar.Children.Add(mail); userBar.Children.Add(more);

            var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
            caption.Children.Add(CaptionBtn(new TextBlock { Text = "\uE921" }, () => WindowState = WindowState.Minimized, false));
            caption.Children.Add(CaptionBtn(maxText, () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, false));
            closeBtn = CaptionBtn(new TextBlock { Text = "\uE8BB" }, () => Close(), true);
            caption.Children.Add(closeBtn);

            Grid.SetColumn(left, 0); Grid.SetColumn(tabScroll, 1); Grid.SetColumn(searchWrap, 2);
            Grid.SetColumn(userBar, 4); Grid.SetColumn(caption, 5);
            g.Children.Add(left); g.Children.Add(tabScroll); g.Children.Add(searchWrap);
            g.Children.Add(userBar); g.Children.Add(caption);
            headerBorder.Background = Ui.B("#141414");
            headerBorder.Child = g;
            return headerBorder;
        }

        Border MakeBtn(string glyph, Action click, string tip)
        {
            var b = new Border
            {
                Width = 30, Height = 30, Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(15),
                Background = Brushes.Transparent, ToolTip = tip, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = glyph, Foreground = Ui.B("#bbbbbb"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(b, "#22FFFFFF");
            b.MouseLeftButtonUp += (_, _) => click();
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            return b;
        }

        Border? bellBtnRef, mailBtnRef, moreBtnRef;

        static Border RoundIcon(string glyph, string tip, UIElement? extra, Action click)
        {
            var host = new Grid();
            host.Children.Add(new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 16, Foreground = Ui.B("#dddddd"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            if (extra != null) host.Children.Add(extra);
            var b = new Border { Width = 34, Height = 32, CornerRadius = new CornerRadius(16), Background = Brushes.Transparent, ToolTip = tip, Cursor = Cursors.Hand, Child = host };
            Ui.Hover(b, "#22FFFFFF");
            b.MouseLeftButtonUp += (_, _) => click();
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            return b;
        }

        /// <summary>Header icon with the small orange "something new" dot at its top right.</summary>
        static Border DotButton(string glyph, string tip, Border dot, Action click, out Border? self)
        {
            dot.HorizontalAlignment = HorizontalAlignment.Right; dot.VerticalAlignment = VerticalAlignment.Top; dot.Margin = new Thickness(0, 2, 3, 0);
            var b = RoundIcon(glyph, tip, dot, click);
            self = b;
            return b;
        }

        static Border IconOnlyButton(string glyph, string tip, Action click, out Border? self)
        {
            var b = RoundIcon(glyph, tip, null, click);
            self = b;
            return b;
        }

        Border? closeBtn;

        Border CaptionBtn(TextBlock glyph, Action click, bool isClose)
        {
            glyph.FontFamily = Ui.Icons; glyph.FontSize = 10; glyph.Foreground = Ui.B("#e6e6e6");
            glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
            var b = new Border { Width = 46, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = glyph };
            Ui.Hover(b, isClose ? "#E81123" : "#22FFFFFF");
            b.MouseLeftButtonUp += (_, _) => click();
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            return b;
        }

        void RefreshAccountButton()
        {
            var a = cfg.Accounts.FirstOrDefault(x => x.Id == cfg.Current);
            acctText.Text = "Sign in"; acctText.Margin = new Thickness(9, 0, 0, 0);
            if (a == null)
            {
                acctBtn.Child = acctText; acctBtn.ToolTip = "Sign in";
                bellDot.Visibility = Visibility.Collapsed; mailDot.Visibility = Visibility.Collapsed;
                return;
            }
            acctBtn.Child = acctPanel; acctBtn.ToolTip = a.Name;
            avatarBox.Background = Brushes.Transparent;
            if (avatarBox.Child == null) avatarBox.Child = MakeSilhouette();
            _ = LoadAvatarAsync();
            _ = RefreshBadgesAsync();
        }

        /// <summary>Your SoundCloud profile picture in the header (fetched once for older accounts, then kept in config.json).</summary>
        async Task LoadAvatarAsync()
        {
            try
            {
                var a = cfg.Accounts.FirstOrDefault(x => x.Id == cfg.Current);
                if (a == null) return;
                string id = a.Id, url = a.AvatarUrl ?? "";
                if (url.Length == 0)
                {
                    url = await api.RefreshAvatarAsync(a);
                    if (url.Length > 0) Store.Save("config.json", cfg);
                }
                if (url.Length == 0 || cfg.Current != id) return;
                var img = await ArtCache.GetAsync(ArtCache.Size(url, "t200x200"), 64);
                if (PhotoInHeader && img != null && cfg.Current == id)
                {
                    avatarBox.Child = null;
                    avatarBox.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        DateTime lastBadges = DateTime.MinValue;

        async Task RefreshBadgesAsync()
        {
            try
            {
                var a = api.Active;
                if (a == null || !IsVisible) return;
                lastBadges = DateTime.UtcNow;
                string id = a.Id;
                var b = await api.GetBadgesAsync();
                if (b == null || cfg.Current != id) return;
                bellDot.Visibility = b.Value.Notifications > 0 ? Visibility.Visible : Visibility.Collapsed;
                mailDot.Visibility = b.Value.Messages > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        // ---- bell / envelope: the real soundcloud.com pages in a small window (browser is created on open, released on close) ----
        WebPanel? webPanel;
        bool notifApiDead; // the notifications list route answered 404: use the in-app page directly from now on

        /// <summary>
        /// Bell / envelope: a light list right under the icon, read straight from SoundCloud (no browser engine at all).
        /// Only if SoundCloud refuses that list does the compact web panel open instead.
        /// </summary>
        void OpenInbox(bool messages)
        {
            if (api.Active == null) { ShowToast("Sign in to SoundCloud first."); return; }
            var dot = messages ? mailDot : bellDot;
            bool hadNew = dot.Visibility == Visibility.Visible;
            dot.Visibility = Visibility.Collapsed;
            string title = messages ? "Messages" : "Notifications";
            string url = messages ? "https://soundcloud.com/messages" : "https://soundcloud.com/notifications";
            var anchor = messages ? mailBtnRef : bellBtnRef;
            if (anchor == null || (!messages && notifApiDead)) { OpenWebPanel(title, url, messages); return; }
            ShowFeed(anchor, title, () => messages ? api.GetConversationsAsync() : api.GetNotificationsAsync(), url,
                messages ? "Write a message…" : "Open full page", messages, () => OpenWebPanel(title, url, messages), hadNew);
        }

        async void CheckSync()
        {
            ShowToast("Checking sync…", true);
            string report;
            try { report = await api.DiagnoseSyncAsync(); }
            catch (Exception ex) { Store.Log("check sync", ex); report = "The check itself failed: " + ex.Message; }
            try { Clipboard.SetText(report); } catch { }
            MessageBox.Show(this, report + "\n(This report was copied to the clipboard.)", "KaruCloud - sync check", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void OpenWebPanel(string title, string url, bool wide = false)
        {
            if (api.Active == null) { ShowToast("Sign in to SoundCloud first."); return; }
            string token = api.ActiveToken;
            if (token.Length == 0) { ShowToast("This account is signed out. Sign in again from Settings."); return; }
            if (webPanel != null) { try { webPanel.Close(); } catch { } webPanel = null; }
            var w = new WebPanel(title, url, token, wide) { Owner = this };
            webPanel = w;
            w.Closed += (_, _) => { if (ReferenceEquals(webPanel, w)) webPanel = null; };
            w.Show();
        }

        async void FixSync()
        {
            try
            {
                ShowToast("Opening soundcloud.com. Browse around for a moment, then close that window.", true);
                await api.OpenSecurityCheckAsync();
                ShowToast("Done. Try the like / repost again.", true);
            }
            catch (Exception ex) { Store.Log("security check", ex); ShowToast(ex.Message); }
        }

        // ---- bell / envelope: the light built-in list (the web panel above is only its fallback) ----
        async void ShowFeed(FrameworkElement anchor, string title, Func<Task<List<FeedItem>>> load, string openUrl, string openText, bool messages = false, Action? fallback = null, bool expectItems = false)
        {
            var panel = new StackPanel { Width = 360 };
            panel.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(16, 12, 16, 6) });
            var body = new StackPanel();
            var wait = Txt("Loading…", 12.5, "#8a8a8a"); wait.Margin = new Thickness(16, 6, 16, 12);
            body.Children.Add(wait);
            panel.Children.Add(new ScrollViewer { MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
            panel.Children.Add(new Border { Height = 1, Background = Ui.B("#333333"), Margin = new Thickness(8, 4, 8, 4) });
            panel.Children.Add(MenuRow(openText, () => { CloseMenu(); if (fallback != null) fallback(); else OpenUrl(openUrl); }));
            OpenMenu(anchor, panel);
            var mine = popup;
            try
            {
                var items = await load();
                if (popup != mine) return;
                // the badge said something is new but the list came back empty: the list route isn't giving real data, use the page
                body.Children.Clear();
                if (items.Count == 0)
                {
                    var none = Txt("Nothing here yet.", 12.5, "#8a8a8a"); none.Margin = new Thickness(16, 6, 16, 12);
                    body.Children.Add(none);
                }
                foreach (var it in items.Take(40)) body.Children.Add(FeedRow(it, messages, body, panel));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Store.Log("menu " + title, ex);
                if (popup != mine) return;
                // The list route is gone (404): open the in-app page instead of showing an error, and remember it.
                if (ex is ApiException gone && gone.Status == 404 && fallback != null)
                {
                    if (!messages) notifApiDead = true;
                    CloseMenu();
                    fallback();
                    return;
                }
                body.Children.Clear();
                string why = ex is ApiException ae ? " (" + ae.Status + ")" : "";
                var msg = new TextBlock { Text = "Couldn't load this list" + why + ". Try again in a moment, or use the link below to open it as a page.", Foreground = Ui.B("#9a9a9a"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 6, 16, 12) };
                body.Children.Add(msg);
            }
        }

        UIElement FeedRow(FeedItem it, bool messages, StackPanel body, StackPanel panel)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var av = ArtBox(it.AvatarUrl, 38, true, "t200x200", 76);
            av.VerticalAlignment = VerticalAlignment.Top;
            var tx = new StackPanel { Margin = new Thickness(10, 0, 8, 0) };
            tx.Children.Add(Txt(it.Title, 13, "#ffffff", true));
            if (it.Text.Length > 0)
                tx.Children.Add(new TextBlock { Text = it.Text, Foreground = Ui.B("#b0b0b0"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            var side = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            string ago = Ui.Ago(it.When);
            if (ago.Length > 0) side.Children.Add(Txt(ago, 11, "#808080"));
            if (it.Unread) side.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.B("#FF5500"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 2, 0) });
            Grid.SetColumn(av, 0); Grid.SetColumn(tx, 1); Grid.SetColumn(side, 2);
            g.Children.Add(av); g.Children.Add(tx); g.Children.Add(side);
            var row = new Border { Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Child = g };
            Action? go = null;
            if (messages && it.Id.Length > 0) go = () => ShowThread(it, body, panel);
            else if (!messages && it.TrackId != 0) go = () => { CloseMenu(); OpenTrack(new ScTrack { Id = it.TrackId, TitleRaw = "Track" }); };
            else if (!messages && it.UserId != 0) go = () => { CloseMenu(); OpenArtist(new ScUser { Id = it.UserId, Username = it.Title }); };
            else if (it.Url.StartsWith("https://")) go = () => { CloseMenu(); OpenUrl(it.Url); };
            if (go != null)
            {
                var act = go;
                row.Cursor = Cursors.Hand;
                Ui.Hover(row, "#2d2d2d");
                row.MouseLeftButtonUp += (_, _) => act();
            }
            return row;
        }

        /// <summary>A conversation, shown inside the same dropdown (back arrow returns to the list).</summary>
        async void ShowThread(FeedItem convo, StackPanel body, StackPanel panel)
        {
            var mine = popup;
            var listView = body.Children.Cast<UIElement>().ToList();
            body.Children.Clear();
            var back = new Border { Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 6, 4), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Child = Txt("←  " + (convo.Title.Length > 0 ? convo.Title : "Messages"), 13, "#ffffff", true) };
            Ui.Hover(back, "#2d2d2d");
            back.MouseLeftButtonUp += (_, _) => { body.Children.Clear(); foreach (var u in listView) body.Children.Add(u); };
            body.Children.Add(back);
            var wait = Txt("Loading…", 12.5, "#8a8a8a"); wait.Margin = new Thickness(16, 6, 16, 12);
            body.Children.Add(wait);
            try
            {
                var msgs = await api.GetMessagesAsync(convo.Id);
                if (popup != mine) return;
                body.Children.Remove(wait);
                if (msgs.Count == 0) { var none = Txt("No messages.", 12.5, "#8a8a8a"); none.Margin = new Thickness(16, 6, 16, 12); body.Children.Add(none); }
                foreach (var m in msgs.Skip(Math.Max(0, msgs.Count - 40)))
                {
                    bool me = m.Title == "You";
                    var bubble = new Border
                    {
                        CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 7, 12, 7), MaxWidth = 270, Margin = new Thickness(12, 2, 12, 2),
                        Background = me ? Ui.B("#FF5500") : Ui.B("#2d2d2d"), HorizontalAlignment = me ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                        Child = new TextBlock { Text = m.Text, Foreground = Brushes.White, FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
                    };
                    body.Children.Add(bubble);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Store.Log("thread " + convo.Id, ex);
                if (popup != mine) return;
                body.Children.Remove(wait);
                var msg = Txt("Couldn't load this conversation.", 12.5, "#9a9a9a"); msg.Margin = new Thickness(16, 6, 16, 12);
                body.Children.Add(msg);
            }
        }

        void ShowMoreMenu(FrameworkElement anchor)
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(MenuRow("⚙  Settings", () => { CloseMenu(); OpenSettings(); }));
            panel.Children.Add(MenuRow("☺  Your profile", () => { CloseMenu(); OpenOwnProfile(); }));
            panel.Children.Add(new Border { Height = 1, Background = Ui.B("#333333"), Margin = new Thickness(4) });
            panel.Children.Add(MenuRow("+  Add account", () => { CloseMenu(); StartSignIn(); }));
            if (api.Active != null) panel.Children.Add(MenuRow("↗  Open SoundCloud", () => { CloseMenu(); OpenUrl("https://soundcloud.com/"); }));
            if (api.Active != null) panel.Children.Add(MenuRow("🛡  Fix likes & sync", () => { CloseMenu(); FixSync(); }));
            if (api.Active != null) panel.Children.Add(MenuRow("🩺  Check sync", () => { CloseMenu(); CheckSync(); }));
            OpenMenu(anchor, panel);
        }

        // ---- tab strip ----
        void RefreshTabs()
        {
            tabStrip.Children.Clear();
            tabBorders.Clear();
            var mine = Mine();
            // One tab = no strip at all, so it reads like a normal app.
            tabScroll.Visibility = mine.Count > 1 ? Visibility.Visible : Visibility.Hidden;
            if (mine.Count <= 1) return;
            double avail = tabScroll.ActualWidth > 0 ? tabScroll.ActualWidth : 500;
            double w = Math.Max(84, Math.Min(150, avail / mine.Count - 6));
            foreach (var t in mine) { var tb = MakeTab(t, w); tabBorders[t] = tb; tabStrip.Children.Add(tb); }
        }

        /// <summary>A small symbol per tab kind so same-looking titles (two song names, two usernames...) can still be told apart at a glance.</summary>
        static string TabGlyph(string kind)
        {
            foreach (var n in Nav) if (n.Kind == kind) return n.Glyph;
            return kind switch
            {
                "track" => "♪",
                "user" => "☺",
                "playlist" => "☰",
                "usercomments" => "✎",
                "search" or "searchtracks" or "searchplaylists" or "searchalbums" or "searchusers" => "⚲",
                _ => "",
            };
        }

        Border MakeTab(PaneTab t, double width)
        {
            bool isActive = t == active;
            var fg = isActive ? Brushes.White : Ui.B("#aaaaaa");
            string glyph = TabGlyph(t.Kind);
            var tabFg = isActive ? Ui.B("#FF7A3D") : Ui.B("#787878");
            var icon = (t.Kind == "user" || t.Kind == "following")
                ? SilhouetteGlyph(11, 12, tabFg, new Thickness(0, 0, 6, 0))
                : new TextBlock
                {
                    Text = glyph, Foreground = tabFg, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, glyph.Length > 0 ? 6 : 0, 0), Visibility = glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                };
            var title = new TextBlock
            {
                Text = t.Title, Foreground = fg, FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            var close = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.Transparent,
                Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand,
                Visibility = isActive ? Visibility.Visible : Visibility.Collapsed,
                Child = new TextBlock { Text = "✕", Foreground = fg, FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(close, "#33FFFFFF");
            close.MouseLeftButtonDown += (_, e) => { e.Handled = true; CloseTab(t); };

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(close); dock.Children.Add(icon); dock.Children.Add(title);

            var b = new Border
            {
                Child = dock, Width = width, Height = 28, Padding = new Thickness(12, 0, 6, 0),
                Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(14),
                Background = isActive ? Ui.B("#2AFFFFFF") : Brushes.Transparent, ToolTip = t.Title, Cursor = Cursors.Hand,
            };
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            if (!isActive)
            {
                b.MouseEnter += (_, _) => { b.Background = Ui.B("#15FFFFFF"); close.Visibility = Visibility.Visible; };
                b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; close.Visibility = Visibility.Collapsed; };
            }
            b.Tag = t;
            b.MouseLeftButtonDown += (_, e) => { if (e.Handled) return; BeginTabDrag(t, e); ActivateTab(t); };
            b.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(t); };
            return b;
        }

        // ---- dropdown menus ----
        void CloseMenu() { if (popup != null) { popup.IsOpen = false; popup = null; } }

        Border MenuRow(string text, Action click)
        {
            var b = new Border
            {
                Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(12),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, Foreground = Ui.B("#eeeeee"), FontSize = 13 },
            };
            Ui.Hover(b, "#2d2d2d");
            b.MouseLeftButtonUp += (_, _) => click();
            return b;
        }

        void OpenMenu(FrameworkElement target, StackPanel panel, PlacementMode mode = PlacementMode.Bottom)
        {
            CloseMenu();
            popup = new Popup
            {
                PlacementTarget = target, Placement = mode, StaysOpen = false, AllowsTransparency = true,
                Child = new Border
                {
                    Background = Ui.B("#F01C1C1C"), BorderBrush = Ui.B("#3a3a3a"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(18), MinWidth = 220, Child = panel,
                },
            };
            popup.IsOpen = true;
        }

        void ShowAccountMenu()
        {
            if (cfg.Accounts.Count == 0) { StartSignIn(); return; }
            var panel = new StackPanel { Margin = new Thickness(4) };
            foreach (var a in cfg.Accounts)
            {
                var id = a.Id;
                panel.Children.Add(MenuRow((id == cfg.Current ? "●  " : "○  ") + a.Name, () => { CloseMenu(); SwitchAccount(id); }));
            }
            if (cfg.Accounts.Count > 0) panel.Children.Add(new Border { Height = 1, Background = Ui.B("#333333"), Margin = new Thickness(4) });
            panel.Children.Add(MenuRow("☺  Your profile", () => { CloseMenu(); OpenOwnProfile(); }));
            panel.Children.Add(MenuRow("+  Add account", () => { CloseMenu(); StartSignIn(); }));
            panel.Children.Add(MenuRow("↗  Open SoundCloud", () => { CloseMenu(); OpenUrl("https://soundcloud.com/"); }));
            panel.Children.Add(MenuRow("🛡  Fix likes & sync", () => { CloseMenu(); FixSync(); }));
            if (api.Active != null) panel.Children.Add(MenuRow("🩺  Check sync", () => { CloseMenu(); CheckSync(); }));
            panel.Children.Add(MenuRow("⚙  Manage accounts…", () => { CloseMenu(); OpenSettings(); }));
            OpenMenu(acctBtn, panel);
        }

        void ShowNewTabMenu()
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(MenuRow("🔍  Search", () => { CloseMenu(); NewSearchTab(); }));
            foreach (var n in Nav)
            {
                if (n.Kind == "#") { panel.Children.Add(MenuSep()); continue; }
                var nn = n;
                panel.Children.Add(MenuRow(n.Glyph + "  " + n.Title, () => { CloseMenu(); if (nn.Kind == "profile") OpenOwnProfile(); else OpenKind(nn.Kind, nn.Title); }));
            }
            OpenMenu(plusBtn, panel);
        }

        static Border MenuSep() => new Border { Height = 1, Background = Ui.B("#333333"), Margin = new Thickness(4) };

        void ShowScopeMenu()
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            void Add(string label, string scope, string hint)
            {
                panel.Children.Add(MenuRow((searchScope == scope ? "●  " : "○  ") + label, () =>
                {
                    CloseMenu();
                    searchScope = scope;
                    scopeChip.Text = (scope == "all" ? "All" : label) + " ▾";
                    searchHint.Text = hint;
                    if (searchBox.Text.Trim().Length > 0) SearchFromBox();
                }));
            }
            Add("Everything", "all", "Search SoundCloud");
            Add("Tracks", "tracks", "Search tracks");
            Add("Playlists", "playlists", "Search playlists");
            Add("Albums", "albums", "Search albums");
            Add("People", "people", "Search people");
            OpenMenu(scopeChip, panel);
        }

        // ================= body =================
        Grid BuildBody()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var side = BuildSidebar();
            Grid.SetColumn(side, 0);

            list.Background = Brushes.Transparent;
            list.BorderThickness = new Thickness(0);
            list.Margin = new Thickness(6, 4, 10, 4);
            list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            list.ItemTemplate = (DataTemplate)XamlReader.Parse(ItemTemplateXaml);
            list.ItemContainerStyle = (Style)XamlReader.Parse(ItemStyleXaml);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.MouseDoubleClick += (_, _) => ActivateSelected();
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ActivateSelected(); e.Handled = true; } };
            list.MouseRightButtonUp += (_, e) => ListContextMenu(e);
            list.PreviewMouseLeftButtonUp += OnListClick; // title / cover / artist open the song or profile; heart and repost work in place
            // endless scroll: load the next page when the list is scrolled near its end
            list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((sender, e) => OnScrolled(e)));
            cardScroll.ScrollChanged += (_, e) => OnScrolled(e);

            message.Foreground = Ui.B("#808080"); message.FontSize = 13; message.TextAlignment = TextAlignment.Center;
            message.TextWrapping = TextWrapping.Wrap; message.MaxWidth = 420;
            message.HorizontalAlignment = HorizontalAlignment.Center; message.VerticalAlignment = VerticalAlignment.Center;
            message.IsHitTestVisible = false;

            Grid.SetColumn(list, 1); Grid.SetColumn(cardScroll, 1); Grid.SetColumn(detailScroll, 1); Grid.SetColumn(message, 1);
            g.Children.Add(side); g.Children.Add(list); g.Children.Add(cardScroll); g.Children.Add(detailScroll); g.Children.Add(message);
            return g;
        }

        bool loadingMore;

        void OnScrolled(ScrollChangedEventArgs e)
        {
            if (e.VerticalChange <= 0 || active == null || active.Next == null) return;
            if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2 && !loadingMore)
            {
                loadingMore = true;
                var t = active;
                var ui = TaskScheduler.FromCurrentSynchronizationContext();
                LoadAsync(t, true).ContinueWith(done => { loadingMore = false; }, ui);
            }
        }

        // ================= SoundCloud-style card pages =================
        // Pages that are collections of things (not plain song lists) show as artwork cards:
        // sections with a title become a horizontal shelf, a flat collection becomes a wrapping grid.
        static readonly HashSet<string> CardKinds = new HashSet<string> { "home", "stations", "playlists", "albums", "following", "search", "searchtracks", "searchplaylists", "searchalbums", "searchusers", "feed", "likes", "reposts", "recent", "usercomments" };
        readonly ScrollViewer cardScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        readonly List<(ItemVM It, TextBlock Title)> cardTitles = new List<(ItemVM, TextBlock)>();
        readonly SemaphoreSlim artGate = new SemaphoreSlim(6);
        PaneTab? cardTab;

        void RenderBody(PaneTab? t)
        {
            bool detail = t != null && ((t.Kind == "track" && t.DTrack != null) || (t.Kind == "user" && t.DUser != null) || (t.Kind == "playlist" && t.DPlaylist != null));
            // a search page keeps its filter column even while loading or when nothing was found, so the scope can always be changed
            bool cards = !detail && t != null && CardKinds.Contains(t.Kind) && (t.Rows.Count > 0 || (SearchKinds.Contains(t.Kind) && t.Param.Length > 0) || (t.Kind == "usercomments" && t.Loaded));
            list.Visibility = (cards || detail) ? Visibility.Collapsed : Visibility.Visible;
            cardScroll.Visibility = cards ? Visibility.Visible : Visibility.Collapsed;
            detailScroll.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
            if (!detail) ClearDetail();
            if (detail)
            {
                cardTab = null; cardScroll.Content = null;
                double keepD = ReferenceEquals(detailTab, t) ? detailScroll.VerticalOffset : 0;
                detailTab = t;
                cardTitles.Clear(); pageRefreshers.Clear(); pageWaves.Clear();
                try { detailScroll.Content = BuildDetail(t!); }
                catch (Exception ex)
                {
                    Store.Log("detail page", ex);
                    detailScroll.Content = new TextBlock { Text = "Couldn't show this page: " + ex.Message, Foreground = Ui.B("#ff8a80"), Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
                }
                detailScroll.UpdateLayout();
                detailScroll.ScrollToVerticalOffset(keepD);
                return;
            }
            if (!cards) { cardTab = null; cardTitles.Clear(); pageRefreshers.Clear(); pageWaves.Clear(); cardScroll.Content = null; return; }
            double keep = ReferenceEquals(cardTab, t) ? cardScroll.VerticalOffset : 0;
            cardTab = t;
            cardScroll.Content = BuildCards(t!);
            cardScroll.UpdateLayout();
            cardScroll.ScrollToVerticalOffset(keep);
        }

        UIElement BuildCards(PaneTab t)
        {
            cardTitles.Clear(); pageRefreshers.Clear(); pageWaves.Clear();
            if (t.Kind == "feed") return BuildFeedPage(t);
            if (t.Kind == "recent") return BuildHistoryPage(t);
            if (t.Kind == "usercomments") return BuildCommentsPage(t);
            if (t.Kind == "likes") return BuildLikesPage(t);
            if (t.Kind == "reposts") return BuildRepostsPage(t);
            if (SearchKinds.Contains(t.Kind)) return BuildSearchPage(t);
            var page = new StackPanel { Margin = new Thickness(24, 14, 12, 24) };
            string head = "";
            var group = new List<ItemVM>();
            void Flush()
            {
                if (group.Count > 0) page.Children.Add(BuildShelf(head, group.ToList()));
                group.Clear(); head = "";
            }
            foreach (var r in t.Rows)
            {
                if (r.Kind == "header") { Flush(); head = r.Title; }
                else if (r.Kind == "more") { Flush(); page.Children.Add(BuildMoreButton(t)); }
                else group.Add(r);
            }
            Flush();
            return page;
        }

        UIElement BuildMoreButton(PaneTab t)
        {
            var b = new Border
            {
                Height = 34, Width = 140, CornerRadius = new CornerRadius(17), Background = Ui.B("#1FFFFFFF"), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock { Text = "Load more", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(b, "#33FFFFFF");
            b.MouseLeftButtonUp += async (_, _) => { try { await LoadAsync(t, true); } catch (Exception ex) { Store.Log("load more", ex); } };
            return b;
        }

        UIElement BuildShelf(string head, List<ItemVM> items)
        {
            var sec = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
            if (head.Length > 0)
                sec.Children.Add(new TextBlock { Text = head, Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14), TextTrimming = TextTrimming.CharacterEllipsis });

            if (head.Length == 0)
            {
                var wrap = new WrapPanel();
                foreach (var it in items) wrap.Children.Add(BuildCard(it));
                sec.Children.Add(wrap);
                return sec;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var it in items) row.Children.Add(BuildCard(it));
            var sv = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = row };
            // the wheel scrolls the page, not the shelf (Shift+wheel scrolls the shelf)
            sv.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta); return; }
                var ev = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = s };
                (cardScroll.Visibility == Visibility.Visible ? cardScroll : detailScroll).RaiseEvent(ev);
            };

            Border Chevron(string glyph, HorizontalAlignment side, int dir)
            {
                var c = new Border
                {
                    Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Ui.B("#EE2A2A2A"), BorderBrush = Ui.B("#44FFFFFF"), BorderThickness = new Thickness(1),
                    HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 62, 6, 0), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed,
                    Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -3, 0, 0) },
                };
                Ui.Hover(c, "#FF3A3A3A");
                c.MouseLeftButtonUp += (_, e) => { e.Handled = true; sv.ScrollToHorizontalOffset(sv.HorizontalOffset + dir * sv.ViewportWidth * 0.8); };
                return c;
            }
            var left = Chevron("‹", HorizontalAlignment.Left, -1);
            var right = Chevron("›", HorizontalAlignment.Right, 1);
            sv.ScrollChanged += (_, _) =>
            {
                left.Visibility = sv.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
                right.Visibility = sv.ScrollableWidth - sv.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
            };
            var g = new Grid();
            g.Children.Add(sv); g.Children.Add(left); g.Children.Add(right);
            sec.Children.Add(g);
            return sec;
        }

        FrameworkElement BuildCard(ItemVM it)
        {
            const double S = 160;
            bool round = it.Kind == "user" || (it.Kind == "station" && it.User != null);
            var radius = new CornerRadius(round ? S / 2 : 8);

            var inner = new Border { CornerRadius = radius, Background = Ui.B("#262626") };
            var dim = new Border { CornerRadius = radius, Background = Ui.B("#66000000"), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            var artGrid = new Grid { Width = S, Height = S };
            artGrid.Children.Add(inner); artGrid.Children.Add(dim);

            Border? playBtn = null;
            if (it.Kind != "user")
            {
                playBtn = new Border
                {
                    Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Ui.B("#FFFF5500"), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Play",
                    Child = new TextBlock { Text = "\uE768", FontFamily = Ui.Icons, FontSize = 17, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) },
                };
                Ui.Hover(playBtn, "#FFFF7A3D");
                var pb = playBtn;
                pb.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (it.Kind == "track" && it.Track != null && current != null && current.Id == it.Track.Id && (player.HasTrack || player.IsLoading)) TogglePlay();
                    else PlayItem(it);
                };
                artGrid.Children.Add(pb);
            }

            var title = new TextBlock
            {
                Text = it.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = it.TitleBrush, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 9, 0, 0), ToolTip = it.Title, TextAlignment = round ? TextAlignment.Center : TextAlignment.Left,
            };
            var sub = new TextBlock
            {
                Text = it.Artist, FontSize = 12, Foreground = Ui.B("#8A8A8A"), TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0), TextAlignment = round ? TextAlignment.Center : TextAlignment.Left,
            };
            cardTitles.Add((it, title));
            if (it.Kind == "track" && it.Track?.User != null)
            {
                var au = it.Track.User;
                sub.Cursor = Cursors.Hand; sub.HorizontalAlignment = HorizontalAlignment.Left;
                sub.MouseEnter += (_, _) => sub.TextDecorations = TextDecorations.Underline;
                sub.MouseLeave += (_, _) => sub.TextDecorations = null;
                sub.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(au); };
            }

            var card = new StackPanel { Width = S, Margin = new Thickness(0, 0, 18, 18), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            card.Children.Add(artGrid); card.Children.Add(title);

            // people: followers line, then a Follow / Following button that only shows while the mouse is over the card
            Border? followBtn = null;
            if (it.Kind == "user" && it.User != null)
            {
                var u = it.User;
                if (u.Followers > 0)
                {
                    var f = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                    f.Children.Add(new TextBlock { Text = "\uE77B", FontFamily = Ui.Icons, FontSize = 10, Foreground = Ui.B("#8A8A8A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                    f.Children.Add(new TextBlock { Text = u.Followers.ToString("N0") + (u.Followers == 1 ? " follower" : " followers"), Foreground = Ui.B("#8A8A8A"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
                    card.Children.Add(f);
                }
                var flabel = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                followBtn = new Border
                {
                    Height = 30, MinWidth = 88, Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 0), Child = flabel,
                    Visibility = Visibility.Hidden, // Hidden (not Collapsed): the card keeps its height, nothing jumps on hover
                };
                var fb = followBtn;
                void PaintFollow()
                {
                    bool on = IsFollowed(u.Id);
                    flabel.Text = on ? "Following" : "Follow";
                    flabel.Foreground = on ? Brushes.White : Brushes.Black;
                    fb.Background = Ui.B(on ? "#FF2E2E2E" : "#FFFFFFFF");
                }
                PaintFollow();
                if (!followState.ContainsKey(u.Id)) _ = LoadFollowStateAsync(u.Id, PaintFollow);
                fb.MouseEnter += (_, _) => { if (IsFollowed(u.Id)) fb.Background = Ui.B("#FF3C3C3C"); };
                fb.MouseLeave += (_, _) => PaintFollow();
                fb.MouseLeftButtonUp += async (_, e) =>
                {
                    e.Handled = true;
                    await SetFollowAsync(u, !IsFollowed(u.Id));
                    PaintFollow();
                };
                card.Children.Add(fb);
                pageRefreshers.Add(PaintFollow);
                title.Margin = new Thickness(0, 12, 0, 0);
                title.FontWeight = FontWeights.Bold;
            }
            else card.Children.Add(sub);

            bool hot = false;
            // a track that is playing keeps its pause button and dim layer, so the card shows what is playing without hovering
            void PaintCard()
            {
                bool on = it.Kind == "track" && it.Track != null && current != null && current.Id == it.Track.Id && player.IsPlaying;
                if (playBtn != null)
                {
                    var g = (TextBlock)playBtn.Child;
                    g.Text = on ? "\uE769" : "\uE768";
                    g.Margin = new Thickness(on ? 0 : 2, 0, 0, 0);
                    playBtn.ToolTip = on ? "Pause" : "Play";
                }
                var v = (hot || on) ? Visibility.Visible : Visibility.Collapsed;
                dim.Visibility = v;
                if (playBtn != null) playBtn.Visibility = v;
                if (followBtn != null) followBtn.Visibility = hot ? Visibility.Visible : Visibility.Hidden;
                title.TextDecorations = hot ? TextDecorations.Underline : null;
            }
            PaintCard();
            pageRefreshers.Add(PaintCard);
            card.MouseEnter += (_, _) => { hot = true; PaintCard(); };
            card.MouseLeave += (_, _) => { hot = false; PaintCard(); };
            card.MouseLeftButtonUp += (_, _) => OpenCard(it);
            card.MouseRightButtonUp += (_, e) => { if (ShowItemMenu(it, card, PlacementMode.MousePoint)) e.Handled = true; };

            if (it.ArtUrl.Length > 0) _ = LoadCardArtAsync(it.ArtUrl, inner);
            return card;
        }

        async Task LoadCardArtAsync(string url, Border target)
        {
            await artGate.WaitAsync();
            try
            {
                var img = await ArtCache.GetAsync(ArtCache.Size(url, "t300x300"), 240);
                if (img != null) target.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }
            catch { }
            finally { artGate.Release(); }
        }

        Border BuildSidebar()
        {
            var panel = new StackPanel { Margin = new Thickness(8, 6, 4, 6) };
            bool firstHeader = true;
            foreach (var n in Nav)
            {
                if (n.Kind == "#")
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = n.Title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("#6a6a6a"),
                        Margin = new Thickness(10, firstHeader ? 4 : 14, 0, 3),
                    });
                    firstHeader = false;
                    continue;
                }
                var nn = n;
                var glyph = (n.Kind == "following" || n.Kind == "profile")
                    ? SilhouetteGlyph(14, 22, Ui.B("#aaaaaa"), new Thickness(0))
                    : new TextBlock { Text = n.Glyph, Width = 22, FontSize = 14, Foreground = Ui.B("#aaaaaa"), VerticalAlignment = VerticalAlignment.Center };
                var label = new TextBlock { Text = n.Title, FontSize = 13, Foreground = Ui.B("#aaaaaa"), VerticalAlignment = VerticalAlignment.Center };
                var row = new Border
                {
                    Height = 32, Padding = new Thickness(10, 0, 8, 0), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 1, 0, 1),
                    Background = Brushes.Transparent, Cursor = Cursors.Hand,
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, label } },
                };
                row.MouseEnter += (_, _) => { if (row.Tag as string != "on") row.Background = Ui.B("#15FFFFFF"); };
                row.MouseLeave += (_, _) => { if (row.Tag as string != "on") row.Background = Brushes.Transparent; };
                row.MouseLeftButtonUp += (_, _) => { if (nn.Kind == "profile") OpenOwnProfile(); else OpenKind(nn.Kind, nn.Title); };
                navRows[n.Kind] = row;
                panel.Children.Add(row);
            }

            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = panel };
            return new Border { Width = 168, Background = Ui.B("#101010"), Child = sv };
        }

        void RefreshAutoplayText() => autoplayText.Text = "∞  Autoplay: " + (cfg.Settings.Autoplay ? "On" : "Off");

        void RefreshSidebar()
        {
            foreach (var kv in navRows)
            {
                bool on = kv.Key == "profile"
                    ? active != null && active.Kind == "user" && active.Param == (api.Active?.UserId ?? 0).ToString()
                    : active != null && active.Kind == kv.Key;
                kv.Value.Tag = on ? "on" : "";
                kv.Value.Background = on ? Ui.B("#26FFFFFF") : Brushes.Transparent;
                var sp = (StackPanel)kv.Value.Child;
                foreach (TextBlock tb in sp.Children) tb.Foreground = on ? Brushes.White : Ui.B("#aaaaaa");
                if (on && sp.Children.Count > 0) ((TextBlock)sp.Children[0]).Foreground = Ui.B("#FF5500");
            }
        }

        void OpenOwnProfile()
        {
            var a = api.Active;
            if (a == null) { StartSignIn(); return; }
            OpenArtist(new ScUser { Id = a.UserId, Username = a.Name });
        }

        /// <summary>Sidebar click: jump to the open tab of that page (reload it if it's already showing), or open it.</summary>
        void OpenKind(string kind, string title)
        {
            if (cfg.Current.Length == 0) { StartSignIn(); return; }
            var ex = Mine().FirstOrDefault(t => t.Kind == kind);
            if (ex == null) { AddTab(kind, title, ""); return; }
            if (ex == active) { ex.Loaded = false; _ = LoadAsync(ex, false); return; }
            ActivateTab(ex);
            if (kind == "queue") RefreshQueueTab();
        }

        void ShowMessage(PaneTab? t)
        {
            bool empty = t == null || (t.Rows.Count == 0 && t.DTrack == null && t.DUser == null && t.DPlaylist == null && !(t.Kind == "usercomments" && t.Comments.Count > 0));
            message.Text = t == null ? (api.Active == null ? "Sign in to SoundCloud with the button at the top left." : "") : (empty ? t.Message : "");
            message.Visibility = message.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderBody(t);
        }

        // ================= player bar =================
        Border BuildPlayerBar()
        {
            var g = new Grid { Height = 96 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // left: artwork, title (links to SoundCloud), artist, attribution
            artInner.CornerRadius = new CornerRadius(8); artInner.Background = artBrush;
            var art = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(8), Background = Ui.B("#2A2A2A"), Margin = new Thickness(16, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Child = artInner, Cursor = Cursors.Hand, ToolTip = "Open this song" };
            art.MouseLeftButtonUp += (_, _) => OpenTrack(current);
            art.Tag = (Action)(() => OpenTrack(current));

            titleText.FontSize = 13; titleText.FontWeight = FontWeights.SemiBold; titleText.Foreground = Brushes.White; titleText.TextTrimming = TextTrimming.CharacterEllipsis; titleText.Cursor = Cursors.Hand;
            titleText.MouseEnter += (_, _) => titleText.TextDecorations = TextDecorations.Underline;
            titleText.MouseLeave += (_, _) => titleText.TextDecorations = null;
            titleText.ToolTip = "Open this song"; titleText.MouseLeftButtonUp += (_, _) => OpenTrack(current);
            titleText.Tag = (Action)(() => OpenTrack(current));
            artistText.FontSize = 12; artistText.Foreground = Ui.B("#9a9a9a"); artistText.TextTrimming = TextTrimming.CharacterEllipsis; artistText.Cursor = Cursors.Hand;
            artistText.MouseEnter += (_, _) => artistText.TextDecorations = TextDecorations.Underline;
            artistText.MouseLeave += (_, _) => artistText.TextDecorations = null;
            artistText.ToolTip = "Open artist"; artistText.MouseLeftButtonUp += (_, _) => OpenArtist(current?.User);

            // Attribution: official logo if you dropped soundcloud-logo.png next to the exe, otherwise text.
            attribText.Text = "Powered by SoundCloud"; attribText.FontSize = 10; attribText.Foreground = Ui.B("#707070"); attribText.Cursor = Cursors.Hand;
            attribText.MouseLeftButtonUp += (_, _) => OpenUrl(string.IsNullOrEmpty(current?.PermalinkUrl) ? "https://soundcloud.com" : current!.PermalinkUrl);
            var logoPath = Path.Combine(AppContext.BaseDirectory, "soundcloud-logo.png");
            if (File.Exists(logoPath))
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(logoPath); bi.DecodePixelHeight = 20; bi.EndInit(); bi.Freeze();
                    attribLogo.Source = bi; attribLogo.Height = 14; attribLogo.Stretch = Stretch.Uniform; attribLogo.HorizontalAlignment = HorizontalAlignment.Left; attribLogo.Cursor = Cursors.Hand;
                    attribLogo.MouseLeftButtonUp += (_, _) => OpenUrl(string.IsNullOrEmpty(current?.PermalinkUrl) ? "https://soundcloud.com" : current!.PermalinkUrl);
                }
                catch { }
            }
            var attrib = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
            if (attribLogo.Source != null) attrib.Children.Add(attribLogo); else attrib.Children.Add(attribText);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(titleText); texts.Children.Add(artistText); texts.Children.Add(attrib);
            var leftGrid = new Grid();
            leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(art, 0); Grid.SetColumn(texts, 1);
            leftGrid.Children.Add(art); leftGrid.Children.Add(texts);

            // center: transport + seek
            toast.FontSize = 11; toast.Foreground = Ui.B("#ff8a80"); toast.HorizontalAlignment = HorizontalAlignment.Center;
            toast.TextTrimming = TextTrimming.CharacterEllipsis; toast.Visibility = Visibility.Collapsed; toast.Margin = new Thickness(0, 0, 0, 2);

            shuffleGlyph.Text = "\uE8B1";
            var shuffle = Ui.RoundBtn(shuffleGlyph, 30, Brushes.Transparent, Ui.B("#8a8a8a"), "#22FFFFFF", ToggleShuffle);
            shuffle.ToolTip = "Shuffle";
            repeatGlyph.Text = "\uE8EE";
            var repeat = Ui.RoundBtn(repeatGlyph, 30, Brushes.Transparent, Ui.B("#8a8a8a"), "#22FFFFFF", ToggleRepeat);
            repeat.ToolTip = "Repeat (off / all / one)";
            var prev = Ui.RoundBtn(new TextBlock { Text = "\uE892" }, 34, Brushes.Transparent, Brushes.White, "#22FFFFFF", Prev);
            playGlyph.Text = "\uE768";
            var play = Ui.RoundBtn(playGlyph, 42, Brushes.White, Brushes.Black, "#DDDDDD", TogglePlay);
            play.Margin = new Thickness(10, 0, 10, 0);
            var next = Ui.RoundBtn(new TextBlock { Text = "\uE893" }, 34, Brushes.Transparent, Brushes.White, "#22FFFFFF", () => Next(true));
            var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            shuffle.Margin = new Thickness(0, 0, 4, 0);
            repeat.Margin = new Thickness(0, 0, 8, 0);
            transport.Children.Add(shuffle); transport.Children.Add(repeat); transport.Children.Add(prev); transport.Children.Add(play); transport.Children.Add(next);

            posText.FontSize = 11; posText.Foreground = Ui.B("#8a8a8a"); posText.Text = "0:00"; posText.Width = 44; posText.TextAlignment = TextAlignment.Right; posText.VerticalAlignment = VerticalAlignment.Center;
            durText.FontSize = 11; durText.Foreground = Ui.B("#8a8a8a"); durText.Text = "0:00"; durText.Width = 44; durText.VerticalAlignment = VerticalAlignment.Center; durText.Margin = new Thickness(6, 0, 0, 0);
            seek = new MiniSlider(1000, 0) { Margin = new Thickness(8, 0, 4, 0) };
            seek.Changed += (v, commit) =>
            {
                var d = player.Duration.TotalSeconds;
                if (d <= 0) return;
                var t = TimeSpan.FromSeconds(d * v / 1000.0);
                posText.Text = Ui.Fmt(t);
                if (commit) { player.Seek(t); UpdatePresence(); }
            };
            var seekRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(posText, 0); Grid.SetColumn(seek, 1); Grid.SetColumn(durText, 2);
            seekRow.Children.Add(posText); seekRow.Children.Add(seek); seekRow.Children.Add(durText);

            var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            center.Children.Add(toast); center.Children.Add(transport); center.Children.Add(seekRow);

            // right: like + volume
            heartGlyph.Text = "\uEB51";
            var heart = Ui.RoundBtn(heartGlyph, 34, Brushes.Transparent, Ui.B("#cccccc"), "#22FFFFFF", () => ToggleLike());
            heart.ToolTip = "Like";
            repostGlyph.Text = "\uE8EE";
            var repostBtn = Ui.RoundBtn(repostGlyph, 34, Brushes.Transparent, Ui.B("#cccccc"), "#22FFFFFF", () => ToggleRepostOf(current));
            repostBtn.ToolTip = "Repost";
            volGlyph.FontFamily = Ui.Icons; volGlyph.FontSize = 14; volGlyph.Foreground = Ui.B("#cccccc"); volGlyph.Text = "\uE767";
            volGlyph.VerticalAlignment = VerticalAlignment.Center; volGlyph.Margin = new Thickness(10, 0, 4, 0);
            volSlider = new MiniSlider(100, cfg.Settings.Volume) { Width = 90, Margin = new Thickness(0, 0, 14, 0) };
            volSlider.Changed += (v, commit) => SetVolume(v, commit);
            volSlider.MouseWheel += (_, e) => volSlider.Wheel(e.Delta > 0 ? 5 : -5);
            var followBtn = Ui.RoundBtn(artistFollowGlyph, 34, Brushes.Transparent, Ui.B("#cccccc"), "#22FFFFFF", ToggleFollowCurrentArtist);
            followBtn.ToolTip = "Follow this artist";
            Border nextUpBtn = null!;
            nextUpBtn = Ui.RoundBtn(new TextBlock { Text = "\uE8FD" }, 34, Brushes.Transparent, Ui.B("#cccccc"), "#22FFFFFF", () => ShowNextUpPanel(nextUpBtn));
            nextUpBtn.ToolTip = "Next up";
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(heart); right.Children.Add(repostBtn); right.Children.Add(followBtn); right.Children.Add(volGlyph); right.Children.Add(volSlider); right.Children.Add(nextUpBtn);

            Grid.SetColumn(leftGrid, 0); Grid.SetColumn(center, 1); Grid.SetColumn(right, 2);
            g.Children.Add(leftGrid); g.Children.Add(center); g.Children.Add(right);

            barBorder.Background = Ui.B("#181818");
            barBorder.BorderBrush = Ui.B("#222222");
            barBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            barBorder.Child = g;
            return barBorder;
        }

        // ================= tabs (management) =================
        void RestoreTabs()
        {
            var saved = Store.Load<SavedState>("tabs.json");
            saved.Tabs ??= new List<SavedTab>();
            saved.Tabs.RemoveAll(x => x == null);
            foreach (var s in saved.Tabs.Where(s => cfg.Accounts.Any(a => a.Id == s.Acct)))
                tabs.Add(s.Kind == "trending" ? new PaneTab { Id = nextId++, Acct = s.Acct, Kind = "home", Title = "Home" } : new PaneTab { Id = nextId++, Acct = s.Acct, Kind = s.Kind, Title = s.Title, Param = s.Param });
            EnsureDefaultTabs(cfg.Current);
            PaneTab? target = (saved.Active >= 0 && saved.Active < tabs.Count && tabs[saved.Active].Acct == cfg.Current) ? tabs[saved.Active] : null;
            target ??= tabs.FirstOrDefault(t => t.Acct == cfg.Current);
            if (target != null) SetActive(target, load: false);
            RefreshTabs();
        }

        void EnsureDefaultTabs(string acct)
        {
            if (acct.Length == 0 || tabs.Any(t => t.Acct == acct)) return;
            tabs.Add(new PaneTab { Id = nextId++, Acct = acct, Kind = "home", Title = "Home" });
        }

        /// <summary>
        /// A normal click on a profile / song / playlist link: the page you are on is replaced (like a browser's same-tab link),
        /// and Back (mouse side button, Alt+Left) brings it back. A middle click goes through AddTab instead (new background tab).
        /// </summary>
        void NavigateHere(string kind, string title, string param, bool autoPlay = false)
        {
            if (cfg.Current.Length == 0) { OpenSettings(); return; }
            var t = active;
            if (bgOpen || t == null || t.Acct != cfg.Current || !tabs.Contains(t)) { AddTab(kind, title, param, true, autoPlay); return; }
            if (t.Kind == kind && t.Param == param && param.Length > 0) return; // already on that page
            t.Back.Add(new TabEntry { Kind = t.Kind, Title = t.Title, Param = t.Param });
            if (t.Back.Count > 40) t.Back.RemoveAt(0);
            t.Fwd.Clear();
            ShowPageIn(t, kind, title, param, autoPlay);
        }

        void ShowPageIn(PaneTab t, string kind, string title, string param, bool autoPlay)
        {
            t.Kind = kind; t.Title = title; t.Param = param; t.AutoPlay = autoPlay;
            t.Loaded = false; t.Next = null; t.Filter = "";
            t.Rows.Clear(); t.DTrack = null; t.DUser = null; t.DPlaylist = null; t.Side = null; t.Comments.Clear(); t.PendingSub = null;
            t.Subs.Clear(); t.Sub = "all"; t.Facets = new Dictionary<string, long>();
            t.IsStation = false; t.StationSeed = null; t.StationArtist = null;
            t.Message = "Loading…";
            detailTab = null; cardTab = null; // so the new page starts at the top
            if (t == active) { ShowMessage(t); RefreshSidebar(); }
            RefreshTabs(); QueueSave();
            _ = LoadAsync(t, false);
        }

        bool StepInTab(bool back)
        {
            var t = active;
            if (t == null || !tabs.Contains(t)) return false;
            var from = back ? t.Back : t.Fwd;
            var to = back ? t.Fwd : t.Back;
            if (from.Count == 0) return false;
            var e = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            to.Add(new TabEntry { Kind = t.Kind, Title = t.Title, Param = t.Param });
            ShowPageIn(t, e.Kind, e.Title, e.Param, false);
            return true;
        }

        PaneTab? AddTab(string kind, string title, string param, bool activate = true, bool autoPlay = false)
        {
            if (cfg.Current.Length == 0) { OpenSettings(); return null; }
            if (bgOpen) { activate = false; autoPlay = false; }
            var t = new PaneTab { Id = nextId++, Acct = cfg.Current, Kind = kind, Title = title, Param = param, AutoPlay = autoPlay };
            tabs.Add(t);
            if (activate) ActivateTab(t);
            else if (bgOpen) { RefreshTabs(); QueueSave(); ShowToast("Opened in a new tab", true); }
            return t;
        }

        void NewSearchTab()
        {
            var t = AddTab(SearchKind(), "New search", "");
            searchBox.Text = "";
            searchBox.Focus();
            if (t != null) ShowMessage(t);
        }

        void SearchFromBox()
        {
            string q = searchBox.Text.Trim();
            if (q.Length == 0) return;
            if (cfg.Current.Length == 0) { OpenSettings(); return; }
            if (active != null && SearchKinds.Contains(active.Kind))
            {
                active.Kind = SearchKind();
                active.Param = q; active.Title = Cut(q, 24); active.Loaded = false; active.Facets = new Dictionary<string, long>();
                RefreshTabs(); QueueSave();
                _ = LoadAsync(active, false);
            }
            else AddTab(SearchKind(), Cut(q, 24), q);
        }

        string SearchKind() => searchScope switch { "tracks" => "searchtracks", "playlists" => "searchplaylists", "albums" => "searchalbums", "people" => "searchusers", _ => "search" };

        static string ScopeOf(string kind) => kind switch { "searchtracks" => "tracks", "searchplaylists" => "playlists", "searchalbums" => "albums", "searchusers" => "people", _ => "all" };

        static string Cut(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        void ActivateTab(PaneTab t)
        {
            if (bgOpen) { ShowToast("Already open in a tab", true); return; } // middle click on something that has a tab: stay where you are
            SetActive(t, load: true);
        }

        // Back / forward through the tabs you visited (mouse side buttons, Alt+Left / Alt+Right).
        readonly List<PaneTab> navBack = new List<PaneTab>();
        readonly List<PaneTab> navFwd = new List<PaneTab>();
        bool navMoving;

        bool Navigable(PaneTab t) => tabs.Contains(t);

        void GoBack() { if (!StepInTab(true)) GoHistory(navBack, navFwd); }
        void GoForward() { if (!StepInTab(false)) GoHistory(navFwd, navBack); }

        void GoHistory(List<PaneTab> from, List<PaneTab> to)
        {
            while (from.Count > 0 && (!Navigable(from[from.Count - 1]) || from[from.Count - 1] == active)) from.RemoveAt(from.Count - 1);
            if (from.Count == 0) return;
            var target = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            if (active != null && Navigable(active)) { to.Add(active); if (to.Count > 60) to.RemoveAt(0); }
            navMoving = true;
            try { ActivateTab(target); }
            finally { navMoving = false; }
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);
            if (AutoScrollMouseDown(e)) return;
            if (e.ChangedButton == MouseButton.Middle && TryMiddleTarget(e, out _)) { e.Handled = true; return; } // no auto-scroll cursor over a link
            if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; GoBack(); }
            else if (e.ChangedButton == MouseButton.XButton2) { e.Handled = true; GoForward(); }
        }

        void SetActive(PaneTab t, bool load)
        {
            if (!navMoving && active != null && active != t && Navigable(active))
            {
                navBack.Add(active);
                if (navBack.Count > 60) navBack.RemoveAt(0);
                navFwd.Clear();
            }
            if (t.Acct != cfg.Current) { cfg.Current = t.Acct; Store.Save("config.json", cfg); RefreshAccountButton(); }
            active = t;
            t.LastActive = DateTime.UtcNow;
            list.ItemsSource = t.Rows;
            ShowMessage(t);
            MarkPlaying();
            RefreshSidebar();
            RefreshTabs();
            QueueSave();
            if (load && !t.Loaded) _ = LoadAsync(t, false);
        }

        void CloseTab(PaneTab t)
        {
            int i = tabs.IndexOf(t);
            if (i < 0) return;
            tabs.RemoveAt(i);
            var mine = tabs.Where(x => x.Acct == t.Acct).ToList();
            if (mine.Count == 0)
            {
                if (cfg.Accounts.Any(a => a.Id == t.Acct)) { var nt = new PaneTab { Id = nextId++, Acct = t.Acct, Kind = "home", Title = "Home" }; tabs.Add(nt); ActivateTab(nt); }
                return;
            }
            if (t == active) ActivateTab(tabs.Skip(i).FirstOrDefault(x => x.Acct == t.Acct) ?? mine.Last());
            else { RefreshTabs(); QueueSave(); }
        }

        void Cycle(int dir)
        {
            var mine = Mine();
            if (mine.Count < 2 || active == null) return;
            int i = mine.IndexOf(active);
            ActivateTab(mine[(i + dir + mine.Count) % mine.Count]);
        }

        void QueueSave() { saveTimer.Stop(); saveTimer.Start(); }

        void WriteTabs()
        {
            Store.Save("tabs.json", new SavedState
            {
                Tabs = tabs.Select(t => new SavedTab { Acct = t.Acct, Kind = t.Kind, Title = t.Title, Param = t.Param }).ToList(),
                Active = active == null ? 0 : tabs.IndexOf(active),
            });
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Escape && asSv != null) { StopAutoScroll(); e.Handled = true; return; }
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool typing = (Keyboard.FocusedElement is TextBox ftb && !ftb.IsReadOnly) || Keyboard.FocusedElement is PasswordBox;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            if (alt && !ctrl && (e.SystemKey == Key.Left || e.SystemKey == Key.Right))
            {
                e.Handled = true;
                if (e.SystemKey == Key.Left) GoBack(); else GoForward();
                return;
            }
            if (ctrl)
            {
                if (e.Key == Key.T) { e.Handled = true; NewSearchTab(); }
                else if (e.Key == Key.W) { e.Handled = true; if (active != null) CloseTab(active); }
                else if (e.Key == Key.Tab) { e.Handled = true; Cycle(shift ? -1 : 1); }
                else if (e.Key == Key.OemComma) { e.Handled = true; OpenSettings(); }
                else if (e.Key == Key.L || e.Key == Key.F) { e.Handled = true; searchBox.Focus(); searchBox.SelectAll(); }
                else if (e.Key >= Key.D1 && e.Key <= Key.D9)
                {
                    e.Handled = true;
                    var mine = Mine(); int n = e.Key - Key.D1;
                    if (n < mine.Count) ActivateTab(mine[n]);
                }
            }
            else if (e.Key == Key.Space && !typing) { e.Handled = true; TogglePlay(); }
        }

        // ================= loading lists =================
        string? UrlFor(PaneTab t)
        {
            long uid = api.Active?.UserId ?? 0;
            string q = Uri.EscapeDataString(t.Param);
            switch (t.Kind)
            {
                case "home": return "/mixed-selections?limit=20";
                case "feed": return "/stream?limit=30";
                case "trending": return "/charts?kind=trending&genre=soundcloud%3Agenres%3Aall-music&limit=30";
                case "top": return "/charts?kind=top&genre=soundcloud%3Agenres%3Aall-music&limit=30";
                case "likes": return uid == 0 ? null : "/users/" + uid + "/track_likes?limit=50";
                case "playlists": return uid == 0 ? null : "/users/" + uid + "/playlists_without_albums?limit=50";
                case "albums": return uid == 0 ? null : "/users/" + uid + "/albums?limit=50";
                case "following": return uid == 0 ? null : "/users/" + uid + "/followings?limit=50";
                case "reposts": return uid == 0 ? null : "/stream/users/" + uid + "/reposts?limit=30";
                case "mytracks": return uid == 0 ? null : "/users/" + uid + "/tracks?limit=50";
                case "recent": return "/me/play-history/tracks?limit=50";
                case "usercomments": return "/users/" + Uri.EscapeDataString(t.Param) + "/comments?limit=30";
                case "search": return t.Param.Length == 0 ? null : "/search?q=" + q + "&facet=model&limit=30";
                case "searchtracks": return t.Param.Length == 0 ? null : "/search/tracks?q=" + q + "&limit=30";
                case "searchplaylists": return t.Param.Length == 0 ? null : "/search/playlists?q=" + q + "&limit=30";
                case "searchalbums": return t.Param.Length == 0 ? null : "/search/albums?q=" + q + "&limit=30";
                case "searchusers": return t.Param.Length == 0 ? null : "/search/users?q=" + q + "&limit=30";
                case "user": return t.Param.Length == 0 ? null : "local:user";
                case "playlist": return t.Param.Length == 0 ? null : "local:playlist";
                case "track": return t.Param.Length == 0 ? null : "local:track";
                case "stations": return uid == 0 ? null : "local:stations";
                case "queue": return "local:queue";
            }
            return null;
        }

        static string EmptyText(PaneTab t)
        {
            if (SearchKinds.Contains(t.Kind)) return t.Param.Length == 0 ? "Type in the search box and press Enter." : "No results.";
            return t.Kind switch
            {
                "home" => "Nothing to show on Home yet.",
                "feed" => "Your feed is empty. Follow some artists on SoundCloud and their new tracks and reposts show up here.",
                "likes" => "You haven't liked any playable tracks yet.",
                "playlists" => "No playlists found.",
                "albums" => "No albums found.",
                "following" => "You aren't following anyone yet.",
                "reposts" => "You haven't reposted anything yet.",
                "mytracks" => "You haven't uploaded any tracks.",
                "recent" => "Nothing played recently.",
                "usercomments" => "No comments yet.",
                "queue" => "The queue is empty. Right-click a track and choose Add to queue.",
                "stations" => "Like a few tracks and stations based on them show up here.",
                _ => "Nothing here.",
            };
        }

        void AddEntryRows(PaneTab t, List<ScEntry> entries) => AddEntryRowsTo(t.Rows, t.Kind == "likes", entries);

        void AddEntryRowsTo(ICollection<ItemVM> rows, bool likesPage, List<ScEntry> entries)
        {
            foreach (var e in entries)
            {
                switch (e.Kind)
                {
                    case "header": rows.Add(ItemVM.Header(e.Title)); break;
                    case "track":
                        var tr = e.Track!;
                        if (tr.IsBlocked) break;
                        if (likesPage && !LikeRecentlyChanged(tr.Id, false)) likedIds.Add(tr.Id);
                        tr.UserFavorite = likedIds.Contains(tr.Id);
                        rows.Add(ItemVM.From(tr));
                        break;
                    case "playlist": rows.Add(ItemVM.From(e.Playlist!)); break;
                    case "user": rows.Add(ItemVM.From(e.User!)); break;
                }
            }
        }

        void FillQueueRows(PaneTab t)
        {
            t.Rows.Clear();
            foreach (var tr in queue) t.Rows.Add(ItemVM.From(tr));
            t.Loaded = true;
            t.Message = t.Rows.Count == 0 ? EmptyText(t) : "";
        }

        void RefreshQueueTab()
        {
            var qt = Mine().FirstOrDefault(x => x.Kind == "queue");
            if (qt == null) return;
            FillQueueRows(qt);
            if (qt == active) { ShowMessage(qt); MarkPlaying(); }
        }

        async Task LoadStationsAsync(PaneTab t, int g)
        {
            long uid = api.Active?.UserId ?? 0;
            var page = await api.GetTrackPageAsync("/users/" + uid + "/track_likes?limit=30");
            if (g != t.Gen) return;
            var tracks = page.Collection.Where(x => !x.IsBlocked).ToList();
            if (tracks.Count > 0) t.Rows.Add(ItemVM.Header("Stations based on your likes"));
            foreach (var tr in tracks.Take(12)) t.Rows.Add(ItemVM.Station(tr));
            var artists = tracks.Where(x => x.User != null).Select(x => x.User!).GroupBy(u => u.Id).Select(gp => gp.First()).Take(8).ToList();
            if (artists.Count > 0) t.Rows.Add(ItemVM.Header("Artist stations"));
            foreach (var u in artists) t.Rows.Add(ItemVM.ArtistStation(u));
            t.Next = null;
        }

        /// <summary>A song's own page: the song, its artist, and SoundCloud's related tracks.</summary>
        async Task LoadTrackPageAsync(PaneTab t, int g)
        {
            if (!long.TryParse(t.Param, out long id)) throw new ApiException(404, "This song link is not valid.");
            var tr = await api.GetTrackAsync(id);
            if (g != t.Gen) return;
            if (tr == null || tr.IsBlocked) throw new ApiException(404, "This song isn't available.");
            likeDelta.Remove(id); repostDelta.Remove(id); // the counts we just fetched already include earlier clicks
            tr.UserFavorite = likedIds.Contains(tr.Id);
            t.DTrack = tr;
            t.DUser = tr.User;
            t.Rows.Add(ItemVM.From(tr));
            if (t == active) ShowMessage(t); // show the song right away, related tracks follow
            Task<ScUser?>? artistTask = tr.User != null ? api.GetUserAsync(tr.User.Id.ToString()) : null;
            try
            {
                var related = await api.GetRelatedAsync(id);
                if (g != t.Gen) return;
                var fresh = related.Where(x => !x.IsBlocked && x.Id != id).ToList();
                if (fresh.Count > 0) t.Rows.Add(ItemVM.Header("Related tracks"));
                foreach (var x in fresh) { x.UserFavorite = likedIds.Contains(x.Id); t.Rows.Add(ItemVM.From(x)); }
            }
            catch (ApiException) { }
            if (artistTask != null)
            {
                try { var full = await artistTask; if (g != t.Gen) return; if (full != null) t.DUser = full; }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            t.Next = null;
        }

        /// <summary>On a profile: the artist's playlists and albums below their tracks.</summary>
        async Task AddProfileExtrasAsync(PaneTab t, int g)
        {
            string id = Uri.EscapeDataString(t.Param);
            var sections = new[] { ("Playlists", "/users/" + id + "/playlists_without_albums?limit=12"), ("Albums", "/users/" + id + "/albums?limit=12") };
            foreach (var (title, path) in sections)
            {
                try
                {
                    var page = await api.GetEntryPageAsync(path);
                    if (g != t.Gen) return;
                    if (page.Collection.Count == 0) continue;
                    t.Rows.Add(ItemVM.Header(title));
                    AddEntryRows(t, page.Collection);
                }
                catch (ApiException) { }
            }
        }

        async Task LoadAsync(PaneTab t, bool append)
        {
            int g = ++t.Gen;
            string? url = append ? t.Next : UrlFor(t);
            if (url == null)
            {
                t.Rows.Clear(); t.Message = EmptyText(t);
                if (t == active) ShowMessage(t);
                return;
            }
            if (append)
            {
                var more = t.Rows.FirstOrDefault(r => r.Kind == "more");
                if (more != null) t.Rows.Remove(more);
            }
            else
            {
                t.Rows.Clear(); t.DTrack = null; t.DUser = null; t.DPlaylist = null; t.Comments.Clear(); t.Subs.Clear(); t.Sub = "all"; t.Message = "Loading…";
                if (t == active) ShowMessage(t);
            }

            try
            {
                if (t.Kind != "queue" && api.Active == null) throw new ApiException(401, "Sign in to SoundCloud with the button at the top left.");
                if (t.Kind == "queue") { FillQueueRows(t); t.Next = null; }
                else if (t.Kind == "stations") await LoadStationsAsync(t, g);
                else if (t.Kind == "usercomments") await LoadUserCommentsAsync(t, g, append, url);
                else if (t.Kind == "track") await LoadTrackPageAsync(t, g);
                else if (t.Kind == "user") await LoadUserPageAsync(t, g);
                else if (t.Kind == "playlist")
                {
                    var (meta, tracks) = await api.GetPlaylistAsync(t.Param);
                    if (g != t.Gen) return;
                    t.IsStation = t.Param.Contains("-stations:", StringComparison.Ordinal);
                    t.StationSeed = null; t.StationArtist = null;
                    if (t.IsStation)
                    {
                        // a station has no page of its own on the API: build one from the song / artist it is based on
                        string tail = t.Param.Substring(t.Param.LastIndexOf(':') + 1);
                        if (t.Param.Contains("track-stations:", StringComparison.Ordinal) && long.TryParse(tail, out long seedId))
                            t.StationSeed = tracks.FirstOrDefault(x => x.Id == seedId);
                        else if (t.Param.Contains("artist-stations:", StringComparison.Ordinal))
                        {
                            try { t.StationArtist = await api.GetUserAsync(tail); } catch (Exception ex) when (ex is not OperationCanceledException) { }
                            if (g != t.Gen) return;
                        }
                        meta ??= new ScPlaylist { TitleRaw = t.StationSeed?.Title ?? t.StationArtist?.Username ?? "Station", Tracks = tracks };
                        meta.IsSystem = true;
                    }
                    t.DPlaylist = meta;
                    foreach (var tr in tracks)
                    {
                        if (tr.IsBlocked) continue;
                        tr.UserFavorite = likedIds.Contains(tr.Id);
                        t.Rows.Add(ItemVM.From(tr));
                    }
                    t.Next = null;
                }
                else
                {
                    var page = await api.GetEntryPageAsync(url);
                    if (g != t.Gen) return;
                    AddEntryRows(t, page.Collection);
                    t.Next = page.NextHref;
                    if (t.Kind == "following")
                    {
                        // everyone on this list is followed, for sure
                        foreach (var e in page.Collection) if (e.Kind == "user" && e.User != null && !(followTouched.TryGetValue(e.User.Id, out var ft) && ft.At > DateTime.UtcNow.AddSeconds(-60) && !ft.On)) SetFollowLocal(e.User.Id, true, false);
                        if (!append) _ = EnsureFollowSyncAsync(true);
                    }
                    else if (SearchKinds.Contains(t.Kind) && !append)
                    {
                        if (page.Facets.Count > 0) t.Facets = page.Facets;
                    }
                    // a page can come back with only hidden items while more pages exist: keep going
                    if (g == t.Gen && t.Next != null && !t.Rows.Any(r => r.Kind != "header" && r.Kind != "more") && t.Rows.Count < 5)
                    {
                        var more = await api.GetEntryPageAsync(t.Next);
                        if (g != t.Gen) return;
                        AddEntryRows(t, more.Collection);
                        t.Next = more.NextHref;
                    }
                }
                if (t.Next != null && t.Kind != "usercomments") t.Rows.Add(ItemVM.More());
                t.Loaded = true;
                t.Message = t.Rows.Count == 0 ? EmptyText(t) : "";
                if (t.AutoPlay)
                {
                    t.AutoPlay = false;
                    var first = t.Rows.FirstOrDefault(r => r.Kind == "track" && r.Track != null);
                    if (first != null) PlayFromRows(t, first);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (g != t.Gen) return;
                t.AutoPlay = false;
                t.Message = ex.Message;
            }
            if (t == active) { ShowMessage(t); MarkPlaying(); }
        }

        void ActivateSelected()
        {
            if (list.SelectedItem is ItemVM it) ActivateItem(it);
        }

        /// <summary>Click on a card: songs open their page, everything else opens/starts as in the list.</summary>
        void OpenCard(ItemVM it)
        {
            if (it.Kind == "track" && it.Track != null) OpenTrack(it.Track);
            else ActivateItem(it);
        }

        /// <summary>The round play button on a card.</summary>
        void PlayItem(ItemVM it)
        {
            try
            {
                if (it.Kind == "track" && active != null) PlayFromRows(active, it);
                else if (it.Kind == "playlist" && it.Playlist != null) AddTab("playlist", Cut(it.Playlist.Title, 24), it.Playlist.Key, true, true);
                else if (it.Kind == "station") StartStation(it.StationKey, it.Title.Replace("Station: ", ""));
            }
            catch (Exception ex) { Store.Log("play card", ex); ShowToast(ex.Message); }
        }

        async void ActivateItem(ItemVM it)
        {
            try
            {
                if (active == null) return;
                switch (it.Kind)
                {
                    case "track": PlayFromRows(active, it); break;
                    case "playlist":
                        if (it.Playlist != null) NavigateHere("playlist", Cut(it.Playlist.Title, 24), it.Playlist.Key);
                        break;
                    case "user":
                        OpenArtist(it.User);
                        break;
                    case "station": StartStation(it.StationKey, it.Title.Replace("Station: ", "")); break;
                    case "more": await LoadAsync(active, true); break;
                }
            }
            catch (Exception ex) { Store.Log("activate", ex); ShowToast(ex.Message); }
        }

        void StartStation(string key, string name)
        {
            if (key.Length == 0) return;
            AddTab("playlist", Cut("Station · " + name, 24), key, true, true);
        }

        // ---- right-click menu ----
        void ListContextMenu(MouseButtonEventArgs e)
        {
            try
            {
                var c = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
                if (c == null || c.DataContext is not ItemVM it) return;
                c.IsSelected = true;
                if (ShowItemMenu(it, list, PlacementMode.MousePoint)) e.Handled = true;
            }
            catch (Exception ex) { Store.Log("context menu", ex); }
        }

        bool ShowItemMenu(ItemVM it, FrameworkElement target, PlacementMode mode)
        {
            try
            {
                var panel = new StackPanel { Margin = new Thickness(4) };
                void Row(string text, Action act) => panel.Children.Add(MenuRow(text, () => { CloseMenu(); try { act(); } catch (Exception ex) { Store.Log("menu", ex); ShowToast(ex.Message); } }));

                if (it.Kind == "track" && it.Track != null)
                {
                    var tr = it.Track;
                    Row("▶  Play", () => { if (active != null) PlayFromRows(active, it); });
                    Row("⏭  Play next", () => QueueAdd(tr, true));
                    Row("＋  Add to queue", () => QueueAdd(tr, false));
                    Row("◎  Start station", () => StartStation("soundcloud:track-stations:" + tr.Id, tr.Title));
                    Row("♪  Go to song", () => OpenTrack(tr));
                    Row("⧉  Open in new tab", () => InNewTab(() => OpenTrack(tr)));
                    panel.Children.Add(MenuSep());
                    Row(likedIds.Contains(tr.Id) ? "♥  Unlike" : "♡  Like", () => ToggleLikeOf(tr));
                    Row(repostedIds.Contains(tr.Id) ? "↻  Remove repost" : "↻  Repost", () => ToggleRepostOf(tr));
                    if (tr.User != null)
                    {
                        var u = tr.User;
                        panel.Children.Add(MenuSep());
                        Row("☻  Go to " + Cut(u.Username ?? "artist", 22), () => OpenArtist(u));
                        Row("＋  Follow " + Cut(u.Username ?? "artist", 20), () => SetFollow(u, true));
                    }
                    panel.Children.Add(MenuSep());
                    Row("⧉  Copy link", () => { if (tr.PermalinkUrl.Length > 0) Clipboard.SetText(tr.PermalinkUrl); });
                    Row("↗  Open on SoundCloud", () => OpenUrl(tr.PermalinkUrl));
                }
                else if (it.Kind == "playlist" && it.Playlist != null)
                {
                    var pl = it.Playlist;
                    Row("▶  Open", () => NavigateHere("playlist", Cut(pl.Title, 24), pl.Key));
                    Row("⧉  Open in new tab", () => InNewTab(() => NavigateHere("playlist", Cut(pl.Title, 24), pl.Key)));
                    Row("▶  Play", () => AddTab("playlist", Cut(pl.Title, 24), pl.Key, true, true));
                    if (pl.Id != 0)
                    {
                        Row("♡  Like playlist", () => _ = LikePlaylistAsync(pl, true));
                        Row("♥  Unlike playlist", () => _ = LikePlaylistAsync(pl, false));
                    }
                    if (!string.IsNullOrEmpty(pl.PermalinkUrl))
                    {
                        panel.Children.Add(MenuSep());
                        Row("⧉  Copy link", () => Clipboard.SetText(pl.PermalinkUrl!));
                        Row("↗  Open on SoundCloud", () => OpenUrl(pl.PermalinkUrl));
                    }
                }
                else if (it.Kind == "user" && it.User != null)
                {
                    var u = it.User;
                    Row("☻  Open profile", () => OpenArtist(u));
                    Row("⧉  Open in new tab", () => InNewTab(() => OpenArtist(u)));
                    Row("◎  Artist station", () => StartStation("soundcloud:artist-stations:" + u.Id, u.Username ?? "artist"));
                    Row("＋  Follow", () => SetFollow(u, true));
                    Row("－  Unfollow", () => SetFollow(u, false));
                    if (!string.IsNullOrEmpty(u.PermalinkUrl)) Row("↗  Open on SoundCloud", () => OpenUrl(u.PermalinkUrl));
                }
                else if (it.Kind == "station") Row("▶  Start station", () => StartStation(it.StationKey, it.Title.Replace("Station: ", "")));
                else return false;

                OpenMenu(target, panel, mode);
                return true;
            }
            catch (Exception ex) { Store.Log("context menu", ex); return false; }
        }

        async Task<bool> LikePlaylistAsync(ScPlaylist pl, bool like)
        {
            string forAccount = cfg.Current;
            string key = forAccount + ":plike:" + pl.Id;
            if (!writeBusy.Add(key)) return false;
            try
            {
                await api.LikePlaylistAsync(pl.Id, like);
                if (cfg.Current != forAccount) return true; // liked fine, but another account is active now: don't toast about it
                ShowToast(like ? "Playlist liked" : "Playlist unliked", true);
                return true;
            }
            catch (Exception ex) { Store.Log("playlist like " + pl.Id, ex); ShowToast(ex.Message); return false; }
            finally { writeBusy.Remove(key); }
        }

        void SetRepost(ScTrack tr, bool on) => _ = RepostAsync(tr, on);

        void SetFollow(ScUser u, bool on) => _ = SetFollowAsync(u, on);

        /// <summary>The follow state changes only after SoundCloud answered 2xx. Returns true when it did.</summary>
        async Task<bool> SetFollowAsync(ScUser u, bool want)
        {
            string forAccount = cfg.Current;
            string key = forAccount + ":follow:" + u.Id;
            if (!writeBusy.Add(key)) return false;
            ShowToast(want ? "Following…" : "Unfollowing…", true);
            try
            {
                await api.FollowAsync(u.Id, want, u.PermalinkUrl);
                if (cfg.Current != forAccount) return false;
                SetFollowLocal(u.Id, want);
                UpdateTransport();
                ShowToast((want ? "Following " : "Unfollowed ") + u.Username, true);
                FollowListChanged();
                return true;
            }
            catch (Exception ex) { Store.Log("follow " + u.Id, ex); ShowToast(ex.Message); return false; }
            finally { writeBusy.Remove(key); }
        }

        // ---- queue ----
        void QueueAdd(ScTrack tr, bool next)
        {
            if (current == null || qi < 0 || qi >= queue.Count)
            {
                queue = new List<ScTrack> { tr }; qi = 0;
                _ = PlayCurrentAsync();
                RefreshQueueTab();
                return;
            }
            if (next) queue.Insert(qi + 1, tr); else queue.Add(tr);
            RefreshQueueTab();
            ShowToast(next ? "Playing next" : "Added to queue", true);
        }

        void ClearQueueAhead()
        {
            if (qi < 0 || qi >= queue.Count) return;
            queue = queue.Take(qi + 1).ToList();
            RefreshQueueTab();
        }

        static Border MiniToggle(bool on, Action<bool> change)
        {
            var thumb = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var track = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Child = thumb };
            void Apply() { track.Background = Ui.B(on ? "#FF5500" : "#3A3A3A"); thumb.Margin = new Thickness(on ? 19 : 3, 0, 0, 0); }
            Apply();
            track.MouseLeftButtonUp += (_, _) => { on = !on; Apply(); change(on); };
            return track;
        }

        /// <summary>The small "..." menu on a Next-up row — only what soundcloud.com itself offers there.</summary>
        void ShowQueueRowMenu(ScTrack tr, FrameworkElement target)
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            void Row(string text, Action act) => panel.Children.Add(MenuRow(text, () => { CloseMenu(); try { act(); } catch (Exception ex) { Store.Log("menu", ex); ShowToast(ex.Message); } }));
            Row("⏭  Add to Next up", () => QueueAdd(tr, true));
            Row("◎  Go to station", () => StartStation("soundcloud:track-stations:" + tr.Id, tr.Title));
            Row("⚑  Report", () => { CopyLink(tr.PermalinkUrl); ShowToast("Link copied - report it from the song's page on SoundCloud", true); });
            OpenMenu(target, panel);
        }

        FrameworkElement NextUpRow(ScTrack tr, Action? click, bool highlight = false)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var art = ArtBox(ItemVM.BestArt(tr.ArtworkUrl, tr.User?.AvatarUrl), 40, false, "t200x200", 80);
            art.CornerRadius = new CornerRadius(5);
            var tx = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            tx.Children.Add(new TextBlock { Text = tr.Title, Foreground = highlight ? Ui.B("#FF7A3D") : Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            tx.Children.Add(new TextBlock { Text = tr.ArtistName, Foreground = Ui.B("#9a9a9a"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(art, 0); Grid.SetColumn(tx, 1);
            g.Children.Add(art); g.Children.Add(tx);

            if (highlight)
            {
                bool liked = likedIds.Contains(tr.Id);
                var heartG = new TextBlock { Text = liked ? "\uEB52" : "\uEB51", FontFamily = Ui.Icons, FontSize = 13, Foreground = Ui.B(liked ? "#FF7A3D" : "#cccccc"), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 10, 0) };
                heartG.MouseLeftButtonUp += async (_, e) => { e.Handled = true; await ToggleLikeAsync(tr); bool l = likedIds.Contains(tr.Id); heartG.Text = l ? "\uEB52" : "\uEB51"; heartG.Foreground = Ui.B(l ? "#FF7A3D" : "#cccccc"); };
                var moreWrap = new Border { Child = new TextBlock { Text = "\uE712", FontFamily = Ui.Icons, FontSize = 13, Foreground = Ui.B("#cccccc"), VerticalAlignment = VerticalAlignment.Center }, Cursor = Cursors.Hand };
                moreWrap.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowQueueRowMenu(tr, moreWrap); };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(heartG); sp.Children.Add(moreWrap);
                Grid.SetColumn(sp, 2); g.Children.Add(sp);
            }
            else
            {
                var dur = Txt(Ui.Fmt(TimeSpan.FromMilliseconds(tr.Duration)), 11, "#808080");
                dur.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(dur, 2); g.Children.Add(dur);
            }

            var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 5, 10, 5), Margin = new Thickness(0, 1, 0, 1), Background = highlight ? Ui.B("#26FF5500") : Brushes.Transparent, Cursor = Cursors.Hand, Child = g };
            if (!highlight && click != null) { Ui.Hover(row, "#15FFFFFF"); row.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); }; }
            return row;
        }

        /// <summary>The "Next up" queue popup, like soundcloud.com's: upcoming tracks, the one playing now, then Autoplay station.</summary>
        void ShowNextUpPanel(FrameworkElement anchor)
        {
            CloseMenu();
            var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14), Width = 320 };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = "Next up", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            var clear = new TextBlock { Text = "Clear", Foreground = Ui.B("#9a9a9a"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 14, 0) };
            clear.MouseLeftButtonUp += (_, e) => { e.Handled = true; ClearQueueAhead(); CloseMenu(); };
            Grid.SetColumn(clear, 1); head.Children.Add(clear);
            var xBtn = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.B("#2A2A2A"), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "✕", FontSize = 9, Foreground = Ui.B("#cccccc"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(xBtn, "#3a3a3a");
            xBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseMenu(); };
            Grid.SetColumn(xBtn, 2); head.Children.Add(xBtn);
            root.Children.Add(head);
            root.Children.Add(new Border { Height = 1, Background = Ui.B("#2a2a2a"), Margin = new Thickness(0, 10, 0, 6) });

            var upcoming = (qi >= 0 && qi < queue.Count) ? queue.Skip(qi + 1).ToList() : new List<ScTrack>();
            var listBox = new StackPanel();
            if (upcoming.Count == 0)
                listBox.Children.Add(new TextBlock { Text = "Nothing queued next.", Foreground = Ui.B("#808080"), FontSize = 12.5, Margin = new Thickness(6, 6, 6, 6) });
            else
                for (int i = 0; i < upcoming.Count; i++)
                {
                    int idx = qi + 1 + i;
                    listBox.Children.Add(NextUpRow(upcoming[i], () => { qi = idx; _ = PlayCurrentAsync(); CloseMenu(); }));
                }
            root.Children.Add(new ScrollViewer { MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listBox });

            if (current != null)
            {
                root.Children.Add(new Border { Height = 1, Background = Ui.B("#2a2a2a"), Margin = new Thickness(0, 6, 0, 6) });
                root.Children.Add(NextUpRow(current, null, true));
            }

            root.Children.Add(new Border { Height = 1, Background = Ui.B("#2a2a2a"), Margin = new Thickness(0, 10, 0, 10) });
            var apRow = new Grid();
            apRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            apRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var apText = new StackPanel();
            apText.Children.Add(new TextBlock { Text = "Autoplay station", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold });
            apText.Children.Add(new TextBlock { Text = "Hear related tracks based on what's playing now.", Foreground = Ui.B("#8a8a8a"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 230 });
            Grid.SetColumn(apText, 0); apRow.Children.Add(apText);
            var apToggle = MiniToggle(cfg.Settings.Autoplay, v => { cfg.Settings.Autoplay = v; Store.Save("config.json", cfg); RefreshAutoplayText(); });
            apToggle.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(apToggle, 1); apRow.Children.Add(apToggle);
            root.Children.Add(apRow);

            popup = new Popup
            {
                PlacementTarget = anchor, Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = 40,
                Child = new Border
                {
                    Background = Ui.B("#F5161616"), BorderBrush = Ui.B("#3a3a3a"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(18), Child = root, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black },
                },
            };
            popup.IsOpen = true;
        }

        /// <summary>Queue ran out: keep going with tracks SoundCloud lists as related to the last one (like the website's autoplay).</summary>
        async Task AutoplayAsync()
        {
            var seed = current;
            if (seed == null || autoplaying) { player.Stop(); UpdateTransport(); UpdatePresence(); return; }
            autoplaying = true;
            try
            {
                var related = await api.GetRelatedAsync(seed.Id);
                var have = new HashSet<long>(queue.Select(x => x.Id));
                var fresh = related.Where(x => !x.IsBlocked && have.Add(x.Id)).ToList();
                if (fresh.Count == 0) { player.Stop(); UpdateTransport(); UpdatePresence(); return; }
                foreach (var x in fresh) x.UserFavorite = likedIds.Contains(x.Id);
                int start = queue.Count;
                queue.AddRange(fresh);
                qi = start;
                RefreshQueueTab();
                await PlayCurrentAsync();
            }
            catch (Exception ex)
            {
                Store.Log("autoplay", ex);
                player.Stop(); UpdateTransport(); UpdatePresence();
            }
            finally { autoplaying = false; }
        }

        // ================= playback =================
        void PlayFromRows(PaneTab t, ItemVM it) => PlayFromList(t.Rows, it);

        /// <summary>Queue = every song in the given list, starting at the clicked one.</summary>
        void PlayFromList(IEnumerable<ItemVM> rows, ItemVM it)
        {
            var tracks = rows.Where(r => r.Kind == "track" && r.Track != null).Select(r => r.Track!).ToList();
            int idx = tracks.FindIndex(x => ReferenceEquals(x, it.Track));
            if (idx < 0)
            {
                if (it.Track == null) return;
                queue = new List<ScTrack> { it.Track }; qi = 0;
                _ = PlayCurrentAsync();
                return;
            }
            queue = tracks; qi = idx;
            _ = PlayCurrentAsync();
        }

        async Task PlayCurrentAsync()
        {
            if (qi < 0 || qi >= queue.Count) return;
            current = queue[qi];
            UpdateNowPlayingUi();
            MarkPlaying();
            await player.PlayAsync(current);
        }

        /// <summary>manual = the person pressed Next / clicked another track; false = the song finished on its own (so "Repeat one" / "Repeat all" apply).</summary>
        void Next(bool manual = true)
        {
            if (queue.Count == 0) return;
            if (!manual && cfg.Settings.Repeat == "one") { player.Seek(TimeSpan.Zero); if (!player.IsPlaying) player.Toggle(); UpdateTransport(); UpdatePresence(); return; }
            int n;
            if (cfg.Settings.Shuffle && queue.Count > 1) { do n = rng.Next(queue.Count); while (n == qi); }
            else n = qi + 1;
            if (n >= queue.Count)
            {
                if (!manual && cfg.Settings.Repeat == "all" && queue.Count > 0) { qi = 0; _ = PlayCurrentAsync(); return; }
                if (cfg.Settings.Autoplay && current != null) { _ = AutoplayAsync(); return; }
                player.Stop(); UpdateTransport(); UpdatePresence(); return;
            }
            qi = n;
            _ = PlayCurrentAsync();
        }

        void Prev()
        {
            if (queue.Count == 0) return;
            if (player.Position.TotalSeconds > 3) { player.Seek(TimeSpan.Zero); UpdatePresence(); return; }
            if (qi > 0) { qi--; _ = PlayCurrentAsync(); }
        }

        void TogglePlay()
        {
            if (!player.HasTrack && !player.IsLoading && qi >= 0 && qi < queue.Count) { _ = PlayCurrentAsync(); return; }
            player.Toggle();
            UpdateTransport();
            UpdatePresence();
        }

        void ToggleShuffle()
        {
            cfg.Settings.Shuffle = !cfg.Settings.Shuffle;
            Store.Save("config.json", cfg);
            UpdateTransport();
        }

        void ToggleRepeat()
        {
            cfg.Settings.Repeat = cfg.Settings.Repeat switch { "off" => "all", "all" => "one", _ => "off" };
            Store.Save("config.json", cfg);
            UpdateTransport();
        }

        void ToggleFollowCurrentArtist()
        {
            var u = current?.User;
            if (u == null) return;
            SetFollow(u, !IsFollowed(u.Id));
        }

        void ToggleLike() => ToggleLikeOf(current);

        async void ToggleLikeOf(ScTrack? tr) => await ToggleLikeAsync(tr);

        /// <summary>The heart changes only after SoundCloud answered 2xx. A refusal leaves everything as it was and says why.</summary>
        async Task<bool> ToggleLikeAsync(ScTrack? tr)
        {
            if (tr == null) return false;
            string forAccount = cfg.Current;
            string key = forAccount + ":like:" + tr.Id; // keyed by account too: account A and account B liking the same track id are independent actions
            if (!writeBusy.Add(key)) return false;
            bool want = !likedIds.Contains(tr.Id);
            ShowToast(want ? "Liking…" : "Removing like…", true);
            try
            {
                await api.LikeAsync(tr.Id, want, tr.PermalinkUrl);
                if (cfg.Current != forAccount) return false; // another account is active now: this answer is not about it
                SetLikedLocal(tr, want);
                ShowToast(want ? "Liked" : "Like removed", true);
                return true;
            }
            catch (Exception ex) { Store.Log("like " + tr.Id, ex); ShowToast(ex.Message); return false; }
            finally { writeBusy.Remove(key); }
        }

        readonly Dictionary<long, (bool On, DateTime At)> likeTouched = new Dictionary<long, (bool, DateTime)>();

        /// <summary>True when you liked / unliked this track here in the last minute (SoundCloud's lists lag behind and must not undo it).</summary>
        bool LikeRecentlyChanged(long id, bool on) =>
            likeTouched.TryGetValue(id, out var v) && v.On == on && v.At > DateTime.UtcNow.AddSeconds(-60);

        void SetLikedLocal(ScTrack tr, bool on)
        {
            likeTouched[tr.Id] = (on, DateTime.UtcNow);
            tr.UserFavorite = on;
            if (on) likedIds.Add(tr.Id); else likedIds.Remove(tr.Id);
            if (current != null && current.Id == tr.Id) current.UserFavorite = on;
            likeDelta[tr.Id] = (likeDelta.TryGetValue(tr.Id, out var d) ? d : 0) + (on ? 1 : -1);
            FlagsChanged(tr.Id);
        }

        async void ToggleRepostOf(ScTrack? tr) { if (tr != null) await RepostAsync(tr, !repostedIds.Contains(tr.Id)); }

        /// <summary>The repost state changes only after SoundCloud answered 2xx.</summary>
        async Task<bool> RepostAsync(ScTrack tr, bool want)
        {
            string forAccount = cfg.Current;
            string key = forAccount + ":repost:" + tr.Id;
            if (!writeBusy.Add(key)) return false;
            ShowToast(want ? "Reposting…" : "Removing repost…", true);
            try
            {
                await api.RepostAsync(tr.Id, want, tr.PermalinkUrl);
                if (cfg.Current != forAccount) return false;
                SetRepostedLocal(tr.Id, want);
                ShowToast(want ? "Reposted" : "Repost removed", true);
                return true;
            }
            catch (Exception ex) { Store.Log("repost " + tr.Id, ex); ShowToast(ex.Message); return false; }
            finally { writeBusy.Remove(key); }
        }

        void SetRepostedLocal(long id, bool on)
        {
            if (on) repostedIds.Add(id); else repostedIds.Remove(id);
            repostDelta[id] = (repostDelta.TryGetValue(id, out var d) ? d : 0) + (on ? 1 : -1);
            FlagsChanged(id);
        }

        void OnPlayerStarted()
        {
            failStreak = 0;
            titleText.Opacity = 1;
            var tr = current;
            if (tr != null) tr.UserFavorite = likedIds.Contains(tr.Id);
            UpdateTransport();
            UpdatePresence();
            if (tr != null) _ = ReportPlayAsync(tr);
        }

        // Actions that are waiting for SoundCloud's answer ("like:123", "follow:45"...): a second click on the same thing is ignored, not sent again.
        readonly HashSet<string> writeBusy = new HashSet<string>();

        int historyFails;
        long lastReported;
        DateTime lastReportedAt = DateTime.MinValue;

        /// <summary>Adds the track you just started to your SoundCloud listening history (quietly; gives up after 3 failures in a row).</summary>
        async Task ReportPlayAsync(ScTrack tr)
        {
            if (api.Active == null || historyFails >= 3) return;
            if (tr.Id == lastReported && (DateTime.UtcNow - lastReportedAt).TotalSeconds < 30) return;
            lastReported = tr.Id; lastReportedAt = DateTime.UtcNow;
            string forAccount = cfg.Current;
            try
            {
                await api.ReportPlayAsync(tr.Id);
                if (cfg.Current != forAccount) return; // sent fine, but another account is active now: its own counters are untouched
                historyFails = 0;
                foreach (var t in Mine().Where(x => x.Kind == "recent")) t.Loaded = false; // next time History is opened it reloads
            }
            catch (Exception ex)
            {
                if (cfg.Current == forAccount) historyFails++; // don't blame the now-active account for an old account's failure
                Store.Log("play history " + tr.Id, ex);
            }
        }

        void OnPlayerFailed(string msg)
        {
            titleText.Opacity = 1;
            ShowToast(msg);
            UpdateTransport();
            UpdatePresence();
            failStreak++;
            if (failStreak < 3 && queue.Count > qi + 1 && !msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                Dispatcher.InvokeAsync(() => Next());
        }

        void ShowToast(string text, bool ok = false)
        {
            toast.Foreground = ok ? Ui.B("#9ad49a") : Ui.B("#ff8a80");
            toast.Text = text; toast.Visibility = Visibility.Visible;
            toastTimer.Stop(); toastTimer.Start();
        }

        public void SetVolume(int v, bool commit)
        {
            v = Math.Max(0, Math.Min(100, v));
            cfg.Settings.Volume = v;
            player.Volume = v / 100f;
            volGlyph.Text = v == 0 ? "\uE74F" : "\uE767";
            if (!volSlider.Dragging) volSlider.SetValue(v);
            if (commit) Store.Save("config.json", cfg);
        }

        // ---- UI sync ----
        void UpdateNowPlayingUi()
        {
            var t = current;
            titleText.Text = t?.Title ?? "Nothing playing";
            artistText.Text = t?.ArtistName ?? "";
            titleText.Cursor = t != null ? Cursors.Hand : Cursors.Arrow;
            posText.Text = "0:00";
            durText.Text = t != null ? Ui.Fmt(TimeSpan.FromMilliseconds(t.Duration)) : "0:00";
            seek.SetValue(0);
            artBrush.ImageSource = null;
            string art = t == null ? "" : ItemVM.BestArt(t.ArtworkUrl, t.User?.AvatarUrl);
            if (art.Length > 0)
            {
                var tr = t;
                _ = Task.Run(async () =>
                {
                    var img = await ArtCache.GetAsync(ArtCache.Size(art, "t200x200"), 112);
                    await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(current, tr)) artBrush.ImageSource = img; });
                });
            }
            ResetDetailProgress();
            ResetFeedProgress();
            UpdateTransport();
        }

        void UpdateTransport()
        {
            bool playing = player.IsPlaying;
            playGlyph.Text = playing ? "\uE769" : "\uE768";
            shuffleGlyph.Foreground = cfg.Settings.Shuffle ? Ui.B("#FF7A3D") : Ui.B("#8a8a8a");
            bool fav = current != null && likedIds.Contains(current.Id);
            heartGlyph.Text = fav ? "\uEB52" : "\uEB51";
            heartGlyph.Foreground = fav ? Ui.B("#FF7A3D") : Ui.B("#cccccc");
            bool rep = current != null && repostedIds.Contains(current.Id);
            repostGlyph.Foreground = rep ? Ui.B("#FF7A3D") : Ui.B("#cccccc");
            repeatGlyph.Text = cfg.Settings.Repeat == "one" ? "\uE8ED" : "\uE8EE";
            repeatGlyph.Foreground = cfg.Settings.Repeat == "off" ? Ui.B("#8a8a8a") : Ui.B("#FF7A3D");
            var cu = current?.User;
            if (cu != null && !followState.ContainsKey(cu.Id)) _ = LoadFollowStateAsync(cu.Id, UpdateTransport);
            bool following = cu != null && IsFollowed(cu.Id);
            artistFollowGlyph.Text = following ? "\uE77B" : "\uE8FA";
            artistFollowGlyph.Foreground = following ? Ui.B("#FF7A3D") : Ui.B("#cccccc");
            UpdateDetailPlayGlyph();
            RefreshPage();
        }

        void MarkPlaying()
        {
            string? urn = current?.Key;
            foreach (var t in tabs)
                foreach (var r in t.Rows)
                    r.IsPlaying = urn != null && r.Kind == "track" && r.Track != null && r.Track.Key == urn;
            foreach (var (it, tb) in cardTitles) tb.Foreground = it.TitleBrush;
        }

        void UiTick()
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            if (!player.HasTrack) return;
            var d = player.Duration;
            var p = player.Position;
            if (d.TotalSeconds <= 0) return;
            durText.Text = Ui.Fmt(d);
            UpdateDetailProgress(p, d);
            UpdateFeedProgress(p, d);
            if (!seek.Dragging)
            {
                seek.SetValue((int)Math.Min(1000, p.TotalSeconds / d.TotalSeconds * 1000));
                posText.Text = Ui.Fmt(p);
            }
        }

        // ================= Discord presence =================
        string lastSentSig = "";
        Dictionary<string, object>? pending;
        bool hasPending;
        DateTime lastSent = DateTime.MinValue;

        static string Fix(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length > 128) s = s.Substring(0, 125) + "…";
            return s.Length < 2 ? s.PadRight(2, '\u200b') : s;
        }

        Dictionary<string, object>? BuildActivity()
        {
            var s = cfg.Settings;
            var t = Strings.En;
            var tr = current;
            if (tr != null && s.ShowSong && player.HasTrack && (player.IsPlaying || !s.HideWhilePaused))
            {
                string cover = ItemVM.BestArt(tr.ArtworkUrl, tr.User?.AvatarUrl);
                var assets = new Dictionary<string, object>
                {
                    ["large_image"] = s.ShowCover && cover.StartsWith("https://") ? ArtCache.Size(cover, "t500x500") : "soundcloud",
                };
                // Discord prints large_text as a third line on "Listening" cards, so it must NOT repeat the title.
                // It is only used to say "Paused" while paused.
                if (!player.IsPlaying) assets["large_text"] = Fix(t.Paused);
                var act = new Dictionary<string, object>
                {
                    ["type"] = 2,
                    ["name"] = "KaruCloud",
                    ["details"] = Fix(tr.Title),
                    ["state"] = Fix(tr.ArtistName.Length > 0 ? tr.ArtistName : "KaruCloud"),
                    ["status_display_type"] = s.StatusDisplay == "song" ? 2 : s.StatusDisplay == "artist" ? 1 : 0,
                    ["assets"] = assets,
                };
                if (player.IsPlaying && s.ShowTimestamps)
                {
                    double dur = player.Duration.TotalSeconds > 0 ? player.Duration.TotalSeconds : tr.Duration / 1000.0;
                    long start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)player.Position.TotalSeconds;
                    var ts = new Dictionary<string, object> { ["start"] = start };
                    if (dur > 0) ts["end"] = start + (long)Math.Ceiling(dur);
                    act["timestamps"] = ts;
                }
                if (s.LinkButtons)
                {
                    var buttons = new List<object>();
                    if (tr.PermalinkUrl.StartsWith("https://")) buttons.Add(new Dictionary<string, object> { ["label"] = Cut(t.Listen, 32), ["url"] = tr.PermalinkUrl });
                    string au = tr.User?.PermalinkUrl ?? "";
                    if (au.StartsWith("https://")) buttons.Add(new Dictionary<string, object> { ["label"] = Cut(t.Artist, 32), ["url"] = au });
                    if (buttons.Count > 0) act["buttons"] = buttons;
                }
                return act;
            }
            if (s.ShowBrowsing)
            {
                return new Dictionary<string, object>
                {
                    ["type"] = 0,
                    ["details"] = Fix(t.Browsing),
                    ["state"] = Fix(active?.Title ?? "KaruCloud"),
                    ["assets"] = new Dictionary<string, object> { ["large_image"] = "soundcloud", ["large_text"] = "KaruCloud" },
                };
            }
            return null;
        }

        void UpdatePresence()
        {
            pending = BuildActivity();
            hasPending = true;
            FlushPresence();
        }

        void FlushPresence()
        {
            if (!hasPending) return;
            double since = (DateTime.UtcNow - lastSent).TotalSeconds;
            if (since < 4) // Discord rate limit: send the latest state a moment later
            {
                if (!presenceTimer.IsEnabled) { presenceTimer.Interval = TimeSpan.FromSeconds(4 - since + 0.1); presenceTimer.Start(); }
                return;
            }
            hasPending = false;
            lastSent = DateTime.UtcNow;
            rpc.SetActivity(pending);
        }

        // ================= accounts & settings API =================
        public Settings Prefs => cfg.Settings;
        public List<Account> AccountList => cfg.Accounts;
        public string CurrentAccount => cfg.Current;
        public string RpcStatus => rpc.Status;
        public string RpcUser => rpc.User;
        public bool IsAccountPlaying(string id) => player.IsPlaying && current != null && cfg.Current == id;

        public void PrefsChanged()
        {
            Store.Save("config.json", cfg);
            ApplySettings();
        }

        public void ResetPrefs()
        {
            var o = cfg.Settings;
            cfg.Settings = new Settings { Volume = o.Volume };
            PrefsChanged();
        }

        void ApplySettings()
        {
            var s = cfg.Settings;
            rpc.SetClientId(DiscordRpc.AppId);
            player.Volume = s.Volume / 100f;
            lastSentSig = "";
            UpdatePresence();
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (k != null)
                {
                    if (s.LaunchAtLogin && Environment.ProcessPath != null)
                        k.SetValue("KaruCloud", "\"" + Environment.ProcessPath + "\" --hidden");
                    else k.DeleteValue("KaruCloud", false);
                    k.DeleteValue("Orbit", false); // old name
                }
            }
            catch { }
        }

        /// <summary>Opens SoundCloud's own sign-in / sign-up page; the account is added as soon as you're signed in.</summary>
        public async void StartSignIn()
        {
            if (loginWin != null) { try { loginWin.Activate(); } catch { } return; }
            try
            {
                loginWin = new LoginWindow(api);
                loginWin.Show();
                loginWin.Activate();
                var acct = await loginWin.Result;
                AddOrUpdateAccount(acct);
            }
            catch (OperationCanceledException) { } // window closed without signing in
            catch (Exception ex)
            {
                Store.Log("sign-in", ex);
                ShowToast(ex.Message);
                MessageBox.Show(ex.Message, "KaruCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                loginWin = null;
                settingsWin?.Refresh();
            }
        }

        void AddOrUpdateAccount(Account acct)
        {
            var existing = acct.UserId == 0 ? null : cfg.Accounts.FirstOrDefault(a => a.UserId == acct.UserId);
            if (existing != null)
            {
                existing.AccessTokenEnc = acct.AccessTokenEnc; // signed in again: keep its tabs and name
                acct = existing;
            }
            else cfg.Accounts.Add(acct);
            Store.Save("config.json", cfg);
            SwitchAccount(acct.Id);
            ShowFromTray();
        }

        async Task RefreshLikedIdsAsync()
        {
            var acct = api.Active;
            if (acct == null) return;
            string id = acct.Id;
            try
            {
                var set = await api.GetLikedIdsAsync();
                if (cfg.Current != id || set == null) return;
                likedIds.Clear(); // the account is the source of truth (also picks up unlikes made elsewhere)
                foreach (var x in set) likedIds.Add(x);
                foreach (var kv in likeTouched.ToList())
                {
                    if (kv.Value.At < DateTime.UtcNow.AddSeconds(-60)) { likeTouched.Remove(kv.Key); continue; }
                    if (kv.Value.On) likedIds.Add(kv.Key); else likedIds.Remove(kv.Key);
                }
                var c = current;
                if (c != null) { c.UserFavorite = likedIds.Contains(c.Id); UpdateTransport(); }
                RefreshAllFlags();
                await RefreshRepostedIdsAsync(id);
            }
            catch { }
        }

        public void SwitchAccount(string id)
        {
            if (cfg.Accounts.All(a => a.Id != id)) return;
            historyFails = 0;
            api.ResetHistory();
            cfg.Current = id;
            Store.Save("config.json", cfg);
            EnsureDefaultTabs(id);
            RefreshAccountButton();
            var last = tabs.Where(t => t.Acct == id).OrderByDescending(t => t.LastActive).FirstOrDefault();
            if (last != null) ActivateTab(last);
            likedIds = new HashSet<long>(); likeTouched.Clear();
            repostedIds = new HashSet<long>();
            followState.Clear(); followTouched.Clear(); followSynced = false; followSync = null;
            lastReported = 0; lastReportedAt = DateTime.MinValue; // a track that just played on the old account must still be reportable on this one
            writeBusy.Clear(); // keys are account-prefixed already (see above); this just drops leftovers from the old account so a like here isn't blocked by one
            _ = RefreshLikedIdsAsync();
            _ = EnsureFollowSyncAsync(true);
            settingsWin?.Refresh();
        }

        public void RenameAccount(string id, string name)
        {
            var a = cfg.Accounts.FirstOrDefault(x => x.Id == id);
            name = (name ?? "").Trim();
            if (a == null || name.Length == 0 || a.Name == name) return;
            a.Name = name.Length > 32 ? name.Substring(0, 32) : name;
            Store.Save("config.json", cfg);
            RefreshAccountButton();
            settingsWin?.Refresh();
        }

        public void Disconnect(string id)
        {
            var a = cfg.Accounts.FirstOrDefault(x => x.Id == id);
            if (a == null) return;
            tabs.RemoveAll(t => t.Acct == id);
            cfg.Accounts.Remove(a);
            if (cfg.Current == id)
            {
                cfg.Current = cfg.Accounts.FirstOrDefault()?.Id ?? "";
                player.Stop(); current = null; queue.Clear(); qi = -1;
                UpdateNowPlayingUi(); UpdatePresence();
            }
            Store.Save("config.json", cfg);
            if (cfg.Current.Length > 0) SwitchAccount(cfg.Current);
            else { active = null; list.ItemsSource = null; RefreshAccountButton(); RefreshTabs(); ShowMessage(null); }
            QueueSave();
            settingsWin?.Refresh();
        }

        public void OpenSettings()
        {
            if (settingsWin != null) { settingsWin.Show(); settingsWin.Activate(); return; }
            settingsWin = new SettingsWindow(this);
            settingsWin.Show();
        }

        public void SettingsClosed(SettingsWindow w) { if (settingsWin == w) settingsWin = null; }

        async Task OnFirstShownAsync()
        {
            if (inited) return;
            inited = true;
            if (active != null && !active.Loaded) await LoadAsync(active, false);
            else ShowMessage(active);
            if (cfg.Accounts.Count == 0) StartSignIn(); // first run: go straight to SoundCloud's sign-in page
            else
            {
                _ = RefreshLikedIdsAsync();
                // one sync at a time: if SoundCloud blocks plain reads, both would queue on the hidden browser at launch
                _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.InvokeAsync(() => { _ = EnsureFollowSyncAsync(); }), TaskScheduler.Default);
            }
        }

        // ================= tray / background =================
        void BuildTray()
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico")!)
                tray.Icon = new System.Drawing.Icon(st);
            tray.Text = "KaruCloud";
            tray.Visible = true;
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) ToggleWindow();
                else if (e.Button == WinForms.MouseButtons.Right) ShowFlyout();
            };
        }

        void ShowFlyout()
        {
            try { flyout?.Close(); } catch { }
            flyout = new TrayFlyout(this, GetTrayInfo(), WinForms.Cursor.Position);
            flyout.Show();
        }

        public TrayInfo GetTrayInfo() => new TrayInfo
        {
            Np = current == null ? null : new NowPlaying { Title = current.Title, Artist = current.ArtistName, Playing = player.IsPlaying },
            Accounts = cfg.Accounts.ToList(),
            Current = cfg.Current,
            Volume = cfg.Settings.Volume,
            WindowVisible = IsVisible && WindowState != WindowState.Minimized,
        };

        public void TrayMedia(string key)
        {
            if (key == "prev") Prev();
            else if (key == "next") Next();
            else TogglePlay();
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
        }

        void HideToTray()
        {
            Hide();
            // Give back the app's own memory while it sits in the tray.
            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
            });
        }

        public void ToggleWindow()
        {
            if (IsVisible && WindowState != WindowState.Minimized) HideToTray(); else ShowFromTray();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            bool max = WindowState == WindowState.Maximized;
            outer.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            ApplyRadius(max ? 0 : Radius);
            maxText.Text = max ? "\uE923" : "\uE922";
            // Hiding from inside the state-change callback can fault; do it right after instead.
            if (WindowState == WindowState.Minimized && cfg.Settings.MinimizeToTray)
                Dispatcher.BeginInvoke(new Action(() => { if (WindowState == WindowState.Minimized) HideToTray(); }));
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!quitting && cfg.Settings.CloseToTray) { e.Cancel = true; HideToTray(); return; }
            if (!quitting) { e.Cancel = true; ExitApp(); return; } // window X with "close to tray" off: quit right away, no window teardown
            base.OnClosing(e);
        }

        bool exitStarted;

        public void ExitApp()
        {
            if (exitStarted) return;
            exitStarted = true;
            quitting = true;
            // Vanish at once: the user should never watch the app "think" about closing.
            try { Hide(); } catch { }
            try { settingsWin?.Hide(); webPanel?.Hide(); } catch { }
            try { tray.Visible = false; } catch { }
            try { WriteTabs(); Store.Save("config.json", cfg); } catch { }

            // Plays still waiting for the next batch get at most 1.5 s, and only if SoundCloud isn't refusing us anyway.
            // Everything else (audio device, Discord pipe, windows) is released by the OS when the process ends;
            // tearing WPF windows down one by one is what made closing slow and logged the "ReleaseOnChannel" error.
            var t = Task.Run(async () =>
            {
                try { rpc.Dispose(); } catch { }
                try { if (api.HasPendingHistory) await Task.WhenAny(api.FlushHistoryAsync(), Task.Delay(1500)); } catch { }
            });
            try { t.Wait(1600); } catch { }
            Environment.Exit(0);
        }
    }
}
