using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KaruCloud
{
    /// <summary>
    /// SoundCloud's own sign-in / sign-up page in a small window (Google, Apple, Facebook and e-mail all work there).
    /// As soon as you're signed in the window closes by itself and the browser engine is released again.
    /// </summary>
    public sealed class LoginWindow : Window
    {
        // Only the sign-in dialog: once it is on the page, everything behind it is hidden (the dialog and what it holds stay visible).
        const string LoginCss = WebChrome.BaseCss + @"
    html.kc-auth body *{visibility:hidden!important}
    html.kc-auth [data-kc-modal],html.kc-auth [data-kc-modal] *{visibility:visible!important}";

        const string LoginJs = @"
  const tick=()=>{
    try{
      let m=null;
      const f=document.querySelector('iframe[src*=""web-auth""],iframe[src*=""signin""],iframe[class*=""webAuth""]');
      if(f)m=f.closest('.modal,[role=""dialog""],[class*=""webAuth""],[class*=""Modal""]')||f.parentElement;
      else{
        const inp=document.querySelector('input[type=""email""],input[type=""password""],input[name=""username""],input[autocomplete=""username""]');
        if(inp)m=inp.closest('.modal,[role=""dialog""],[class*=""webAuth""],[class*=""Modal""]');
      }
      if(m&&!m.hasAttribute('data-kc-modal')){
        document.querySelectorAll('[data-kc-modal]').forEach(x=>x.removeAttribute('data-kc-modal'));
        m.setAttribute('data-kc-modal','1');
        document.documentElement.classList.add('kc-auth');
        ready();
      }
    }catch(e){}
  };
  setInterval(tick,400);";

        readonly Action hideCover;
        readonly DispatcherTimer coverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

        readonly ScApi api;
        readonly WebView2 web = new WebView2();
        readonly TaskCompletionSource<Account> tcs = new TaskCompletionSource<Account>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        string lastRejected = "";
        bool busy, done;

        public Task<Account> Result => tcs.Task;

        public LoginWindow(ScApi api)
        {
            this.api = api;
            Title = "Sign in to SoundCloud";
            Width = 440; Height = 660; MinWidth = 360; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WebChrome.FrameWindow(this);
            WebChrome.DarkFrame(this); // dark frame / Windows 11 rounded corners (ignored where unsupported)
            var icon = WebChrome.LoadIcon();
            if (icon != null) Icon = icon;
            web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 18, 18);

            // our own slim title bar and a cover that hides the full website until only the sign-in dialog is left
            var bar = WebChrome.TitleBar(this, icon, "Sign in to SoundCloud", out _);
            var cover = WebChrome.Cover(out hideCover);
            var area = new Grid();
            area.Children.Add(web); area.Children.Add(cover);
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(bar, 0); Grid.SetRow(area, 1);
            root.Children.Add(bar); root.Children.Add(area);
            Content = new Border { BorderBrush = Ui.B("#2a2a2a"), BorderThickness = new Thickness(1), Child = root };

            Loaded += async (_, _) => await InitAsync();
            poll.Tick += async (_, _) => await CheckAsync();
            coverTimer.Tick += (_, _) => { coverTimer.Stop(); hideCover(); }; // never leave the cover up if the page stays quiet
            Closed += (_, _) =>
            {
                poll.Stop(); coverTimer.Stop();
                try { cts.Cancel(); } catch { }
                if (!done) { done = true; tcs.TrySetCanceled(); }
                try { web.Dispose(); } catch { }
            };
        }

        async Task InitAsync()
        {
            try
            {
                coverTimer.Start();
                // Same browser profile as the hidden like / repost / sync page: what SoundCloud's bot check learns about a real person here is reused there.
                var env = await WebHost.EnvAsync();
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;
                WebChrome.ApplyLook(core);
                core.Settings.IsStatusBarEnabled = false;
                core.IsMuted = false; // a spoken captcha must stay possible
                core.Settings.AreDefaultContextMenusEnabled = true; // right-click paste into the password box
                WebChrome.BlockExtras(core, env, blockMedia: false); // Google / Apple / Facebook sign-in popups stay untouched
                core.WebMessageReceived += (_, e) =>
                {
                    try { if (e.TryGetWebMessageAsString() == "kc-ready") { coverTimer.Stop(); hideCover(); } } catch { }
                };
                await core.AddScriptToExecuteOnDocumentCreatedAsync(WebChrome.Skin(LoginCss, LoginJs, autoReady: false));
                await WebHost.ClearSessionAsync(core);
                core.Navigate("https://soundcloud.com/signin");
                poll.Start();
            }
            catch (Exception ex)
            {
                Store.Log("sign-in window", ex);
                Fail(new Exception("Couldn't open the sign-in page. Install the Microsoft Edge WebView2 Runtime (https://go.microsoft.com/fwlink/p/?LinkId=2124703) and try again."));
            }
        }

        void Fail(Exception ex)
        {
            if (!done) { done = true; tcs.TrySetException(ex); }
            try { Close(); } catch { }
        }

        async Task CheckAsync()
        {
            if (busy || done || web.CoreWebView2 == null) return;
            busy = true;
            try
            {
                var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://soundcloud.com");
                var c = cookies.FirstOrDefault(x => x.Name == "oauth_token");
                if (c == null) return;
                string token = Uri.UnescapeDataString(c.Value ?? "").Trim();
                if (token.Length == 0 || token == lastRejected) return;

                var acct = await api.TrySignInAsync(token, cts.Token);
                if (acct == null) { lastRejected = token; return; }

                done = true;
                tcs.TrySetResult(acct);
                await WebHost.ClearSessionAsync(web.CoreWebView2);
                Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Store.Log("sign-in check", ex); } // network hiccup: try again on the next tick
            finally { busy = false; }
        }
    }
}
