using System;
using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KaruCloud
{
    /// <summary>
    /// Sends a request from inside a real (hidden) soundcloud.com page, signed in with your session token.
    /// SoundCloud's bot protection answers 403 to like / repost / follow / history writes that come from a plain HTTP client,
    /// but accepts the same request when the website's own page makes it. It is only started the first time a write is refused,
    /// and it is closed again after 25 idle seconds, so nothing runs while you just listen.
    /// While it is open it runs in a low-memory mode (no GPU process, one renderer, no background networking).
    /// </summary>
    public sealed class WebBridge
    {
                static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(8); // close the hidden window quickly once nothing is pending

        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, TaskCompletionSource<(int, string)>> pending = new Dictionary<string, TaskCompletionSource<(int, string)>>();
        Window? host;
        WebView2? web;
        DispatcherTimer? idle;
        string loadedFor = "";
        DateTime checkCooldown = DateTime.MinValue; // after a check that wasn't passed, don't open the window again for a while

        /// <summary>Raised when the app should show a short message (the security check window is about to open).</summary>
        public event Action<string>? Notice;

        public async Task<(int Status, string Body)> FetchAsync(string method, string url, string? jsonBody, string accountId, string token, CancellationToken ct, int maxChars = 2000)
        {
            await gate.WaitAsync(ct);
            try { return await OnUi(() => RunAsync(method, url, jsonBody, accountId, token, ct, maxChars)); }
            finally { gate.Release(); }
        }

        static Task<T> OnUi<T>(Func<Task<T>> f)
        {
            var d = Application.Current?.Dispatcher ?? throw new InvalidOperationException("The app window isn't ready yet.");
            return d.InvokeAsync(f).Task.Unwrap();
        }

        async Task<(int, string)> RunAsync(string method, string url, string? jsonBody, string accountId, string token, CancellationToken ct, int maxChars)
        {
            var r = await ExecAsync(method, url, jsonBody, accountId, token, ct, maxChars);
            if (r.Item1 != 403 || !IsCaptcha(r.Item2, out string cUrl) || web?.CoreWebView2 == null || host == null) return r;
            if (DateTime.UtcNow < checkCooldown) return r;

            // SoundCloud's bot protection (DataDome) wants a human check. Show it once in a normal window; the answer is kept in the
            // browser profile, so later likes / reposts go straight through. Then the same request is repeated.
            //  - "t=fe" links are a normal puzzle: show that page.
            //  - "t=bv" links are a hard block page with nothing to solve. Showing it again is pointless, so the real soundcloud.com
            //    page is opened instead (a real page visit is what gets a fresh clearance cookie), and the window stays until you close it.
            bool hard = cUrl.Length == 0 || cUrl.IndexOf("t=bv", StringComparison.OrdinalIgnoreCase) >= 0;
            // A hard block page has nothing to solve, and showing soundcloud.com's home page for minutes only leaves you on a normal page.
            // Hand back the refusal at once: the caller then presses the real button on the song's own page, which is what SoundCloud accepts.
            if (hard) return r;
            var w = host;
            var core = web.CoreWebView2;
            bool closed = false;
            void OnClosing(object? s, CancelEventArgs e) { e.Cancel = true; closed = true; }
            w.Closing += OnClosing;
            idle?.Stop();
            try
            {
                Notice?.Invoke(hard
                    ? "SoundCloud is blocking automatic requests from this connection. Browse soundcloud.com in the window that opened, then close it."
                    : "SoundCloud wants a quick security check. Finish it in the window that just opened.");
                for (int round = 0; round < 3 && !closed; round++)
                {
                    ShowHost(w);
                    await NavigateAsync(hard ? "https://soundcloud.com/" : cUrl);
                    string last = await DataDomeAsync();
                    var until = DateTime.UtcNow.AddMinutes(hard ? 4 : 3);
                    bool moved = false;
                    while (!closed && DateTime.UtcNow < until)
                    {
                        await Task.Delay(1000);
                        string now = await DataDomeAsync();
                        bool away = (core.Source ?? "").IndexOf("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) < 0;
                        if (!hard && (now != last || away)) { moved = true; break; }
                    }
                    if (hard) moved = true; // the person looked around and closed the window (or time ran out): try the request again
                    if (!moved) { checkCooldown = DateTime.UtcNow.AddMinutes(10); break; }
                    HideHost(w);
                    await Task.Delay(500);
                    loadedFor = ""; // EnsureAsync reloads soundcloud.com so the page picks up the new cookie
                    r = await ExecAsync(method, url, jsonBody, accountId, token, ct, maxChars);
                    if (r.Item1 != 403 || !IsCaptcha(r.Item2, out cUrl)) return r;
                    hard = cUrl.Length == 0 || cUrl.IndexOf("t=bv", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hard) break; // still blocked after a real visit: the connection itself is flagged, don't loop
                }
            }
            finally
            {
                checkCooldown = DateTime.UtcNow.AddMinutes(closed ? 10 : 5);
                w.Closing -= OnClosing;
                HideHost(w);
                idle?.Stop(); idle?.Start();
            }
            return r;
        }

        /// <summary>
        /// Opens soundcloud.com in the bridge's own window (signed in) so you can pass SoundCloud's check by hand.
        /// Whatever clearance it gives is stored in the shared browser profile and used by every later like / repost / sync.
        /// Returns when the window is closed.
        /// </summary>
        public Task OpenCheckAsync(string accountId, string token) => OnUi(async () =>
        {
            await gate.WaitAsync();
            try
            {
                await EnsureAsync(accountId, token);
                var w = host!;
                bool closed = false;
                void OnClosing(object? s, CancelEventArgs e) { e.Cancel = true; closed = true; }
                w.Closing += OnClosing;
                idle?.Stop();
                try
                {
                    ShowHost(w);
                    await NavigateAsync("https://soundcloud.com/");
                    var until = DateTime.UtcNow.AddMinutes(10);
                    while (!closed && DateTime.UtcNow < until) await Task.Delay(500);
                }
                finally
                {
                    w.Closing -= OnClosing;
                    HideHost(w);
                    checkCooldown = DateTime.MinValue;
                    loadedFor = "";
                    idle?.Stop(); idle?.Start();
                }
            }
            finally { gate.Release(); }
            return true;
        });

        async Task<(int, string)> ExecAsync(string method, string url, string? jsonBody, string accountId, string token, CancellationToken ct, int maxChars = 2000)
        {
            await EnsureAsync(accountId, token);
            return await RequestAsync(new { op = "fetch", method, url, body = jsonBody, token, max = maxChars }, TimeSpan.FromSeconds(25), ct);
        }

        /// <summary>
        /// One request to the preload script in the page and one answer back, both through WebView2's own message channel
        /// (PostWebMessageAsJson / chrome.webview.postMessage): no script text is built by string concatenation.
        /// The answer always has the same shape: { id, ok, status, data } or { id, ok:false, status, error }.
        /// </summary>
        async Task<(int, string)> RequestAsync(object request, TimeSpan timeout, CancellationToken ct)
        {
            idle?.Stop(); // the idle timer must not run while a request is waiting (it used to close the window mid-request: "closed")
            string id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            try
            {
                var node = JsonSerializer.SerializeToNode(request)!.AsObject();
                node["id"] = id;
                web!.CoreWebView2.PostWebMessageAsJson(node.ToJsonString());
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
                if (done != tcs.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    return (0, "timed out");
                }
                return await tcs.Task;
            }
            finally { pending.Remove(id); idle?.Stop(); idle?.Start(); }
        }

        static bool IsCaptcha(string body, out string url)
        {
            url = "";
            if (body.IndexOf("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) < 0) return false;
            try
            {
                using var d = JsonDocument.Parse(body);
                if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    url = u.GetString() ?? "";
            }
            catch { }
            if (url.Length == 0)
            {
                var m = Regex.Match(body, "https://geo\\.captcha-delivery\\.com/[^\"\\\\\\s]+");
                if (m.Success) url = m.Value;
            }
            return true;
        }

        async Task<string> DataDomeAsync()
        {
            try
            {
                var list = await web!.CoreWebView2.CookieManager.GetCookiesAsync("https://soundcloud.com");
                foreach (var c in list) if (c.Name == "datadome") return c.Value ?? "";
            }
            catch { }
            return "";
        }

        async Task NavigateAsync(string url)
        {
            var core = web!.CoreWebView2;
            var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) { nav.TrySetResult(e.IsSuccess); }
            core.NavigationCompleted += Done;
            try
            {
                core.Navigate(url);
                await Task.WhenAny(nav.Task, Task.Delay(TimeSpan.FromSeconds(25)));
            }
            finally { core.NavigationCompleted -= Done; }
        }

        static void ShowHost(Window w)
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                w.WindowStyle = WindowStyle.SingleBorderWindow;
                w.ResizeMode = ResizeMode.CanResize;
                w.Title = "KaruCloud - SoundCloud security check";
                w.Focusable = true;
                w.Width = 520; w.Height = 720;
                w.Left = wa.Left + Math.Max(0, (wa.Width - w.Width) / 2);
                w.Top = wa.Top + Math.Max(0, (wa.Height - w.Height) / 2);
                w.ShowInTaskbar = true;
                w.Topmost = true;
                w.Activate();
            }
            catch { }
        }

        static void HideHost(Window w)
        {
            try
            {
                w.Topmost = false;
                w.ShowInTaskbar = false;
                w.WindowStyle = WindowStyle.None;
                w.ResizeMode = ResizeMode.NoResize;
                w.Width = 1280; w.Height = 900; w.Left = -10000; w.Top = -10000;
            }
            catch { }
        }

        async Task EnsureAsync(string accountId, string token)
        {
            idle?.Stop();
            if (web == null)
            {
                var w = new Window
                {
                    Width = 1280, Height = 900, Left = -10000, Top = -10000,
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false, ShowActivated = false, Focusable = false,
                };
                var v = new WebView2();
                w.Content = v;
                w.Show(); // off-screen: WebView2 needs a real window handle to run
                try
                {
                    var env = await WebHost.EnvAsync(); // the same low-memory environment the notifications panel uses
                    await v.EnsureCoreWebView2Async(env);
                    host = w; web = v;
                }
                catch (Exception ex)
                {
                    WebHost.Reset();
                    try { v.Dispose(); } catch { }
                    try { w.Close(); } catch { }
                    Close();
                    throw;
                }

                var core = web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.IsMuted = true;
                try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
                try { WebChrome.BlockHeavy(core, await WebHost.EnvAsync()); } catch { }
                core.WebMessageReceived += OnMessage;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(Preload);
                loadedFor = "";
                idle = new DispatcherTimer { Interval = IdleClose };
                idle.Tick += (_, _) => Close();
            }

            if (loadedFor != accountId)
            {
                var core = web!.CoreWebView2;
                WebHost.SetSession(core, token);

                var nav = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    core.NavigationCompleted -= Done;
                    nav.TrySetResult(e.IsSuccess);
                }
                core.NavigationCompleted += Done;
                core.Navigate("https://soundcloud.com/");
                await Task.WhenAny(nav.Task, Task.Delay(TimeSpan.FromSeconds(25)));
                // the bot check hands out its cookie a moment after the page loads: requests sent before that are refused.
                // Wait for the cookie itself (up to ~7s) instead of a fixed pause, then give the page's scripts a short moment.
                for (int i = 0; i < 35 && (await DataDomeAsync()).Length == 0; i++) await Task.Delay(200);
                await Task.Delay(600);
                loadedFor = accountId;
            }
        }

        void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var r = doc.RootElement;
                string id = r.GetProperty("id").GetString() ?? "";
                bool ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                int status = r.TryGetProperty("status", out var st) && st.TryGetInt32(out int sv) ? sv : (ok ? 200 : 0);
                string body = "";
                if (r.TryGetProperty(ok ? "data" : "error", out var b) && b.ValueKind == JsonValueKind.String) body = b.GetString() ?? "";
                if (pending.TryGetValue(id, out var tcs)) tcs.TrySetResult((status, body));
            }
            catch { }
        }

        /// <summary>
        /// Compiled into the exe (not read from a file next to it, so it can't be swapped out). Runs in every soundcloud.com page the bridge opens and answers
        /// two requests: "fetch" (an API call made by the page itself) and "ui" (click the real like / repost / follow button, or post a comment).
        /// </summary>
        const string Preload = @"
