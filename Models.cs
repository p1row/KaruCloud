using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaruCloud
{
    public sealed class Settings
    {
        // Discord Rich Presence
        /// <summary>Kept only so old config.json files still load. The app always uses <see cref="DiscordRpc.AppId"/> now.</summary>
        public string DiscordAppId { get; set; } = "";
        public bool ShowBrowsing { get; set; } = false;
        public bool ShowSong { get; set; } = true;
        public bool HideWhilePaused { get; set; } = true;
        public bool ShowTimestamps { get; set; } = true;
        public bool ShowCover { get; set; } = true;
        public bool LinkButtons { get; set; } = true; // keeps a backlink to the track on SoundCloud
        public string StatusDisplay { get; set; } = "artist"; // artist | song | app

        // App
        public bool CloseToTray { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartMinimized { get; set; } = false;
        public bool LaunchAtLogin { get; set; } = false;
        public int Volume { get; set; } = 80;
        public bool Shuffle { get; set; } = false;
        public string Repeat { get; set; } = "off"; // off | all | one
        public bool Autoplay { get; set; } = true; // keep playing related tracks when the queue runs out
    }

    public sealed class Account
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public long UserId { get; set; }
        public string AccessTokenEnc { get; set; } = ""; // SoundCloud session token, DPAPI-protected
        public string AvatarUrl { get; set; } = "";      // profile picture shown in the header
    }

    public sealed class Config
    {
        public Settings Settings { get; set; } = new Settings();
        public List<Account> Accounts { get; set; } = new List<Account>();
        public string Current { get; set; } = "";

        /// <summary>A hand-edited or half-written config.json can contain nulls; never let that crash startup.</summary>
        public void Normalize()
        {
            Settings ??= new Settings();
            Accounts ??= new List<Account>();
            Accounts.RemoveAll(a => a == null);
            Current ??= "";
        }
    }

    public sealed class NowPlaying
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public bool Playing { get; set; }
    }

    // ---------- SoundCloud (api-v2, same JSON the website uses) ----------
    public sealed class ScVisual
    {
        [JsonPropertyName("visual_url")] public string? Url { get; set; }
    }

    public sealed class ScVisualSet
    {
        [JsonPropertyName("visuals")] public List<ScVisual>? Items { get; set; }
    }

    public sealed class ScUser
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("permalink_url")] public string? PermalinkUrl { get; set; }
        [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        [JsonPropertyName("followers_count")] public long Followers { get; set; }
        [JsonPropertyName("track_count")] public int TrackCount { get; set; }
        [JsonPropertyName("followings_count")] public long? Following { get; set; }
        [JsonPropertyName("playlist_count")] public int? PlaylistCount { get; set; }
        [JsonPropertyName("likes_count")] public long? LikesCount { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
        [JsonPropertyName("verified")] public bool? Verified { get; set; }
        [JsonPropertyName("visuals")] public ScVisualSet? Visuals { get; set; }

        /// <summary>The profile banner, if the artist set one.</summary>
        [JsonIgnore] public string BannerUrl => Visuals?.Items?.FirstOrDefault()?.Url ?? "";
    }

    public sealed class ScFormat
    {
        [JsonPropertyName("protocol")] public string? Protocol { get; set; }
        [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
    }

    public sealed class ScTranscoding
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("preset")] public string? Preset { get; set; }
        [JsonPropertyName("snipped")] public bool Snipped { get; set; }
        [JsonPropertyName("format")] public ScFormat? Format { get; set; }
    }

    public sealed class ScMedia
    {
        [JsonPropertyName("transcodings")] public List<ScTranscoding>? Transcodings { get; set; }
    }

    public sealed class ScPublisher
    {
        [JsonPropertyName("artist")] public string? Artist { get; set; }
    }

    public sealed class ScTrack
    {
        string title = "", permalink = "";
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("title")] public string? TitleRaw { get => title; set => title = value ?? ""; }
        [JsonPropertyName("duration")] public long Duration { get; set; }
        [JsonPropertyName("artwork_url")] public string? ArtworkUrl { get; set; }
        [JsonPropertyName("permalink_url")] public string? PermalinkRaw { get => permalink; set => permalink = value ?? ""; }
        [JsonPropertyName("access")] public string? Access { get; set; }
        [JsonPropertyName("policy")] public string? Policy { get; set; }
        [JsonPropertyName("track_authorization")] public string? TrackAuthorization { get; set; }
        [JsonPropertyName("publisher_metadata")] public ScPublisher? Publisher { get; set; }
        [JsonPropertyName("media")] public ScMedia? Media { get; set; }
        [JsonPropertyName("user")] public ScUser? User { get; set; }
        // counts and extras shown on a song's own page (nullable: SoundCloud sends null for some tracks)
        [JsonPropertyName("playback_count")] public long? PlaybackCount { get; set; }
        [JsonPropertyName("likes_count")] public long? LikesCount { get; set; }
        [JsonPropertyName("reposts_count")] public long? RepostsCount { get; set; }
        [JsonPropertyName("comment_count")] public long? CommentCount { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("genre")] public string? Genre { get; set; }
        [JsonPropertyName("waveform_url")] public string? WaveformUrl { get; set; }
        [JsonPropertyName("visuals")] public ScVisualSet? Visuals { get; set; }

        /// <summary>The wide picture the artist put behind the song on its page (SoundCloud "visuals"), if any.</summary>
        [JsonIgnore] public string BannerUrl => Visuals?.Items?.FirstOrDefault(v => !string.IsNullOrEmpty(v.Url))?.Url ?? "";

        [JsonIgnore] public bool UserFavorite { get; set; }
        [JsonIgnore] public string Via { get; set; } = ""; // "↻ Someone reposted" in the feed
        [JsonIgnore] public string PostedAt { get; set; } = ""; // feed: when it was posted / reposted (ISO)
        [JsonIgnore] public ScUser? Poster { get; set; }        // feed: the person who reposted it (null for an original post)
        [JsonIgnore] public string Title => title;
        [JsonIgnore] public string PermalinkUrl => permalink;
        [JsonIgnore] public string Key => Id.ToString();
        /// <summary>Playlists only list the first few tracks in full; the rest are bare ids that must be fetched.</summary>
        [JsonIgnore] public bool IsStub => title.Length == 0 && Id != 0;
        [JsonIgnore] public bool IsBlocked =>
            string.Equals(Access, "blocked", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Policy, "BLOCK", StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public string ArtistName =>
            !string.IsNullOrWhiteSpace(Publisher?.Artist) ? Publisher!.Artist! : (User?.Username ?? "");
    }

    public sealed class ScPlaylist
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("title")] public string? TitleRaw { get; set; }
        [JsonPropertyName("track_count")] public int TrackCount { get; set; }
        [JsonPropertyName("artwork_url")] public string? ArtworkUrl { get; set; }
        [JsonPropertyName("permalink_url")] public string? PermalinkUrl { get; set; }
        [JsonPropertyName("user")] public ScUser? User { get; set; }
        [JsonPropertyName("tracks")] public List<ScTrack>? Tracks { get; set; }
        [JsonPropertyName("urn")] public string? Urn { get; set; }
        [JsonPropertyName("set_type")] public string? SetType { get; set; }
        [JsonPropertyName("genre")] public string? Genre { get; set; }
        [JsonPropertyName("tag_list")] public string? TagList { get; set; }
        [JsonPropertyName("sharing")] public string? Sharing { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("likes_count")] public long LikesCount { get; set; }
        [JsonPropertyName("last_modified")] public string? LastModified { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }

        [JsonIgnore] public bool IsPrivate => string.Equals(Sharing, "private", StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool IsSystem { get; set; } // "Mixed for you", daily mixes, stations...
        [JsonIgnore] public string Via { get; set; } = "";
        [JsonIgnore] public string PostedAt { get; set; } = "";
        [JsonIgnore] public ScUser? Poster { get; set; }
        [JsonIgnore] public string Title => TitleRaw ?? "";
        /// <summary>Normal playlists use their number; system playlists only have a "soundcloud:system-playlists:..." id.</summary>
        [JsonIgnore] public string Key => Id != 0 ? Id.ToString() : (Urn ?? "");
    }

    /// <summary>One row of a mixed list (feed, home, search...): a track, playlist, person or a section header.</summary>
    public sealed class ScEntry
    {
        public string Kind = "track"; // track | playlist | user | header
        public ScTrack? Track;
        public ScPlaylist? Playlist;
        public ScUser? User;
        public string Title = "";
    }

    public sealed class ScPage<T>
    {
        public List<T> Collection { get; set; } = new List<T>();
        public string? NextHref { get; set; }
        /// <summary>Search only: how many tracks / people / playlists / albums were found in total (\"track\" -> 33 ...). Empty when SoundCloud didn't say.</summary>
        public Dictionary<string, long> Facets { get; set; } = new Dictionary<string, long>();
    }

    public sealed class ScMe
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    }

    /// <summary>A comment on a track. Timestamp is the position in the song (ms) the comment was left at; null for a plain comment.</summary>
    public sealed class ScComment
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("timestamp")] public long? Timestamp { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
        [JsonPropertyName("user")] public ScUser? User { get; set; }
        [JsonPropertyName("track")] public ScTrack? Track { get; set; }
    }

    public sealed class ScWebProfile
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
    }

    /// <summary>One line of the notification / message menus.</summary>
    public sealed class FeedItem
    {
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
        public string When { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Unread { get; set; }
        public string Id { get; set; } = "";   // conversation id (messages)
        public long TrackId { get; set; }      // notification about a song: opens its page inside the app
        public long UserId { get; set; }       // ... or about a person: opens their profile
    }

    // ---------- Storage ----------
    public static class Store
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaruCloud");

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static T Load<T>(string file) where T : new()
        {
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Dir, file)), Json) ?? new T(); }
            catch { return new T(); }
        }

        public static void Save(string file, object o)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, file);
                string tmp = path + ".tmp"; // write-then-replace so a crash mid-save can't leave a broken config
                File.WriteAllText(tmp, JsonSerializer.Serialize(o, Json));
                File.Move(tmp, path, true);
            }
            catch { }
        }

        public static void Log(string what, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, "crash.log");
                if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + what + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>The session token is encrypted with Windows DPAPI (tied to your Windows user).</summary>
    public static class Secrets
    {
        public static string Protect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(b);
        }

        public static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try
            {
                var b = ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(b);
            }
            catch { return ""; }
        }
    }

    /// <summary>Texts used in Discord Rich Presence (English only).</summary>
    public sealed class Strings
    {
        public string Browsing = "Browsing music", Paused = "Paused", Listen = "Listen on SoundCloud", Artist = "View Artist";
        public static readonly Strings En = new Strings();
    }
}
