using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KaruCloud
{
    public sealed class ApiException : Exception
    {
        public int Status { get; }
        public int RetryAfter { get; }
        public ApiException(int status, string message, int retryAfter = 0) : base(message)
        {
            Status = status; RetryAfter = retryAfter;
        }
    }

    /// <summary>
    /// Talks to SoundCloud the same way soundcloud.com does (api-v2): you sign in on SoundCloud's own page,
    /// KaruCloud keeps the session token and borrows the website's public web key. No developer API app needed.
    /// </summary>
    public sealed class ScApi
    {
        const string Base = "https://api-v2.soundcloud.com";
        const string Web = "https://soundcloud.com";
        // The website sends likes/reposts/follows to this host (api-v2 is behind bot protection that answers 403 to non-browser writes).
        const string V1 = "https://api.soundcloud.com";

        readonly HttpClient http;
        readonly Config cfg;
        readonly SemaphoreSlim idLock = new SemaphoreSlim(1, 1);
        string clientId = "";
        volatile bool v2First; // remembers which host accepted the last write
        readonly WebBridge bridge = new WebBridge();
        // Neither flag is set anywhere in the automatic write path any more (WriteAsync is plain HTTP only - see below).
        // They stay false for the life of the app unless something explicitly opts into the hidden-browser / page-click
        // recovery below, which nothing currently does. Kept so that recovery path works if it's ever wired to a manual action.
        volatile bool bridgeFirst;
        volatile bool uiFirst;
        DateTime hardBlockUntil = DateTime.MinValue;       // SoundCloud's hard block page was shown: fail new writes at once for a short while instead of retrying every route
        DateTime plainBlockedUntil = DateTime.MinValue;   // bot protection answered the plain client with a block page: don't keep hammering it
        DateTime readsViaWebUntil = DateTime.MinValue;    // plain GETs were blocked, so reads go through the web page for a while
        DateTime lastIdRefresh = DateTime.MinValue;       // the web key is re-read at most once per IdRefreshGap, so a refusal can't turn into a download loop
        static readonly TimeSpan IdRefreshGap = TimeSpan.FromMinutes(10);

        /// <summary>
        /// A block is already known to be active: build the user-ready refusal from it (no request is sent). The hard / soft
        /// wording tells the person whether waiting helps, so "Fix likes & sync" isn't suggested when it can't do anything.
        /// </summary>
        ApiException Blocked()
        {
            bool hard = DateTime.UtcNow < hardBlockUntil;
            string until = (hard ? hardBlockUntil : plainBlockedUntil).ToLocalTime().ToString("HH:mm:ss");
            Store.Log("write stopped", new Exception("bot protection active (" + (hard ? "hard" : "soft") + ") until " + until + " - request not sent"));
            return new ApiException(403, hard
                ? "SoundCloud's bot protection has hard-blocked this connection (nothing to solve - \"access temporarily restricted\"). KaruCloud stopped sending likes / reposts / comments until " + until + ". Waiting will not fix a hard block: change network or turn off any VPN / proxy."
                : "SoundCloud's bot protection is blocking likes / reposts / comments from this network right now. KaruCloud stopped sending requests until " + until + " so the block can clear. Turn off any VPN / proxy, or use the \"...\" menu -> \"Fix likes & sync\" to browse soundcloud.com once by hand. Nothing was changed.");
        }

        // Listening-history writes are queued while the hidden browser would be needed, and sent in one go
        // (4 plays, 5 minutes, the next like/repost, or on exit) so the browser starts rarely instead of once per song.
        const int HistoryBatch = 4;
        static readonly TimeSpan HistoryDelay = TimeSpan.FromMinutes(5);
        readonly object historyLock = new object();
        readonly Queue<long> historyQueue = new Queue<long>();
        readonly Timer historyTimer;
        int flushing;
        int flushFails;
        /// <summary>Raised (on a background thread) after queued plays have reached your SoundCloud history.</summary>
        public event Action? HistorySynced;
        /// <summary>Raised when the app should tell you something (for example: SoundCloud wants a quick human check).</summary>
        public event Action<string>? Notice;
        public bool HasPendingHistory { get { lock (historyLock) return historyQueue.Count > 0; } }

        public ScApi(Config cfg, Action save)
        {
            this.cfg = cfg;
            bridge.Notice += m => Notice?.Invoke(m);
            historyTimer = new Timer(_ => { _ = FlushHistoryAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            })
            { Timeout = TimeSpan.FromSeconds(40), DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }

        public Account? Active => cfg.Accounts.FirstOrDefault(a => a.Id == cfg.Current);

        /// <summary>The signed-in account's session token (for pages shown in a web view). Empty when signed out.</summary>
        public string ActiveToken { get { var a = Active; return a == null ? "" : Secrets.Unprotect(a.AccessTokenEnc); } }

        /// <summary>Opens soundcloud.com in a window so you can pass SoundCloud's check by hand; the clearance is reused by likes / reposts / sync.</summary>
        public Task OpenSecurityCheckAsync()
        {
            var a = Active;
            if (a == null) throw new ApiException(401, "Sign in to SoundCloud first (the account button, top left).");
            string token = Secrets.Unprotect(a.AccessTokenEnc);
            if (token.Length == 0) throw new ApiException(401, "This account is signed out. Sign in again from Settings.");
            hardBlockUntil = DateTime.MinValue; // the person is about to browse soundcloud.com by hand: let the next click try again
            plainBlockedUntil = DateTime.MinValue;
            return bridge.OpenCheckAsync(a.Id, token);
        }

        /// <summary>Plain-text report for "Check sync": does the token work, does the plain client work, does the hidden browser work.</summary>
        public async Task<string> DiagnoseSyncAsync()
        {
            var a = Active;
            if (a == null) return "Not signed in.";
            string token = Secrets.Unprotect(a.AccessTokenEnc);
            var sb = new StringBuilder();
            sb.AppendLine("KaruCloud sync check");
            sb.AppendLine("Account: " + a.Name + " (id " + a.UserId + "), token stored: " + (token.Length > 0 ? "yes" : "NO"));
            try { await SendAsync(HttpMethod.Get, "/me", a, CancellationToken.None, false); sb.AppendLine("1. Plain read of /me: OK"); }
            catch (Exception ex) { string m = ex.Message; sb.AppendLine("1. Plain read of /me: FAILED - " + (m.Length > 140 ? m.Substring(0, 140) : m)); }
            try
            {
                string cid = await ClientIdAsync(false, CancellationToken.None);
                sb.Append(await bridge.DiagnoseAsync(a.Id, token, AddQuery(AddQuery(Base + "/me", "client_id", cid), "app_locale", "en")));
            }
            catch (Exception ex) { sb.AppendLine("2. Hidden browser check failed: " + ex.Message); }
            sb.AppendLine("Plain-write route that worked last: " + (bridgeFirst ? "hidden browser" : "plain HTTP") + ", page-click route: " + (uiFirst ? "in use" : "not needed yet"));
            return sb.ToString();
        }

        static string Abs(string urlOrPath) =>
            urlOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? urlOrPath : Base + urlOrPath;

        static string AddQuery(string url, string key, string value)
        {
            if (url.Contains(key + "=", StringComparison.Ordinal)) return url;
            return url + (url.Contains('?') ? "&" : "?") + key + "=" + Uri.EscapeDataString(value);
        }

        // ---------- the website's public web key ----------
        async Task<string> ClientIdAsync(bool refresh, CancellationToken ct)
        {
            if (!refresh && clientId.Length > 0) return clientId;
            await idLock.WaitAsync(ct);
            try
            {
                if (!refresh && clientId.Length > 0) return clientId;
                string html = await http.GetStringAsync(Web, ct);
                var scripts = Regex.Matches(html, "<script[^>]+src=\"(https://a-v2\\.sndcdn\\.com/assets/[^\"]+\\.js)\"");
                for (int i = scripts.Count - 1; i >= 0; i--)
                {
                    string js;
                    try { js = await http.GetStringAsync(scripts[i].Groups[1].Value, ct); }
                    catch (HttpRequestException) { continue; }
                    var m = Regex.Match(js, "client_id\\s*[:=]\\s*\"([A-Za-z0-9]{32})\"");
                    if (m.Success) { clientId = m.Groups[1].Value; return clientId; }
                }
                throw new ApiException(502, "Couldn't reach SoundCloud. Check your connection and try again.");
            }
            finally { idLock.Release(); }
        }

        // ---------- Sign-in (token comes from SoundCloud's own login page, see LoginWindow) ----------
        /// <summary>Returns the account if SoundCloud accepts the token, null if it doesn't (yet).</summary>
        public async Task<Account?> TrySignInAsync(string token, CancellationToken ct)
        {
            var acct = new Account { Id = "a" + DateTime.UtcNow.Ticks.ToString("x"), AccessTokenEnc = Secrets.Protect(token) };
            try
            {
                string body = await SendAsync(HttpMethod.Get, "/me", acct, ct);
                var me = JsonSerializer.Deserialize<ScMe>(body, Store.Json);
                if (me == null || me.Id == 0) return null;
                acct.UserId = me.Id;
                acct.Name = !string.IsNullOrWhiteSpace(me.Username) ? me.Username! : "Account";
                acct.AvatarUrl = me.AvatarUrl ?? "";
                return acct;
            }
            catch (ApiException ex) when (ex.Status == 401 || ex.Status == 403) { return null; }
        }

        // ---------- Requests ----------
        public async Task<string> SendAsync(HttpMethod method, string urlOrPath, Account? a = null, CancellationToken ct = default, bool refreshOnDeny = true, string? jsonBody = null)
        {
            a ??= Active;
            if (a == null) throw new ApiException(401, "Sign in to SoundCloud first (the account button, top left).");
            string token = Secrets.Unprotect(a.AccessTokenEnc);
            if (token.Length == 0) throw new ApiException(401, "This account is signed out. Sign in again from Settings.");

            // While the bot protection is blocking this connection a write is not even sent: every extra request only makes the block last longer.
            if (method != HttpMethod.Get && (DateTime.UtcNow < plainBlockedUntil || DateTime.UtcNow < hardBlockUntil)) throw Blocked();

            // Only a GET gets a second try (with a re-read web key, at most once per IdRefreshGap). A write is never repeated here.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string cid = await ClientIdAsync(attempt > 0, ct);
                if (attempt > 0) lastIdRefresh = DateTime.UtcNow;
                string url = AddQuery(Abs(urlOrPath), "client_id", cid);
                if (method != HttpMethod.Get) url = AddQuery(url, "app_locale", "en");
                // Plain reads were blocked a moment ago: ask the real soundcloud.com page instead of the plain client.
                if (method == HttpMethod.Get && DateTime.UtcNow < readsViaWebUntil && url.StartsWith(Base, StringComparison.OrdinalIgnoreCase))
                {
                    var viaWeb = await WebReadAsync(url, a, token, ct);
                    if (viaWeb != null) return viaWeb;
                }
                if (url.StartsWith(V1, StringComparison.OrdinalIgnoreCase)) url = AddQuery(url, "oauth_token", token);
                using var req = new HttpRequestMessage(method, url);
                if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
                req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
                // Same headers the website sends; SoundCloud rejects like/repost/follow writes that don't look like they come from soundcloud.com.
                req.Headers.TryAddWithoutValidation("Origin", Web);
                req.Headers.TryAddWithoutValidation("Referer", Web + "/");
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                using var res = await http.SendAsync(req, ct);
                string body = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) return body;
                if (res.StatusCode == HttpStatusCode.Forbidden && body.IndexOf("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // "t=bv" is DataDome's own marker for a hard block page (nothing to solve); anything else is a soft
                    // block a human check can clear. Same page, read directly from the plain client - no browser needed to tell them apart.
                    bool hard = body.IndexOf("t=bv", StringComparison.OrdinalIgnoreCase) >= 0;
                    plainBlockedUntil = DateTime.UtcNow.AddMinutes(hard ? 15 : 10);
                    if (hard) hardBlockUntil = DateTime.UtcNow.AddMinutes(15);
                    Store.Log("datadome " + (hard ? "hard" : "soft") + " block", new Exception(method.Method + " " + url.Split('?')[0] + " -> 403: " + (body.Length > 300 ? body.Substring(0, 300) : body)));
                    if (method == HttpMethod.Get && url.StartsWith(Base, StringComparison.OrdinalIgnoreCase))
                    {
                        var viaWeb = await WebReadAsync(url, a, token, ct);
                        if (viaWeb != null) { readsViaWebUntil = DateTime.UtcNow.AddMinutes(10); return viaWeb; }
                    }
                    throw Blocked();
                }
                if ((res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden) && attempt == 0 && refreshOnDeny
                    && method == HttpMethod.Get && DateTime.UtcNow - lastIdRefresh > IdRefreshGap) continue;
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                    throw new ApiException(401, "Your SoundCloud session expired. Sign in again from Settings.");
                throw Fail(res, body);
            }
            throw new ApiException(401, "Your SoundCloud session expired. Sign in again from Settings.");
        }

        /// <summary>
        /// A GET made by the real (hidden) soundcloud.com page, which SoundCloud's bot protection accepts. Null = the page couldn't answer either
        /// (the caller then reports the original error). A normal error answer (404 ...) is thrown like a plain HTTP one.
        /// </summary>
        async Task<string?> WebReadAsync(string url, Account a, string token, CancellationToken ct)
        {
            try
            {
                var (status, text) = await bridge.FetchAsync("GET", url, null, a.Id, token, ct, 4_000_000);
                if (status >= 200 && status < 300) return text;
                if (status == 0 || status == 403 || status == 429) return null;
                string msg = "";
                try { using var d = JsonDocument.Parse(text); if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) msg = m.GetString() ?? ""; } catch { }
                throw new ApiException(status, msg.Length > 0 ? msg : status.ToString());
            }
            catch (ApiException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Store.Log("web read", ex); return null; }
        }

        /// <summary>Plain download (audio segments, artwork). No credentials are sent.</summary>
        public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", Web + "/");
            req.Headers.TryAddWithoutValidation("Origin", Web);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var ex = Fail(res, "");
                string host = "";
                try { host = new Uri(url).Host; } catch { }
                throw new ApiException(ex.Status, ex.Message + (host.Length > 0 ? " (" + host + ")" : ""), ex.RetryAfter);
            }
            return await res.Content.ReadAsByteArrayAsync(ct);
        }

        static ApiException Fail(HttpResponseMessage res, string body)
        {
            string msg = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) msg = m.GetString() ?? "";
            }
            catch { }
            int status = (int)res.StatusCode;
            int retry = 0;
            if (res.Headers.TryGetValues("Retry-After", out var vals)) int.TryParse(vals.FirstOrDefault(), out retry);
            if (status == 429) msg = "SoundCloud rate limit reached" + (retry > 0 ? " - try again in " + retry + "s." : ". Try again in a moment.");
            if (msg.Length == 0) msg = status + " " + res.ReasonPhrase;
            return new ApiException(status, msg, retry);
        }

        // ---------- Parsing (one bad item never breaks the whole page) ----------
        static bool TryItems(JsonElement root, out JsonElement items, out string? next)
        {
            next = null;
            items = default;
            if (root.ValueKind == JsonValueKind.Array) { items = root; return true; }
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("next_href", out var n) && n.ValueKind == JsonValueKind.String) next = n.GetString();
            if (root.TryGetProperty("collection", out var c) && c.ValueKind == JsonValueKind.Array) { items = c; return true; }
            return false;
        }

        static JsonElement Unwrap(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : e;

        public static ScPage<ScTrack> ParseTracks(string json)
        {
            var page = new ScPage<ScTrack>();
            using var doc = JsonDocument.Parse(json);
            if (!TryItems(doc.RootElement, out var items, out var next)) return page;
            page.NextHref = next;
            foreach (var el in items.EnumerateArray())
            {
                try
                {
                    var tr = JsonSerializer.Deserialize<ScTrack>(Unwrap(el, "track").GetRawText(), Store.Json);
                    if (tr != null && tr.Id != 0) page.Collection.Add(tr);
                }
                catch { }
            }
            return page;
        }

        public static ScPage<ScPlaylist> ParsePlaylists(string json)
        {
            var page = new ScPage<ScPlaylist>();
            using var doc = JsonDocument.Parse(json);
            if (!TryItems(doc.RootElement, out var items, out var next)) return page;
            page.NextHref = next;
            foreach (var el in items.EnumerateArray())
            {
                try
                {
                    var pl = JsonSerializer.Deserialize<ScPlaylist>(Unwrap(el, "playlist").GetRawText(), Store.Json);
                    if (pl != null && pl.Id != 0) page.Collection.Add(pl);
                }
                catch { }
            }
            return page;
        }

        // ---------- Mixed lists (feed, home, search, charts...) ----------
        static string? Str(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static long Num(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

        static bool Has(JsonElement e, string key, JsonValueKind kind) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == kind;

        static ScTrack? ReadTrack(JsonElement el)
        {
            try { var t = JsonSerializer.Deserialize<ScTrack>(el.GetRawText(), Store.Json); return t != null && t.Id != 0 ? t : null; }
            catch { return null; }
        }

        static ScUser? ReadUser(JsonElement el)
        {
            try { var u = JsonSerializer.Deserialize<ScUser>(el.GetRawText(), Store.Json); return u != null && u.Id != 0 ? u : null; }
            catch { return null; }
        }

        /// <summary>Playlists by hand: system playlists ("Mixed for you", stations) have a text id that would break normal parsing.</summary>
        public static ScPlaylist? ReadPlaylist(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            var p = new ScPlaylist
            {
                Id = Num(el, "id"),
                TitleRaw = Str(el, "title") ?? Str(el, "short_title"),
                TrackCount = (int)Num(el, "track_count"),
                ArtworkUrl = Str(el, "artwork_url"),
                PermalinkUrl = Str(el, "permalink_url"),
                Urn = Str(el, "urn"),
                SetType = Str(el, "set_type"),
                Genre = Str(el, "genre"),
                TagList = Str(el, "tag_list"),
                Sharing = Str(el, "sharing"),
                Description = Str(el, "description"),
                LikesCount = Num(el, "likes_count"),
                LastModified = Str(el, "last_modified"),
                CreatedAt = Str(el, "created_at"),
            };
            if (p.Id == 0 && string.IsNullOrEmpty(p.Urn)) p.Urn = Str(el, "id");
            if (Has(el, "user", JsonValueKind.Object)) p.User = ReadUser(el.GetProperty("user"));
            if (Has(el, "tracks", JsonValueKind.Array))
            {
                p.Tracks = new List<ScTrack>();
                foreach (var te in el.GetProperty("tracks").EnumerateArray())
                {
                    var t = ReadTrack(te);
                    if (t != null) p.Tracks.Add(t);
                }
                if (p.TrackCount == 0) p.TrackCount = p.Tracks.Count;
            }
            p.IsSystem = Str(el, "kind") == "system-playlist" || (p.Urn ?? "").StartsWith("soundcloud:system-playlists", StringComparison.Ordinal);
            if (p.Id == 0 && string.IsNullOrEmpty(p.Urn)) return null;
            return p;
        }

        static void AddEntries(JsonElement el, List<ScEntry> into)
        {
            if (el.ValueKind != JsonValueKind.Object) return;
            string type = Str(el, "type") ?? "";
            string kind = Str(el, "kind") ?? "";

            // Home page: { title, items: { collection: [...] } }  ->  a header followed by its items
            if (el.TryGetProperty("items", out var its) && (Has(its, "collection", JsonValueKind.Array) || its.ValueKind == JsonValueKind.Array))
            {
                var arr = its.ValueKind == JsonValueKind.Array ? its : its.GetProperty("collection");
                var sub = new List<ScEntry>();
                foreach (var child in arr.EnumerateArray()) { try { AddEntries(child, sub); } catch { } }
                if (sub.Count > 0)
                {
                    string title = Str(el, "title") ?? "";
                    if (title.Length > 0) into.Add(new ScEntry { Kind = "header", Title = title });
                    into.AddRange(sub);
                }
                return;
            }

            // Feed items: type = track | track-repost | playlist | playlist-repost, the reposter is the top-level "user"
            string via = "";
            ScUser? poster = null;
            string postedAt = Str(el, "created_at") ?? "";
            if (type.EndsWith("-repost", StringComparison.Ordinal) && Has(el, "user", JsonValueKind.Object))
            {
                via = "↻ " + (Str(el.GetProperty("user"), "username") ?? "Someone") + " reposted";
                poster = ReadUser(el.GetProperty("user"));
            }

            if (Has(el, "track", JsonValueKind.Object))
            {
                var t = ReadTrack(el.GetProperty("track"));
                if (t != null) { t.Via = via; t.Poster = poster; t.PostedAt = postedAt; into.Add(new ScEntry { Kind = "track", Track = t }); }
                return;
            }
            if (Has(el, "playlist", JsonValueKind.Object))
            {
                var p = ReadPlaylist(el.GetProperty("playlist"));
                if (p != null) { p.Via = via; p.Poster = poster; p.PostedAt = postedAt; into.Add(new ScEntry { Kind = "playlist", Playlist = p }); }
                return;
            }

            if (kind == "track" || (kind.Length == 0 && el.TryGetProperty("duration", out _) && el.TryGetProperty("title", out _)))
            {
                var t = ReadTrack(el);
                if (t != null) into.Add(new ScEntry { Kind = "track", Track = t });
            }
            else if (kind == "playlist" || kind == "system-playlist" || kind == "album" || (kind.Length == 0 && el.TryGetProperty("track_count", out _)))
            {
                var p = ReadPlaylist(el);
                if (p != null) into.Add(new ScEntry { Kind = "playlist", Playlist = p });
            }
            else if (kind == "user" || (kind.Length == 0 && el.TryGetProperty("username", out _)))
            {
                var u = ReadUser(el);
                if (u != null) into.Add(new ScEntry { Kind = "user", User = u });
            }
        }

        /// <summary>
        /// Search answers carry "facets": [{ "facet": "model", "values": [{ "value": "track", "count": 33 }, ...] }].
        /// Read it loosely (the shape has changed before) so the page can say "Found 7 playlists, 33 tracks, 3 people".
        /// </summary>
        static void ReadFacets(JsonElement root, Dictionary<string, long> into)
        {
            try
            {
                if (!Has(root, "facets", JsonValueKind.Array)) return;
                foreach (var f in root.GetProperty("facets").EnumerateArray())
                {
                    if (!string.Equals(Str(f, "facet"), "model", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var key in new[] { "values", "value", "facets" })
                    {
                        if (!Has(f, key, JsonValueKind.Array)) continue;
                        foreach (var v in f.GetProperty(key).EnumerateArray())
                        {
                            string name = (Str(v, "value") ?? Str(v, "name") ?? "").ToLowerInvariant();
                            long n = Num(v, "count");
                            if (name.Length > 0 && n > 0) into[name] = n;
                        }
                        if (into.Count > 0) return;
                    }
                }
            }
            catch { }
        }

        public static ScPage<ScEntry> ParseEntries(string json)
        {
            var page = new ScPage<ScEntry>();
            using var doc = JsonDocument.Parse(json);
            ReadFacets(doc.RootElement, page.Facets);
            if (!TryItems(doc.RootElement, out var items, out var next)) return page;
            page.NextHref = next;
            foreach (var el in items.EnumerateArray())
            {
                try { AddEntries(el, page.Collection); } catch { }
            }
            return page;
        }

        public async Task<ScPage<ScEntry>> GetEntryPageAsync(string urlOrPath, CancellationToken ct = default) =>
            ParseEntries(await SendAsync(HttpMethod.Get, urlOrPath, null, ct));

        /// <summary>Where a playlist-like thing lives: normal playlists, "Mixed for you" system playlists, and stations.</summary>
        static string PlaylistPath(string key)
        {
            string esc = Uri.EscapeDataString(key).Replace("%3A", ":");
            if (key.StartsWith("soundcloud:system-playlists", StringComparison.Ordinal)) return "/system-playlists/" + esc;
            if (key.Contains("-stations:", StringComparison.Ordinal)) return "/stations/" + esc;
            return "/playlists/" + esc;
        }

        public async Task<List<ScTrack>> GetRelatedAsync(long trackId, CancellationToken ct = default)
        {
            var page = ParseTracks(await SendAsync(HttpMethod.Get, "/tracks/" + trackId + "/related?limit=30", null, ct));
            return page.Collection;
        }

        /// <summary>Playlists that contain this song ("In playlists" on the song page).</summary>
        public async Task<List<ScPlaylist>> GetTrackPlaylistsAsync(long trackId, CancellationToken ct = default) =>
            ParsePlaylists(await SendAsync(HttpMethod.Get, "/tracks/" + trackId + "/playlists_without_albums?limit=6", null, ct)).Collection;

        public async Task<ScTrack?> GetTrackAsync(long id, CancellationToken ct = default)
        {
            try { return JsonSerializer.Deserialize<ScTrack>(await SendAsync(HttpMethod.Get, "/tracks/" + id, null, ct), Store.Json); }
            catch (ApiException ex) when (ex.Status == 404) { return null; }
        }

        public async Task<ScPage<ScTrack>> GetTrackPageAsync(string urlOrPath, CancellationToken ct = default) =>
            ParseTracks(await SendAsync(HttpMethod.Get, urlOrPath, null, ct));

        public async Task<ScPage<ScPlaylist>> GetPlaylistPageAsync(string urlOrPath, CancellationToken ct = default) =>
            ParsePlaylists(await SendAsync(HttpMethod.Get, urlOrPath, null, ct));

        /// <summary>All tracks of one playlist, system playlist or station (stub entries are filled in, unavailable ones dropped).</summary>
        public async Task<List<ScTrack>> GetPlaylistTracksAsync(string key, CancellationToken ct = default) =>
            (await GetPlaylistAsync(key, ct)).Tracks;

        /// <summary>The playlist itself (title, owner, cover, tags, privacy...; null for stations) together with all of its tracks.</summary>
        public async Task<(ScPlaylist? Meta, List<ScTrack> Tracks)> GetPlaylistAsync(string key, CancellationToken ct = default)
        {
            ScPlaylist? pl;
            try
            {
                string json = await SendAsync(HttpMethod.Get, PlaylistPath(key), null, ct);
                using var doc = JsonDocument.Parse(json);
                pl = ReadPlaylist(doc.RootElement);
                if (pl == null) // station replies have no id of their own, only tracks
                {
                    pl = new ScPlaylist { Tracks = new List<ScTrack>() };
                    if (Has(doc.RootElement, "tracks", JsonValueKind.Array))
                        foreach (var te in doc.RootElement.GetProperty("tracks").EnumerateArray()) { var t = ReadTrack(te); if (t != null) pl.Tracks.Add(t); }
                }
            }
            catch (ApiException ex) when (ex.Status == 404 && key.Contains("track-stations:", StringComparison.Ordinal))
            {
                // Station endpoint missing for this track: fall back to SoundCloud's "related tracks".
                long id = long.TryParse(key.Substring(key.LastIndexOf(':') + 1), out var n) ? n : 0;
                pl = new ScPlaylist { Tracks = new List<ScTrack>() };
                if (id != 0)
                {
                    var seed = await GetTrackAsync(id, ct);
                    if (seed != null) pl.Tracks.Add(seed);
                    pl.Tracks.AddRange((await GetRelatedAsync(id, ct)).Where(t => t.Id != id));
                }
            }

            var tracks = pl.Tracks ?? new List<ScTrack>();
            // A station should start with the song it was made from.
            if (key.Contains("track-stations:", StringComparison.Ordinal) && long.TryParse(key.Substring(key.LastIndexOf(':') + 1), out long seedId)
                && tracks.Count > 0 && tracks.All(t => t.Id != seedId))
            {
                var seed = await GetTrackAsync(seedId, ct);
                if (seed != null) tracks.Insert(0, seed);
            }

            var full = new Dictionary<long, ScTrack>();
            var stubs = tracks.Where(t => t != null && t.IsStub).Select(t => t.Id).Distinct().ToList();
            for (int i = 0; i < stubs.Count; i += 30)
            {
                string ids = string.Join(",", stubs.Skip(i).Take(30));
                var page = ParseTracks(await SendAsync(HttpMethod.Get, "/tracks?ids=" + ids, null, ct));
                foreach (var t in page.Collection) full[t.Id] = t;
            }
            var result = new List<ScTrack>();
            foreach (var t in tracks)
            {
                if (t == null) continue;
                if (t.IsStub) { if (full.TryGetValue(t.Id, out var f)) result.Add(f); }
                else result.Add(t);
            }
            return (string.IsNullOrEmpty(pl.TitleRaw) ? null : pl, result);
        }

        // ---------- Streaming ----------
        static bool IsMp3(ScTranscoding t) =>
            (t.Format?.MimeType ?? "").StartsWith("audio/mpeg", StringComparison.OrdinalIgnoreCase);

        static bool IsAac(ScTranscoding t) =>
            (t.Format?.MimeType ?? "").StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase) && t.Format?.Protocol == "hls";

        /// <summary>Best first: full-length MP3 file, full-length MP3 HLS, then AAC HLS; 30 s previews only after the full versions.</summary>
        static List<ScTranscoding> Candidates(ScTrack tr) =>
            (tr.Media?.Transcodings ?? new List<ScTranscoding>())
                .Where(t => !string.IsNullOrEmpty(t.Url) && (IsMp3(t) || IsAac(t)))
                .OrderBy(t => t.Snipped ? 1 : 0)
                .ThenBy(t => IsMp3(t) ? 0 : 1)
                .ThenBy(t => t.Format?.Protocol == "progressive" ? 0 : 1)
                .ToList();

        /// <summary>Always ask SoundCloud for the track again: the copy in a list can have an expired track_authorization.</summary>
        async Task<ScTrack> FreshTrackAsync(ScTrack fallback, CancellationToken ct)
        {
            string[] paths = { "/tracks/" + fallback.Id, "/tracks?ids=" + fallback.Id, "/tracks/soundcloud:tracks:" + fallback.Id };
            ApiException? last = null;
            foreach (var path in paths)
            {
                try
                {
                    string json = await SendAsync(HttpMethod.Get, path, null, ct);
                    var page = ParseTracks(json);
                    var t = page.Collection.FirstOrDefault(x => x.Id == fallback.Id);
                    if (t == null)
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object) t = ReadTrack(doc.RootElement);
                    }
                    if (t != null) return t;
                }
                catch (ApiException ex) when (ex.Status == 404 || ex.Status == 403 || ex.Status == 400) { last = ex; }
            }
            if (last != null && last.Status == 404)
                throw new InvalidOperationException("SoundCloud says this track no longer exists or isn't available in your region (404).");
            return fallback;
        }

        /// <summary>Finds a playable stream for the track. Hls = playlist of segments, otherwise one MP3 file.</summary>
        public async Task<(string Url, bool Hls)> ResolveStreamAsync(ScTrack track, CancellationToken ct)
        {
            var tr = track;
            var problems = new List<string>();
            bool stale = tr.Media?.Transcodings == null || tr.Media.Transcodings.Count == 0 || string.IsNullOrEmpty(tr.TrackAuthorization);

            for (int round = 0; round < 2; round++)
            {
                if (round == 1 || stale) tr = await FreshTrackAsync(track, ct);
                var list = Candidates(tr);
                if (list.Count == 0)
                {
                    if (round == 0) { stale = true; continue; }
                    break;
                }
                foreach (var pick in list)
                {
                    try
                    {
                        string url = pick.Url!;
                        if (!string.IsNullOrEmpty(tr.TrackAuthorization)) url = AddQuery(url, "track_authorization", tr.TrackAuthorization!);
                        string res = await SendAsync(HttpMethod.Get, url, null, ct);
                        using var doc = JsonDocument.Parse(res);
                        if (doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(u.GetString()))
                            return (u.GetString()!, pick.Format?.Protocol == "hls");
                        problems.Add((pick.Preset ?? "stream") + ": no url");
                    }
                    catch (ApiException ex) when (ex.Status == 404 || ex.Status == 401 || ex.Status == 403 || ex.Status == 410)
                    {
                        problems.Add((pick.Preset ?? "stream") + ": " + ex.Status);
                    }
                }
                stale = true; // every option failed with the copy we had: fetch a fresh one and try once more
            }
            Store.Log("stream", new Exception("No stream for track " + track.Id + " (" + track.Title + "): " + string.Join(", ", problems)));
            throw new InvalidOperationException(problems.Count > 0
                ? "SoundCloud refused the audio for this track (" + string.Join(", ", problems.Distinct()) + "). It may be restricted or Go+ only."
                : "This track can't be streamed here (it may be restricted or Go+ only).");
        }

        // ---------- Profiles, waveforms, follow state ----------
        /// <summary>A full profile (follower counts, bio, banner). Null if the user doesn't exist any more.</summary>
        public async Task<ScUser?> GetUserAsync(string idOrPath, CancellationToken ct = default)
        {
            try { return JsonSerializer.Deserialize<ScUser>(await SendAsync(HttpMethod.Get, "/users/" + Uri.EscapeDataString(idOrPath), null, ct), Store.Json); }
            catch (ApiException ex) when (ex.Status == 404) { return null; }
        }

        /// <summary>Turns a soundcloud.com link into the song / profile / playlist it points at, so it can be opened inside the app.</summary>
        public async Task<(string Kind, ScTrack? Track, ScUser? User, ScPlaylist? Playlist)> ResolveAsync(string url, CancellationToken ct = default)
        {
            string json = await SendAsync(HttpMethod.Get, "/resolve?url=" + Uri.EscapeDataString(url), null, ct);
            using var doc = JsonDocument.Parse(json);
            string kind = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? (k.GetString() ?? "") : "";
            switch (kind)
            {
                case "track": return (kind, JsonSerializer.Deserialize<ScTrack>(json, Store.Json), null, null);
                case "user": return (kind, null, JsonSerializer.Deserialize<ScUser>(json, Store.Json), null);
                case "playlist":
                case "system-playlist": return ("playlist", null, null, JsonSerializer.Deserialize<ScPlaylist>(json, Store.Json));
                default: return (kind, null, null, null);
            }
        }

        /// <summary>The links on a profile (Spotify, Instagram...).</summary>
        public async Task<List<ScWebProfile>> GetWebProfilesAsync(long userId, CancellationToken ct = default)
        {
            var list = new List<ScWebProfile>();
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "/users/" + userId + "/web-profiles", null, ct));
            if (!TryItems(doc.RootElement, out var items, out _)) return list;
            foreach (var el in items.EnumerateArray())
            {
                try { var w = JsonSerializer.Deserialize<ScWebProfile>(el.GetRawText(), Store.Json); if (w != null && !string.IsNullOrWhiteSpace(w.Url)) list.Add(w); } catch { }
            }
            return list;
        }

        /// <summary>People (followers / following / related artists) from any user-list route.</summary>
        public async Task<List<ScUser>> GetUserListAsync(string path, CancellationToken ct = default)
        {
            var page = ParseEntries(await SendAsync(HttpMethod.Get, path, null, ct));
            return page.Collection.Where(e => e.Kind == "user" && e.User != null).Select(e => e.User!).ToList();
        }

        /// <summary>The newest comments a person left (with the song each one is on).</summary>
        public async Task<List<ScComment>> GetUserCommentsAsync(long userId, int limit, CancellationToken ct = default)
        {
            var list = new List<ScComment>();
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "/users/" + userId + "/comments?limit=" + limit, null, ct));
            if (!TryItems(doc.RootElement, out var items, out _)) return list;
            foreach (var el in items.EnumerateArray())
            {
                try { var c = JsonSerializer.Deserialize<ScComment>(el.GetRawText(), Store.Json); if (c != null && !string.IsNullOrWhiteSpace(c.Body)) list.Add(c); } catch { }
            }
            return list;
        }

        /// <summary>One page of a person's comments plus the link to the next page (null on the last page).</summary>
        public async Task<(List<ScComment> Items, string? Next)> GetUserCommentsPageAsync(string urlOrPath, CancellationToken ct = default)
        {
            var list = new List<ScComment>();
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, urlOrPath, null, ct));
            if (!TryItems(doc.RootElement, out var items, out var next)) return (list, null);
            foreach (var el in items.EnumerateArray())
            {
                try { var c = JsonSerializer.Deserialize<ScComment>(el.GetRawText(), Store.Json); if (c != null && !string.IsNullOrWhiteSpace(c.Body)) list.Add(c); } catch { }
            }
            return (list, next);
        }

        /// <summary>Wipe the listening history of the signed-in account.</summary>
        public Task ClearHistoryAsync() => WriteAsync(HttpMethod.Delete, null, "/me/play-history/tracks");

        /// <summary>
        /// Ids of everyone you follow, read in one go from your account (the same way liked / reposted tracks are synced).
        /// First the short "ids" routes, then - if SoundCloud refuses those - the Following list itself, page by page.
        /// Null only when nothing could be read at all, so a failed read never turns into "you follow nobody".
        /// </summary>
        public async Task<HashSet<long>?> GetFollowingIdsAsync(CancellationToken ct = default)
        {
            var a = Active;
            if (a == null) return null;
            var ids = await ReadIdsAsync(ct, "/me/followings/ids?limit=5000", V1 + "/e1/me/followings/ids?limit=5000");
            if (ids != null) return ids;
            try
            {
                var set = new HashSet<long>();
                string? next = "/users/" + a.UserId + "/followings?limit=200";
                for (int i = 0; i < 25 && next != null; i++)
                {
                    var page = ParseEntries(await SendAsync(HttpMethod.Get, next, null, ct));
                    foreach (var e in page.Collection)
                        if (e.Kind == "user" && e.User != null) set.Add(e.User.Id);
                    next = page.NextHref;
                }
                return set;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// Is this one person followed? True / false when SoundCloud gave a clear answer, null when it couldn't be told
        /// (an error is never read as "not following"). Only used while the full list hasn't been synced yet.
        /// </summary>
        public async Task<bool?> IsFollowingAsync(long userId, CancellationToken ct = default)
        {
            var a = Active;
            if (a == null) return null;
            foreach (var route in new[] { "/users/" + a.UserId + "/followings/" + userId, V1 + "/me/followings/" + userId })
            {
                try { await SendAsync(HttpMethod.Get, route, null, ct, false); return true; }
                catch (ApiException ex) when (ex.Status == 404) { return false; }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return null;
        }

        /// <summary>The bars of a track's waveform (wave.sndcdn.com serves the same data as .json next to the .png). Null if unavailable.</summary>
        public async Task<float[]?> GetWaveformAsync(string? url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                string json = url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? url.Substring(0, url.Length - 4) + ".json" : url;
                var bytes = await GetBytesAsync(json, ct);
                using var doc = JsonDocument.Parse(bytes);
                if (!doc.RootElement.TryGetProperty("samples", out var s) || s.ValueKind != JsonValueKind.Array) return null;
                var list = new List<float>(s.GetArrayLength());
                foreach (var v in s.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.Number) list.Add((float)v.GetDouble());
                return list.Count > 0 ? list.ToArray() : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// Runs an account action (like / repost / follow / comment / history) with plain HTTP only.
        /// One request per route. The next route is tried only when the server answered "no such route" (404 / 405).
        /// A refusal (401 / 403 / 429 / 5xx) or a network error stops at once and is reported. Nothing is retried and no hidden browser
        /// or page click is started from here. Success means the server answered 2xx; every other outcome throws.
        /// </summary>
        async Task WriteAsync(HttpMethod method, string? v1Path, string v2Path, string? jsonBody = null, UiAct? ui = null, string? v2Alt = null)
        {
            var a = Active;
            if (a == null) throw new ApiException(401, "Sign in to SoundCloud first (the account button, top left).");
            if (DateTime.UtcNow < hardBlockUntil || DateTime.UtcNow < plainBlockedUntil) throw Blocked();

            var routes = new List<string>();
            if (v1Path == null || v2First) { routes.Add(Base + v2Path); if (v1Path != null) routes.Add(V1 + v1Path); }
            else { routes.Add(V1 + v1Path); routes.Add(Base + v2Path); }
            if (v2Alt != null) routes.Add(Base + v2Alt); // a second spelling of the same call, tried only when the first one answers 404 / 405

            var tried = new List<string>();
            ApiException? last = null;
            foreach (string route in routes)
            {
                bool viaV2 = route.StartsWith(Base, StringComparison.Ordinal);
                string label = viaV2 ? "api-v2" : "api";
                try
                {
                    await SendAsync(method, route, a, CancellationToken.None, false, jsonBody);
                    v2First = viaV2;
                    KickHistory(); // a like/repost/follow succeeded: also flush any queued listening-history plays
                    return;
                }
                catch (ApiException ex) when (ex.Status == 404 || ex.Status == 405)
                {
                    last = ex;
                    tried.Add(label + ": " + ex.Status);
                }
                catch (ApiException ex) when (ex.Status == 401 || ex.Status == 429 || ex.Message.StartsWith("SoundCloud's bot protection", StringComparison.Ordinal))
                {
                    throw; // message is already user-ready (expired session / rate limit / bot protection)
                }
                catch (ApiException ex)
                {
                    throw new ApiException(ex.Status, "SoundCloud did not accept this (" + label + " " + ex.Status + "): " + ex.Message + " Nothing was changed.", ex.RetryAfter);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
                {
                    Store.Log("write " + method.Method + " " + route.Split('?')[0], ex);
                    throw new ApiException(502, "Couldn't reach SoundCloud (network error or timeout). Nothing was changed.");
                }
            }

            // Every route said "not found / not allowed". That is a failure, never a success - also for unlike / unfollow / unrepost.
            throw new ApiException(last?.Status ?? 502, "SoundCloud has no such action on the routes KaruCloud knows (" + string.Join(", ", tried) + "). Nothing was changed.");
        }

        /// <summary>An action done by clicking the real button on the real soundcloud.com page (kind: like | repost | follow | comment).</summary>
        sealed record UiAct(string Kind, string PageUrl, bool Want, string Text = "");

        /// <summary>
        /// OPT-IN ONLY. Not called from WriteAsync or any automatic path - confirmed by audit, nothing in the project calls this.
        /// Driving the real page is read from its own DOM state after clicking (not a server response), so a success here is
        /// "the button now shows the state we wanted", not a confirmed write. Only call this from an explicit, user-triggered
        /// retry action if one is ever added; never from a background or automatic retry.
        /// </summary>
        async Task UiRunAsync(UiAct ui, Account a)
        {
            string token = Secrets.Unprotect(a.AccessTokenEnc);
            if (token.Length == 0) throw new ApiException(401, "This account is signed out. Sign in again from Settings.");
            var (ok, err) = await bridge.UiActionAsync(a.Id, token, ui.PageUrl, ui.Kind, ui.Want, ui.Text);
            if (!ok) throw new ApiException(409, err.Length > 0 ? err : "The page didn't accept it.");
        }

        /// <summary>
        /// OPT-IN ONLY. Not called from WriteAsync or any automatic path - confirmed by audit, nothing in the project calls this.
        /// The same request, made by a hidden soundcloud.com page (see WebBridge). Only call this from an explicit, user-triggered
        /// retry action if one is ever added; never from a background or automatic retry.
        /// </summary>
        async Task BridgeWriteAsync(HttpMethod method, List<string> routes, string? jsonBody, Account a)
        {
            string token = Secrets.Unprotect(a.AccessTokenEnc);
            if (token.Length == 0) throw new ApiException(401, "This account is signed out. Sign in again from Settings.");
            string cid = await ClientIdAsync(false, CancellationToken.None);
            int status = 0;
            string text = "";
            foreach (var route in routes)
            {
                // api.soundcloud.com has no CORS headers, so a request from the soundcloud.com page can never succeed there.
                if (route.StartsWith(V1, StringComparison.OrdinalIgnoreCase)) continue;
                string url = AddQuery(AddQuery(route, "client_id", cid), "app_locale", "en");
                try { (status, text) = await bridge.FetchAsync(method.Method, url, jsonBody, a.Id, token, CancellationToken.None); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Store.Log("web bridge", ex);
                    throw new ApiException(502, "The background browser couldn't start: " + ex.Message);
                }
                if (status >= 200 && status < 300) return;
                try { Store.Log("web bridge " + method.Method + " " + route.Split('?')[0] + " -> " + status, new Exception(text.Length > 600 ? text.Substring(0, 600) : text)); } catch { }
                if (status == 429) break;
            }
            if (status == 403 && text.IndexOf("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("t=bv", StringComparison.OrdinalIgnoreCase) >= 0)
                hardBlockUntil = DateTime.UtcNow.AddMinutes(2);
            if (status == 403 && text.IndexOf("captcha-delivery.com", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new ApiException(403, "SoundCloud's bot protection is blocking likes / reposts / comments from this network right now (\"access temporarily restricted\"). Open the \"...\" menu -> \"Fix likes & sync\", browse soundcloud.com there once and close it, then try again. If it still says this, turn off any VPN / proxy or switch network: the block is on the connection, not on the app.");
            string detail = (text.Length > 160 ? text.Substring(0, 160) : text).Replace("\r", " ").Replace("\n", " ");
            throw new ApiException(status == 0 ? 502 : status, status == 0 ? "The background browser couldn't reach SoundCloud (" + detail + ")." : "SoundCloud answered " + status + " (" + detail + ").");
        }

        /// <summary>
        /// Tells SoundCloud you started a track, which is what puts it in your listening history (like the website's player does).
        /// Plain HTTP is used when SoundCloud accepts it; once only the hidden browser works, plays are queued and sent in batches.
        /// </summary>
        public async Task ReportPlayAsync(long trackId)
        {
            if (flushFails >= 3) throw new ApiException(502, "Listening-history sync is paused after repeated failures.");
            if (!bridgeFirst) { await SendHistoryAsync(trackId); return; }

            int count;
            lock (historyLock)
            {
                historyQueue.Enqueue(trackId);
                while (historyQueue.Count > 50) historyQueue.Dequeue();
                count = historyQueue.Count;
            }
            if (count >= HistoryBatch) _ = FlushHistoryAsync();
            else if (count == 1) historyTimer.Change(HistoryDelay, Timeout.InfiniteTimeSpan);
        }

        bool historyTracksRouteDead; // once "/me/play-history/tracks" says 404, don't ask it again for every play

        async Task SendHistoryAsync(long trackId)
        {
            string body = "{\"track_urn\":\"soundcloud:tracks:" + trackId + "\"}";
            if (!historyTracksRouteDead)
            {
                try { await WriteAsync(HttpMethod.Post, null, "/me/play-history/tracks", body); return; }
                catch (ApiException ex) when (ex.Status == 400 || ex.Status == 404 || ex.Status == 405 || ex.Status == 422) { historyTracksRouteDead = true; }
            }
            await WriteAsync(HttpMethod.Post, null, "/me/play-history", body);
        }

        /// <summary>The hidden browser just ran for something else, so send any waiting plays now.</summary>
        void KickHistory()
        {
            if (Volatile.Read(ref flushing) == 0 && HasPendingHistory) _ = Task.Run(FlushHistoryAsync);
        }

        /// <summary>Sends every queued play, oldest first, in one browser session.</summary>
        public async Task FlushHistoryAsync()
        {
            if (Interlocked.Exchange(ref flushing, 1) == 1) return;
            try
            {
                long[] items;
                lock (historyLock) { items = historyQueue.ToArray(); historyQueue.Clear(); }
                if (items.Length == 0) return;
                historyTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                int sent = 0;
                try
                {
                    foreach (long id in items) { await SendHistoryAsync(id); sent++; }
                    flushFails = 0;
                }
                catch (Exception ex)
                {
                    flushFails++;
                    Store.Log("play history batch", ex);
                    lock (historyLock)
                    {
                        var rest = items.Skip(sent).Concat(historyQueue).Take(50).ToArray();
                        historyQueue.Clear();
                        if (flushFails < 3) foreach (long r in rest) historyQueue.Enqueue(r);
                    }
                    if (flushFails < 3) historyTimer.Change(HistoryDelay, Timeout.InfiniteTimeSpan);
                    return;
                }
                HistorySynced?.Invoke();
            }
            finally { Volatile.Write(ref flushing, 0); }
        }

        /// <summary>Forget waiting plays (they belong to the account that was active) and allow history sync again.</summary>
        public void ResetHistory()
        {
            lock (historyLock) historyQueue.Clear();
            flushFails = 0;
            try { historyTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); } catch { }
        }

        /// <summary>Reads a list of ids (liked / reposted tracks). Null when SoundCloud couldn't be read at all.</summary>
        async Task<HashSet<long>?> ReadIdsAsync(CancellationToken ct, params string[] routes)
        {
            foreach (var route in routes)
            {
                try
                {
                    string json = await SendAsync(HttpMethod.Get, route, null, ct);
                    using var doc = JsonDocument.Parse(json);
                    if (!TryItems(doc.RootElement, out var items, out _)) continue;
                    var set = new HashSet<long>();
                    foreach (var el in items.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long numId)) set.Add(numId);
                        else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out long objId)) set.Add(objId);
                    }
                    return set;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            return null;
        }

        /// <summary>Ids of the tracks you reposted. Complete = the full list from your account (so it can replace what's cached).</summary>
        public async Task<(HashSet<long>? Set, bool Complete)> GetRepostedIdsAsync(CancellationToken ct = default)
        {
            var a = Active;
            if (a == null) return (null, false);
            var ids = await ReadIdsAsync(ct, V1 + "/e1/me/track_reposts/ids?limit=5000");
            if (ids != null) return (ids, true);
            try
            {
                var set = new HashSet<long>();
                string? next = "/stream/users/" + a.UserId + "/reposts?limit=50";
                for (int i = 0; i < 4 && next != null; i++)
                {
                    var page = ParseEntries(await SendAsync(HttpMethod.Get, next, null, ct));
                    foreach (var e in page.Collection)
                        if (e.Kind == "track" && e.Track != null) set.Add(e.Track.Id);
                    next = page.NextHref;
                }
                return (set, false);
            }
            catch (OperationCanceledException) { throw; }
            catch { return (null, false); }
        }

        // ---------- Likes ----------
        /// <summary>Ids of the tracks you liked, straight from your account. Null when SoundCloud couldn't be read.</summary>
        public Task<HashSet<long>?> GetLikedIdsAsync(CancellationToken ct = default) =>
            ReadIdsAsync(ct, "/me/track_likes/ids?limit=5000", V1 + "/e1/me/track_likes/ids?limit=5000");

        public Task RepostAsync(long trackId, bool on, string? pageUrl = null) =>
            WriteAsync(on ? HttpMethod.Put : HttpMethod.Delete, "/e1/me/track_reposts/" + trackId, "/e1/me/track_reposts/" + trackId, null,
                string.IsNullOrEmpty(pageUrl) ? null : new UiAct("repost", pageUrl, on));

        public Task FollowAsync(long userId, bool on, string? pageUrl = null) =>
            WriteAsync(on ? HttpMethod.Put : HttpMethod.Delete, "/e1/me/followings/" + userId, "/me/followings/" + userId, null,
                string.IsNullOrEmpty(pageUrl) ? null : new UiAct("follow", pageUrl, on), Active != null ? "/users/" + Active.UserId + "/followings/" + userId : null);

        public Task LikePlaylistAsync(long playlistId, bool like)
        {
            var a = Active;
            if (a == null) throw new ApiException(401, "Sign in to SoundCloud first.");
            return WriteAsync(like ? HttpMethod.Put : HttpMethod.Delete, "/e1/me/playlist_likes/" + playlistId, "/users/" + a.UserId + "/playlist_likes/" + playlistId);
        }

        // ---------- Your playlists (Add to playlist) ----------
        /// <summary>Your own playlists (public and private), newest first as SoundCloud lists them.</summary>
        public async Task<List<ScPlaylist>> GetMyPlaylistsAsync(CancellationToken ct = default)
        {
            var a = Active;
            if (a == null) throw new ApiException(401, "Sign in to SoundCloud first.");
            var all = new List<ScPlaylist>();
            string? next = "/users/" + a.UserId + "/playlists_without_albums?limit=50";
            for (int i = 0; i < 6 && next != null; i++)
            {
                var pg = await GetPlaylistPageAsync(next, ct);
                all.AddRange(pg.Collection.Where(p => p.Id != 0));
                next = string.IsNullOrEmpty(pg.NextHref) ? null : pg.NextHref;
            }
            return all;
        }

        /// <summary>Adds a track at the end of one of your playlists (SoundCloud replaces the whole track list, so the current ids are read first).</summary>
        public async Task AddTrackToPlaylistAsync(long playlistId, long trackId)
        {
            var ids = new List<long>();
            using (var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "/playlists/" + playlistId, null, CancellationToken.None)))
                if (doc.RootElement.TryGetProperty("tracks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var te in arr.EnumerateArray())
                        if (te.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id)) ids.Add(id);
            if (ids.Contains(trackId)) throw new ApiException(409, "This track is already in that playlist.");
            ids.Add(trackId);
            string path = "/playlists/" + playlistId;
            string nested = JsonSerializer.Serialize(new { playlist = new { tracks = ids.Select(i => new { id = i }).ToArray() } });
            try { await WriteAsync(HttpMethod.Put, path, path, nested); }
            catch (ApiException ex) when (ex.Status != 401 && ex.Status != 429)
            {
                await WriteAsync(HttpMethod.Put, null, path, JsonSerializer.Serialize(new { tracks = ids }));
            }
        }

        /// <summary>Creates a playlist that starts with the given track.</summary>
        public async Task CreatePlaylistAsync(string title, bool isPrivate, long trackId)
        {
            string sharing = isPrivate ? "private" : "public";
            string nested = JsonSerializer.Serialize(new { playlist = new { title, sharing, tracks = new[] { new { id = trackId } } } });
            try { await WriteAsync(HttpMethod.Post, "/playlists", "/playlists", nested); }
            catch (ApiException ex) when (ex.Status != 401 && ex.Status != 429)
            {
                await WriteAsync(HttpMethod.Post, null, "/playlists", JsonSerializer.Serialize(new { title, sharing, tracks = new[] { trackId } }));
            }
        }

        public Task LikeAsync(long trackId, bool like, string? pageUrl = null)
        {
            var a = Active;
            if (a == null) throw new ApiException(401, "Sign in to SoundCloud first.");
            return WriteAsync(like ? HttpMethod.Put : HttpMethod.Delete, "/e1/me/track_likes/" + trackId, "/users/" + a.UserId + "/track_likes/" + trackId, null,
                string.IsNullOrEmpty(pageUrl) ? null : new UiAct("like", pageUrl, like));
        }

        // ---------- Profile picture ----------
        /// <summary>Reads your own profile once so the header can show your picture (also fills it in for accounts added before this existed).</summary>
        public async Task<string> RefreshAvatarAsync(Account a, CancellationToken ct = default)
        {
            try
            {
                var me = JsonSerializer.Deserialize<ScMe>(await SendAsync(HttpMethod.Get, "/me", a, ct), Store.Json);
                if (me != null && !string.IsNullOrEmpty(me.AvatarUrl)) a.AvatarUrl = me.AvatarUrl!;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return a.AvatarUrl;
        }

        // ---------- Comments ----------
        /// <summary>The comments on a track (timed ones carry the song position they were left at).</summary>
        public async Task<List<ScComment>> GetCommentsAsync(long trackId, CancellationToken ct = default)
        {
            var list = new List<ScComment>();
            string json = await SendAsync(HttpMethod.Get, "/tracks/" + trackId + "/comments?threaded=0&filter_replies=1&limit=200", null, ct);
            using var doc = JsonDocument.Parse(json);
            if (!TryItems(doc.RootElement, out var items, out _)) return list;
            foreach (var el in items.EnumerateArray())
            {
                try
                {
                    var c = JsonSerializer.Deserialize<ScComment>(el.GetRawText(), Store.Json);
                    if (c != null && !string.IsNullOrWhiteSpace(c.Body)) list.Add(c);
                }
                catch { }
            }
            return list;
        }

        /// <summary>Posts a comment; timestampMs is the position in the song (use a negative value for a plain comment).</summary>
        public Task PostCommentAsync(long trackId, string body, long timestampMs, string? pageUrl = null)
        {
            string json = timestampMs >= 0
                ? JsonSerializer.Serialize(new { body, timestamp = timestampMs })
                : JsonSerializer.Serialize(new { body });
            return WriteAsync(HttpMethod.Post, null, "/tracks/" + trackId + "/comments", json,
                string.IsNullOrEmpty(pageUrl) ? null : new UiAct("comment", pageUrl, true, body));
        }

        // ---------- Notifications and messages ----------
        /// <summary>The red-dot counters the website shows on the bell and the envelope. Null when SoundCloud didn't answer.</summary>
        public async Task<(int Notifications, int Messages)?> GetBadgesAsync(CancellationToken ct = default)
        {
            try
            {
                using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "/badges", null, ct, false));
                int n = 0, m = 0;
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int v)) continue;
                        string name = p.Name.ToLowerInvariant();
                        if (name.Contains("notification")) n += v;
                        else if (name.Contains("message") || name.Contains("conversation")) m += v;
                    }
                return (n, m);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>Recent notifications (likes, reposts, comments, new followers...). Tries the routes the website has used.</summary>
        public Task<List<FeedItem>> GetNotificationsAsync(CancellationToken ct = default) =>
            GetFeedAsync(ct, false, "/web-notifications?limit=30");

        /// <summary>Your conversations, newest first.</summary>
        public Task<List<FeedItem>> GetConversationsAsync(CancellationToken ct = default) =>
            GetFeedAsync(ct, true, "/users/" + (Active?.UserId ?? 0) + "/conversations?limit=30", "/me/conversations?limit=30", "/conversations?limit=30");

        /// <summary>The messages of one conversation, oldest first.</summary>
        public async Task<List<FeedItem>> GetMessagesAsync(string conversationId, CancellationToken ct = default)
        {
            long me = Active?.UserId ?? 0;
            string id = Uri.EscapeDataString(conversationId).Replace("%3A", ":");
            var routes = new[] { "/users/" + me + "/conversations/" + id + "/messages?limit=60", "/me/conversations/" + id + "/messages?limit=60", "/conversations/" + id + "/messages?limit=60" };
            ApiException? last = null;
            foreach (var route in routes)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, route, null, ct, false));
                    var list = new List<FeedItem>();
                    if (!TryItems(doc.RootElement, out var items, out _)) return list;
                    foreach (var el in items.EnumerateArray())
                    {
                        string text = FirstStr(el, "content", "body", "text", "message");
                        if (text.Length == 0) continue;
                        var f = new FeedItem { Text = text, When = FirstStr(el, "sent_at", "created_at") };
                        JsonElement who = default;
                        foreach (var k in new[] { "sender", "user", "from_user" }) if (Has(el, k, JsonValueKind.Object)) { who = el.GetProperty(k); break; }
                        long uid = Num(who, "id");
                        if (uid == 0) { string urn = FirstStr(el, "sender_urn"); int ix = urn.LastIndexOf(':'); if (ix >= 0) long.TryParse(urn.Substring(ix + 1), out uid); }
                        f.UserId = uid;
                        f.Title = uid != 0 && uid == me ? "You" : FirstStr(who, "username", "full_name");
                        f.AvatarUrl = FirstStr(who, "avatar_url");
                        list.Add(f);
                    }
                    list.Sort((a, b) => string.CompareOrdinal(a.When, b.When));
                    return list;
                }
                catch (ApiException ex) when (ex.Status == 404 || ex.Status == 405) { last = ex; }
            }
            throw last ?? new ApiException(404, "Not available.");
        }

        readonly HashSet<string> deadFeedRoutes = new HashSet<string>(); // routes that answered 404 once: not asked again this session

        async Task<List<FeedItem>> GetFeedAsync(CancellationToken ct, bool messages, params string[] routes)
        {
            ApiException? last = null;
            foreach (var route in routes)
            {
                if (deadFeedRoutes.Contains(route)) continue;
                try
                {
                    string json = await SendAsync(HttpMethod.Get, route, null, ct, false);
                    var list = new List<FeedItem>();
                    using var doc = JsonDocument.Parse(json);
                    if (!TryItems(doc.RootElement, out var items, out _)) return list;
                    foreach (var el in items.EnumerateArray())
                    {
                        var f = ReadFeedItem(el, messages, Active?.UserId ?? 0);
                        if (f != null) list.Add(f);
                    }
                    return list;
                }
                catch (ApiException ex) when (ex.Status == 404 || ex.Status == 405) { last = ex; deadFeedRoutes.Add(route); Store.Log("feed route " + route.Split('?')[0] + " -> " + ex.Status, ex); }
            }
            // every route answered 404: forget them so the next click asks again instead of failing instantly
            deadFeedRoutes.Clear();
            throw last ?? new ApiException(404, "Not available.");
        }

        static string FirstStr(JsonElement e, params string[] keys)
        {
            foreach (var k in keys) { var v = Str(e, k); if (!string.IsNullOrWhiteSpace(v)) return v!; }
            return "";
        }

        static FeedItem? ReadFeedItem(JsonElement el, bool message, long myId)
        {
            try
            {
                var f = new FeedItem();
                // who it's from: a single user object, or the other people in a conversation
                JsonElement who = default;
                foreach (var k in new[] { "user", "from_user", "sender", "actor" })
                    if (Has(el, k, JsonValueKind.Object)) { who = el.GetProperty(k); break; }
                if (who.ValueKind != JsonValueKind.Object)
                {
                    foreach (var k in new[] { "users", "participants", "members" })
                        if (Has(el, k, JsonValueKind.Array) && el.GetProperty(k).GetArrayLength() > 0)
                        {
                            foreach (var u in el.GetProperty(k).EnumerateArray()) { who = u; if (Num(u, "id") != myId) break; }
                            break;
                        }
                }
                f.Title = FirstStr(who, "username", "full_name");
                f.AvatarUrl = FirstStr(who, "avatar_url");
                f.When = FirstStr(el, "created_at", "last_message_at", "updated_at");

                if (message)
                {
                    JsonElement lm = Has(el, "last_message", JsonValueKind.Object) ? el.GetProperty("last_message") : el;
                    f.Text = FirstStr(lm, "content", "body", "text", "message");
                    f.Unread = el.TryGetProperty("unread", out var un) && un.ValueKind == JsonValueKind.True;
                    f.Id = FirstStr(el, "id", "conversation_id");
                    if (f.Id.Length == 0 && el.TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.Number) f.Id = cid.GetRawText();
                    f.UserId = Num(who, "id");
                    if (f.Title.Length == 0) f.Title = "Conversation";
                    if (f.When.Length == 0) f.When = FirstStr(lm, "sent_at", "created_at");
                    return f;
                }

                string type = FirstStr(el, "type", "notification_type", "kind").ToLowerInvariant();
                string verb =
                    type.Contains("like") ? "liked" :
                    type.Contains("repost") ? "reposted" :
                    type.Contains("comment") ? "commented on" :
                    type.Contains("follow") || type.Contains("affiliation") ? "started following you" :
                    type.Contains("post") ? "posted a new track" :
                    type.Contains("mention") ? "mentioned you in" :
                    type.Contains("playlist") ? "added you to a playlist" :
                    type.Length > 0 ? type.Replace('-', ' ').Replace('_', ' ') : "has new activity";
                string subject = "";
                foreach (var k in new[] { "track", "playlist", "target", "resource" })
                    if (Has(el, k, JsonValueKind.Object)) { subject = FirstStr(el.GetProperty(k), "title", "name", "username"); if (subject.Length > 0) { f.Url = FirstStr(el.GetProperty(k), "permalink_url"); break; } }
                string comment = Has(el, "comment", JsonValueKind.Object) ? FirstStr(el.GetProperty("comment"), "body") : "";
                f.UserId = Num(who, "id");
                foreach (var k in new[] { "track", "target", "resource" })
                    if (Has(el, k, JsonValueKind.Object)) { long tid = Num(el.GetProperty(k), "id"); if (tid != 0 && FirstStr(el.GetProperty(k), "kind", "type").IndexOf("playlist", StringComparison.OrdinalIgnoreCase) < 0) { f.TrackId = tid; break; } }
                f.Text = verb + (subject.Length > 0 ? " " + subject : "") + (comment.Length > 0 ? ": " + comment : "");
                if (f.Title.Length == 0) f.Title = "SoundCloud";
                f.Unread = el.TryGetProperty("read", out var rd) && rd.ValueKind == JsonValueKind.False;
                return f;
            }
            catch { return null; }
        }
    }
}
