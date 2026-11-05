using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KaruCloud
{
    /// <summary>
    /// One shared, low-memory WebView2 environment for everything that needs a real browser page
    /// (the hidden request bridge and the notifications / messages panel). Both use the same profile,
    /// so a security check passed once is remembered by both, and only one browser process group exists.
    /// It is created on demand and disappears again when the last page is closed.
    /// </summary>
    public static class WebHost
    {
        public static string DataDir => Path.Combine(Store.Dir, "WebView2-actions");

        // No background networking / sync / updates, one renderer process, no GPU shader disk cache: as small as WebView2 gets.
        public const string LightArgs = "--renderer-process-limit=1 --disable-background-networking --disable-sync --disable-component-update --no-first-run --disable-gpu-shader-disk-cache --disable-extensions --disable-breakpad --disable-domain-reliability --no-pings --disable-gpu --disable-software-rasterizer --disable-renderer-backgrounding --disable-features=Translate,OptimizationHints,MediaRouter,AutofillServerCommunication,InterestFeedContentSuggestions,CalculateNativeWinOcclusion,BackForwardCache,PrivacySandboxSettings4,Prerender2,NetworkPrediction";

        static Task<CoreWebView2Environment>? env;
        static readonly object gate = new object();

        public static Task<CoreWebView2Environment> EnvAsync()
        {
            lock (gate) return env ??= CreateAsync();
        }

        static async Task<CoreWebView2Environment> CreateAsync()
        {
            CoreWebView2Environment e;
            try
            {
                e = await CoreWebView2Environment.CreateAsync(null, DataDir, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = LightArgs });
            }
            catch
            {
                // An older KaruCloud process may still own this profile with other flags: use plain defaults then.
                try { e = await CoreWebView2Environment.CreateAsync(null, DataDir, null); }
                catch { lock (gate) env = null; throw; }
            }
            // When the browser process has exited (every page closed) the environment is no longer usable: build a fresh one next time.
            try { e.BrowserProcessExited += (_, _) => { lock (gate) env = null; }; } catch { }
            return e;
        }

        /// <summary>Forget the cached environment (used after a start-up failure).</summary>
        public static void Reset() { lock (gate) env = null; }

        /// <summary>Signs the shared browser profile in with the account's session token, the way soundcloud.com stores it.</summary>
        public static void SetSession(CoreWebView2 core, string token)
        {
            if (token.Length == 0) return;
            var cookie = core.CookieManager.CreateCookie("oauth_token", token, ".soundcloud.com", "/");
            cookie.IsSecure = true;
            core.CookieManager.AddOrUpdateCookie(cookie);
        }

        /// <summary>
        /// Forgets the signed-in account in the shared browser profile (so the next sign-in page starts clean) but keeps SoundCloud's bot-check
        /// cookie: that cookie is what lets the hidden like / repost / sync page through after a real person has used the sign-in page.
        /// </summary>
        public static async Task ClearSessionAsync(CoreWebView2 core)
        {
            try
            {
                var keep = new System.Collections.Generic.List<CoreWebView2Cookie>();
                foreach (var c in await core.CookieManager.GetCookiesAsync("https://soundcloud.com"))
                    if (c.Name.StartsWith("datadome", StringComparison.OrdinalIgnoreCase)) keep.Add(CopyCookie(core, c));
                core.CookieManager.DeleteAllCookies();
                foreach (var c in keep) core.CookieManager.AddOrUpdateCookie(c);
            }
            catch { try { core.CookieManager.DeleteAllCookies(); } catch { } }
        }

        static CoreWebView2Cookie CopyCookie(CoreWebView2 core, CoreWebView2Cookie c)
        {
            var n = core.CookieManager.CreateCookie(c.Name, c.Value, c.Domain, c.Path);
            n.Expires = c.Expires;
            n.IsHttpOnly = c.IsHttpOnly;
            n.IsSecure = c.IsSecure;
            n.SameSite = c.SameSite;
            return n;
        }

        /// <summary>Mouse side buttons inside a web page: back / forward through the page's own history.</summary>
        public const string SideButtonScript =
            "window.addEventListener('mouseup',function(e){if(e.button===3){e.preventDefault();history.back();}else if(e.button===4){e.preventDefault();history.forward();}},true);";
    }

    /// <summary>
    /// Pieces shared by the small web windows (sign-in, notifications / messages page): our own title bar, a cover that stays up until
    /// the page has been trimmed down to what it is for, trimmed browser settings, and no trackers or media downloads.
    /// </summary>
    public static class WebChrome
    {
        /// <summary>Hosts that only measure, advertise or ask about cookies. Nothing on soundcloud.com or its sign-in needs them.</summary>
        static readonly string[] Trackers =
        {
            "*://*.google-analytics.com/*", "*://*.googletagmanager.com/*", "*://*.doubleclick.net/*", "*://*.googlesyndication.com/*",
            "*://*.googleadservices.com/*", "*://*.cookielaw.org/*", "*://*.onetrust.com/*", "*://*.sentry.io/*", "*://*.hotjar.com/*",
            "*://*.adsrvr.org/*", "*://*.scorecardresearch.com/*", "*://*.amazon-adsystem.com/*", "*://*.criteo.com/*", "*://*.criteo.net/*",
            "*://*.taboola.com/*", "*://*.outbrain.com/*", "*://*.quantserve.com/*", "*://*.segment.io/*", "*://*.segment.com/*",
            "*://*.branch.io/*", "*://*.app.link/*", "*://*.fullstory.com/*", "*://*.newrelic.com/*", "*://*.nr-data.net/*",
        };

        /// <summary>Thin dark scrollbars and the page background, shared by every skin.</summary>
        public const string BaseCss = @"
    html,body{background:#121212!important;overflow-x:hidden!important}
    .header,.l-header,.playControls,.l-footer,.compactFooter,.announcement,.announcements,#onetrust-consent-sdk,#onetrust-banner-sdk,.sc-cookie-banner,[class*='cookieBanner'],[class*='CookieBanner'],.webiOSBanner,[class*='appBanner']{display:none!important}
    body{padding-top:0!important;padding-bottom:0!important}
    ::-webkit-scrollbar{width:8px;height:8px}
    ::-webkit-scrollbar-track{background:transparent}
    ::-webkit-scrollbar-thumb{background:#3a3a3a;border-radius:4px}
    ::-webkit-scrollbar-thumb:hover{background:#555}
    ::-webkit-scrollbar-corner{background:transparent}
    *{-webkit-tap-highlight-color:transparent}";

        /// <summary>Runs in every page: injects the CSS and tells the window when the page is ready to be shown.</summary>
        public static string Skin(string css, string extraJs = "", bool autoReady = true) => @"
(function(){
  if(window.top!==window)return;
  if(!/(^|\.)soundcloud\.com$/.test(location.hostname))return;
  const css=`" + css.Replace("`", "'") + @"`;
  const put=()=>{if(document.getElementById('kc-skin'))return;const s=document.createElement('style');s.id='kc-skin';s.textContent=css;(document.head||document.documentElement).appendChild(s);};
  put();document.addEventListener('DOMContentLoaded',put);
  new MutationObserver(put).observe(document.documentElement,{childList:true});
  let said=false;const ready=()=>{if(said)return;said=true;try{window.chrome.webview.postMessage('kc-ready');}catch(e){}};
  window.kcReady=ready;
" + extraJs + (autoReady ? @"
  if(document.readyState!=='loading')setTimeout(ready,350);else document.addEventListener('DOMContentLoaded',()=>setTimeout(ready,350));" : "") + @"
})();";

        public static void ApplyLook(CoreWebView2 core)
        {
            // nothing that says "browser": no dev tools, menus, status bar, zoom, swipe navigation, accelerator keys, autofill
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            try { core.IsMuted = true; } catch { }
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
        }

        /// <summary>Trackers and audio / video are answered with an empty 403 (the page never needs them in these windows).</summary>
        public static void BlockExtras(CoreWebView2 core, CoreWebView2Environment env, bool blockMedia = true)
        {
            try
            {
                foreach (var pattern in Trackers) core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
                if (blockMedia) core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
                core.WebResourceRequested += (_, e) =>
                {
                    try { e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", ""); } catch { }
                };
            }
            catch { }
        }

        /// <summary>
        /// For the hidden request bridge: nothing it does needs artwork, fonts or audio / video, so those never download
        /// (the bot-check's own images on captcha-delivery.com are left alone so a check can still be solved).
        /// </summary>
        public static void BlockHeavy(CoreWebView2 core, CoreWebView2Environment env)
        {
            try
            {
                core.AddWebResourceRequestedFilter("*://*.sndcdn.com/*", CoreWebView2WebResourceContext.Image);
                core.AddWebResourceRequestedFilter("*://*.soundcloud.com/*", CoreWebView2WebResourceContext.Image);
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Font);
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
                foreach (var pattern in Trackers) core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    try { e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", ""); } catch { }
                };
            }
            catch { }
        }

        /// <summary>Our own slim title bar: icon, title, close. The window is dragged by it.</summary>
        public static Border TitleBar(Window w, ImageSource? icon, string title, out TextBlock caption)
        {
            var bar = new Grid { Height = 36, Background = Ui.B("#141414") };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            if (icon != null) head.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 9, 0) });
            caption = new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(caption);
            bar.Children.Add(head);
            var close = new Border { Width = 46, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = new TextBlock { Text = "\uE8BB", FontFamily = Ui.Icons, FontSize = 10, Foreground = Ui.B("#e6e6e6"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            Ui.Hover(close, "#E81123");
            close.MouseLeftButtonUp += (_, _) => w.Close();
            Grid.SetColumn(close, 1);
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            bar.Children.Add(close);
            bar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) { try { w.DragMove(); } catch { } } };
            return new Border { Child = bar };
        }

        public static void FrameWindow(Window w)
        {
            w.WindowStyle = WindowStyle.None;
            w.ResizeMode = ResizeMode.CanResizeWithGrip;
            w.Background = Ui.B("#121212");
            System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false,
            });
        }

        /// <summary>The page is built behind this cover and only shown once it has been trimmed (no flash of the full website).</summary>
        public static Grid Cover(out Action hide)
        {
            var dots = new TextBlock { Text = "Loading…", Foreground = Ui.B("#8a8a8a"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cover = new Grid { Background = Ui.B("#121212") };
            cover.Children.Add(dots);
            hide = () => cover.Visibility = Visibility.Collapsed;
            return cover;
        }

        public static BitmapFrame? LoadIcon()
        {
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico")!;
                return BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { return null; }
        }

        public static void DarkFrame(Window w)
        {
            w.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int dark = 1, round = 2;
                try { WebPanel.Dwm(h, 20, ref dark); WebPanel.Dwm(h, 33, ref round); } catch { }
            };
        }
    }

    /// <summary>
    /// Fallback for the bell / envelope when SoundCloud won't hand over the plain list: the real notifications / messages page in a small
    /// window next to the header, trimmed to just that list (no website header, footer, side column, player bar, cookie banner or trackers).
    /// A cover hides the page until it has been trimmed. The browser is created when the window opens and thrown away when it closes.
    /// </summary>
    public sealed class WebPanel : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        internal static int Dwm(IntPtr hwnd, int attr, ref int value) => DwmSetWindowAttribute(hwnd, attr, ref value, 4);

        // Only the list: no side column, no upsell, content as wide as the window.
        const string PanelCss = WebChrome.BaseCss + @"
    .l-sidebar-right,.l-sidebar-left,.sidebarContent,.l-about-top,.upsell,[class*='goPlus'],[class*='GoPlus']{display:none!important}
    .l-container,.l-content,.g-content,#content{margin-top:0!important;padding-top:8px!important;max-width:100%!important}
    .l-main,.l-fluid-fixed{width:100%!important;max-width:100%!important;margin-left:0!important;margin-right:0!important}
    html,body,#app,#content,.l-container,.l-content{min-width:0!important}";

        // Hides the website's own top bar, banners and player bar without relying on their class names (those change): anything fixed or
        // sticky goes, and so does the small "Now available / Artist Pro" banner. The cover comes down once that has run a few times.
        const string PanelJs = @"
  const hideBits=()=>{
    try{
      for(const el of document.querySelectorAll('body *')){
        if(el.dataset&&el.dataset.kcHid)continue;
        if(el.closest('[role=""dialog""],[aria-modal=""true""]'))continue;
        const p=getComputedStyle(el).position;
        if(p==='fixed'||p==='sticky'){el.dataset.kcHid='1';el.style.setProperty('display','none','important');}
      }
      for(const el of document.querySelectorAll('div,section,aside')){
        if((el.dataset&&el.dataset.kcHid)||el.children.length>14)continue;
        const t=el.textContent||'';
        if(t.length<220&&/Now available:|Get heard by|Artist Pro|Try Artist|Go\+/i.test(t)&&!el.querySelector('h1,h2')){el.dataset.kcHid='1';el.style.setProperty('display','none','important');}
      }
    }catch(e){}
  };
  let kcN=0;
  const kcIv=setInterval(()=>{hideBits();kcN++;if(kcN===3)ready();if(kcN>40)clearInterval(kcIv);},500);";

        readonly WebView2 web = new WebView2();
        readonly string url, token;
        readonly Action hideCover;
        readonly System.Windows.Threading.DispatcherTimer coverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

        public WebPanel(string title, string url, string token, bool wide = false)
        {
            this.url = url; this.token = token;
            Title = "KaruCloud - " + title;
            Width = wide ? 600 : 470; Height = 680; MinWidth = 380; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WebChrome.FrameWindow(this);
            WebChrome.DarkFrame(this);
            var icon = WebChrome.LoadIcon();
            if (icon != null) Icon = icon;
            web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 18, 18);

            var bar = WebChrome.TitleBar(this, icon, title, out _);
            var cover = WebChrome.Cover(out hideCover);
            var area = new Grid();
            area.Children.Add(web); area.Children.Add(cover);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(bar, 0); Grid.SetRow(area, 1);
            root.Children.Add(bar); root.Children.Add(area);
            Content = new Border { BorderBrush = Ui.B("#2a2a2a"), BorderThickness = new Thickness(1), Child = root };

            // sits under the header's icons, at the right edge of the main window
            SourceInitialized += (_, _) => Place();
            Loaded += async (_, _) => await InitAsync();
            coverTimer.Tick += (_, _) => { coverTimer.Stop(); hideCover(); }; // never leave the cover up if the page stays quiet
            Closed += (_, _) => { coverTimer.Stop(); try { web.Dispose(); } catch { } }; // releases the browser again
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); }
            };
        }

        void Place()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double ol = wa.Left, ot = wa.Top, ow = wa.Width;
                if (Owner != null && Owner.WindowState != WindowState.Maximized) { ol = Owner.Left; ot = Owner.Top; ow = Owner.ActualWidth; }
                Left = Math.Max(wa.Left, Math.Min(ol + ow - Width - 16, wa.Right - Width));
                Top = Math.Max(wa.Top, Math.Min(ot + 54, wa.Bottom - Height));
            }
            catch { }
        }

        async Task InitAsync()
        {
            try
            {
                coverTimer.Start();
                var env = await WebHost.EnvAsync();
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;
                WebChrome.ApplyLook(core);
                WebChrome.BlockExtras(core, env);
                WebHost.SetSession(core, token);
                core.WebMessageReceived += (_, e) =>
                {
                    try { if (e.TryGetWebMessageAsString() == "kc-ready") { coverTimer.Stop(); hideCover(); } } catch { }
                };
                await core.AddScriptToExecuteOnDocumentCreatedAsync(WebChrome.Skin(PanelCss, PanelJs, autoReady: false));
                await core.AddScriptToExecuteOnDocumentCreatedAsync(WebHost.SideButtonScript);
                // links that want a new window stay in this panel (never the default browser)
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    try { if (e.Uri.StartsWith("https://soundcloud.com/", StringComparison.OrdinalIgnoreCase)) core.Navigate(e.Uri); } catch { }
                };
                core.Navigate(url);
            }
            catch (Exception ex)
            {
                WebHost.Reset();
                Store.Log("web panel", ex);
                MessageBox.Show(this, "Couldn't open this page. Install the Microsoft Edge WebView2 Runtime (https://go.microsoft.com/fwlink/p/?LinkId=2124703) and try again.\n\n" + ex.Message,
                    "KaruCloud", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }
    }
}