(function(){
  if(!window.chrome||!chrome.webview||!/(^|\.)soundcloud\.com$/.test(location.hostname)||window.__kc)return;
  window.__kc=1;
  const send=o=>chrome.webview.postMessage(o);
  const sleep=ms=>new Promise(r=>setTimeout(r,ms));
  const SEL={
    like:['.listenEngagement .sc-button-like','.soundActions .sc-button-like','button.sc-button-like','button[title^=""Like"" i]','button[title^=""Unlike"" i]','button[aria-label^=""Like"" i]','button[aria-label^=""Unlike"" i]'],
    repost:['.listenEngagement .sc-button-repost','.soundActions .sc-button-repost','button.sc-button-repost','button[title*=""repost"" i]','button[aria-label*=""repost"" i]'],
    follow:['.userInfoBar button.sc-button-follow','.soundTitle button.sc-button-follow','button.sc-button-follow','button[title^=""Follow"" i]','button[title^=""Unfollow"" i]','button[aria-label^=""Follow"" i]','button[aria-label^=""Unfollow"" i]']};
  const vis=b=>b&&b.offsetParent!==null;
  const find=k=>{for(const s of SEL[k]){let l=[];try{l=[...document.querySelectorAll(s)].filter(vis);}catch(e){}if(l.length)return l[0];}return null;};
  const label=b=>((b.getAttribute('title')||'')+' '+(b.getAttribute('aria-label')||'')).trim().toLowerCase();
  const isOn=b=>b.classList.contains('sc-button-selected')||b.getAttribute('aria-pressed')==='true'||/^(unlike|unrepost|unfollow|remove repost|liked|reposted|following)/.test(label(b));
  const blocked=()=>!!document.querySelector('iframe[src*=""captcha-delivery""]')||/captcha-delivery/.test(document.documentElement.innerHTML.slice(0,4000));
  const dump=()=>[...document.querySelectorAll('button')].filter(vis).slice(0,14).map(b=>(b.className||'').toString().split(' ').slice(0,3).join('.')+'|'+(label(b)||(b.textContent||'').trim().slice(0,12))).join('; ');
  async function doFetch(r){
    try{
      const h={'Authorization':'OAuth '+r.token,'Accept':'application/json; charset=utf-8'};
      if(r.body!=null)h['Content-Type']='application/json';
      const res=await fetch(r.url,{method:r.method,credentials:'include',headers:h,body:r.body!=null?r.body:undefined});
      const t=(await res.text()).slice(0,r.max||2000);
      return res.ok?{ok:true,status:res.status,data:t}:{ok:false,status:res.status,error:t};
    }catch(e){return{ok:false,status:0,error:String(e)};}
  }
  async function doUi(r){
    if(r.kind==='comment'){
      let inp=null;const end=Date.now()+20000;
      while(Date.now()<end&&!(inp=[...document.querySelectorAll('input.commentForm__input,textarea.commentForm__input,input[placeholder*=""comment"" i],textarea[placeholder*=""comment"" i]')].filter(vis)[0]||null))await sleep(400);
      if(!inp)return{ok:false,status:404,error:blocked()?'blocked':'comment box not found'};
      const setter=Object.getOwnPropertyDescriptor(Object.getPrototypeOf(inp),'value').set;
      inp.focus();setter.call(inp,r.text);inp.dispatchEvent(new Event('input',{bubbles:true}));
      await sleep(250);
      const btn=document.querySelector('.commentForm__submit,button[type=submit].commentForm__submit')||(inp.closest('form')?inp.closest('form').querySelector('button[type=submit],button:last-of-type'):null);
      if(btn&&!btn.disabled)btn.click();
      else for(const t of['keydown','keypress','keyup'])inp.dispatchEvent(new KeyboardEvent(t,{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));
      const e2=Date.now()+8000;
      while(Date.now()<e2){await sleep(300);if(!inp.value)return{ok:true,status:200,data:'done'};}
      return{ok:false,status:409,error:'the comment was not accepted'};
    }
    const gone=()=>/can.t find that|page not found|doesn.t exist|no longer available/i.test((document.title||'')+' '+((document.querySelector('h1,h2')||{}).textContent||''));
    const viaMore=async()=>{
      const more=[...document.querySelectorAll('.listenEngagement .sc-button-more,.soundActions .sc-button-more,button.sc-button-more')].filter(vis)[0];
      if(!more)return null;
      more.click();await sleep(450);
      const kw=r.kind==='repost'?/repost/i:r.kind==='follow'?/follow/i:/like/i;
      return [...document.querySelectorAll('[role=menuitem],.dropdownMenu button,.moreActions button,.sc-button-'+r.kind)].filter(vis).find(x=>kw.test(label(x)+' '+(x.textContent||'')))||null;
    };
    let b=null;const t0=Date.now(),end=t0+20000;let triedMore=false;
    while(Date.now()<end&&!(b=find(r.kind))){
      if(!triedMore&&Date.now()-t0>6000){triedMore=true;b=await viaMore();if(b)break;}
      if(Date.now()-t0>4000&&gone())return{ok:false,status:404,error:'this page no longer exists on SoundCloud'};
      await sleep(400);
    }
    if(!b)return{ok:false,status:404,error:blocked()?'blocked':'button not found ['+dump()+']'};
    if(r.mode==='read')return{ok:true,status:200,data:isOn(b)?'on':'off'};
    if(isOn(b)===r.want)return{ok:true,status:200,data:'already'};
    b.click();
    const e2=Date.now()+8000;
    while(Date.now()<e2){await sleep(300);const nb=find(r.kind)||b;if(isOn(nb)===r.want)return{ok:true,status:200,data:'done'};}
    return{ok:false,status:409,error:'the page did not change state'};
  }
  chrome.webview.addEventListener('message',async ev=>{
    const r=ev.data;if(!r||!r.id)return;
    const out=r.op==='ui'?await doUi(r):await doFetch(r);
    out.id=r.id;send(out);
  });
})();";

        /// <summary>
        /// Does a like / repost / follow / comment by driving the real soundcloud.com page (its own scripts send the request, with everything
        /// the site normally adds). Used when the plain API route is refused. If the page can't do it alone (for example a check is showing),
        /// the window is shown so you can finish it by hand; it closes when the state matches or you close it.
        /// </summary>
        public Task<(bool Ok, string Error)> UiActionAsync(string accountId, string token, string pageUrl, string kind, bool want, string text) => OnUi(async () =>
        {
            await gate.WaitAsync();
            try
            {
                await EnsureAsync(accountId, token);
                idle?.Stop();
                await NavigateAsync(pageUrl);
                var r = await RequestAsync(new { op = "ui", kind, want, text, mode = "click" }, TimeSpan.FromSeconds(45), CancellationToken.None);
                if (r.Item1 >= 200 && r.Item1 < 300) { idle?.Start(); return (true, ""); }
                try { Store.Log("page action " + kind + " " + pageUrl + " -> " + r.Item1, new Exception(r.Item2.Length > 700 ? r.Item2.Substring(0, 700) : r.Item2)); } catch { }
                // Don't pop a visible browser window for an ordinary failed click (slow page, button not found, etc.) - that
                // just shows soundcloud.com's heavy JS for no reason. Fail quietly instead; "Fix likes & sync" in the account
                // menu is the one place that opens a window on purpose, for the rare case that really does need a human.
                idle?.Start();
                return (false, r.Item2);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Store.Log("page action", ex); return (false, "The page action failed: " + ex.Message); }
            finally { gate.Release(); }
        });

        /// <summary>A step-by-step report of what the hidden browser can and can't do (shown by "Check sync" in the account menu).</summary>
        public Task<string> DiagnoseAsync(string accountId, string token, string url) => OnUi(async () =>
        {
            var sb = new StringBuilder();
            await gate.WaitAsync();
            try
            {
                try { await EnsureAsync(accountId, token); sb.AppendLine("2. Hidden browser (WebView2): started"); }
                catch (Exception ex) { sb.AppendLine("2. Hidden browser (WebView2): FAILED to start - " + ex.Message); return sb.ToString(); }
                idle?.Stop();
                var core = web!.CoreWebView2;
                string info;
                try { info = await core.ExecuteScriptAsync("location.href+' | visibility='+document.visibilityState+' | helper script='+(window.__kc?'installed':'MISSING')"); }
                catch (Exception ex) { info = "script error: " + ex.Message; }
                sb.AppendLine("3. Page: " + info);
                sb.AppendLine("4. Bot-check cookie (datadome): " + ((await DataDomeAsync()).Length > 0 ? "present" : "MISSING"));
                bool oauth = false;
                try { foreach (var c in await core.CookieManager.GetCookiesAsync("https://soundcloud.com")) if (c.Name == "oauth_token") oauth = true; } catch { }
                sb.AppendLine("5. Session cookie in the hidden browser: " + (oauth ? "present" : "MISSING"));
                var r = await RequestAsync(new { op = "fetch", method = "GET", url, body = (string?)null, token, max = 300 }, TimeSpan.FromSeconds(20), CancellationToken.None);
                string head = r.Item2.Replace("\r", " ").Replace("\n", " ");
                if (head.Length > 160) head = head.Substring(0, 160);
                sb.AppendLine("6. Read /me from inside the page: " + (r.Item1 >= 200 && r.Item1 < 300 ? "HTTP " + r.Item1 + " OK" : r.Item1 == 0 ? "NO ANSWER (" + head + ")" : "HTTP " + r.Item1 + " " + head));
                if (IsCaptcha(r.Item2, out _)) sb.AppendLine("   -> SoundCloud's bot protection is asking for a check (use Fix likes & sync)");
            }
            catch (Exception ex) { sb.AppendLine("Check failed: " + ex.Message); }
            finally { idle?.Stop(); idle?.Start(); gate.Release(); }
            return sb.ToString();
        });

        void Close()
        {
            try { idle?.Stop(); } catch { }
            try { web?.Dispose(); } catch { }
            try { host?.Close(); } catch { }
            web = null; host = null; idle = null; loadedFor = "";
            foreach (var p in pending.Values) p.TrySetResult((0, "closed"));
        }
    }
}
