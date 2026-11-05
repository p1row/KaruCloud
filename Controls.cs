using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KaruCloud
{
    public static class Ui
    {
        public static readonly FontFamily Icons = new FontFamily("Segoe MDL2 Assets");

        public static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

        public static void Hover(Border b, string hoverHex)
        {
            var normal = b.Background;
            b.MouseEnter += (_, _) => b.Background = B(hoverHex);
            b.MouseLeave += (_, _) => b.Background = normal;
        }

        public static Border RoundBtn(TextBlock glyph, double size, SolidColorBrush bg, SolidColorBrush fg, string hoverHex, Action click)
        {
            glyph.FontFamily = Icons;
            glyph.FontSize = size > 38 ? 16 : 13;
            glyph.Foreground = fg;
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            var b = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
                Background = bg, Cursor = Cursors.Hand, Child = glyph,
            };
            b.MouseEnter += (_, _) => b.Background = B(hoverHex);
            b.MouseLeave += (_, _) => b.Background = bg;
            b.MouseLeftButtonUp += (_, _) => click();
            return b;
        }

        /// <summary>52800 -> "52.8K", 1848 -> "1,848" (like soundcloud.com).</summary>
        public static string Count(long n)
        {
            if (n < 0) n = 0;
            if (n < 10_000) return n.ToString("N0", CultureInfo.InvariantCulture);
            if (n < 1_000_000) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>ISO date from SoundCloud -> "3 days ago", "1 month ago".</summary>
        public static string Ago(string? iso)
        {
            if (string.IsNullOrEmpty(iso) || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return "";
            var s = DateTimeOffset.UtcNow - d;
            if (s < TimeSpan.Zero) s = TimeSpan.Zero;
            string Unit(int n, string u) => n + " " + u + (n == 1 ? "" : "s") + " ago";
            if (s.TotalMinutes < 1) return "just now";
            if (s.TotalHours < 1) return Unit((int)s.TotalMinutes, "minute");
            if (s.TotalDays < 1) return Unit((int)s.TotalHours, "hour");
            if (s.TotalDays < 30) return Unit((int)s.TotalDays, "day");
            if (s.TotalDays < 365) return Unit((int)(s.TotalDays / 30), "month");
            return Unit((int)(s.TotalDays / 365), "year");
        }

        public static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : t.Minutes + ":" + t.Seconds.ToString("00");
        }
    }

    /// <summary>Thin custom slider (the stock WPF one looks dated). Value runs 0..max.</summary>
    public sealed class MiniSlider : Grid
    {
        readonly Border fill, thumb;
        readonly int max;
        public int Value { get; private set; }
        public bool Dragging { get; private set; }
        public event Action<int, bool>? Changed; // value, committed (mouse released / wheel)

        public MiniSlider(int max, int value)
        {
            this.max = Math.Max(1, max);
            Height = 22; Background = Brushes.Transparent; Cursor = Cursors.Hand;
            Value = Math.Max(0, Math.Min(this.max, value));
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.B("#33FFFFFF"), VerticalAlignment = VerticalAlignment.Center };
            fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            thumb = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(track); Children.Add(fill); Children.Add(thumb);

            SizeChanged += (_, _) => Relayout();
            MouseLeftButtonDown += (_, e) => { Dragging = true; CaptureMouse(); FromPoint(e.GetPosition(this)); };
            MouseMove += (_, e) => { if (IsMouseCaptured) FromPoint(e.GetPosition(this)); };
            MouseLeftButtonUp += (_, _) =>
            {
                if (!IsMouseCaptured) return;
                ReleaseMouseCapture();
                Dragging = false;
                Changed?.Invoke(Value, true);
            };
        }

        /// <summary>Programmatic update (no event).</summary>
        public void SetValue(int v)
        {
            Value = Math.Max(0, Math.Min(max, v));
            Relayout();
        }

        void Relayout()
        {
            double usable = Math.Max(0, ActualWidth - 12);
            double x = usable * Value / max;
            fill.Width = x + 6;
            thumb.Margin = new Thickness(x, 0, 0, 0);
        }

        void FromPoint(Point p)
        {
            double usable = Math.Max(1, ActualWidth - 12);
            int v = (int)Math.Round(Math.Max(0, Math.Min(1, (p.X - 6) / usable)) * max);
            if (v == Value) return;
            Value = v; Relayout(); Changed?.Invoke(v, false);
        }

        public void Wheel(int deltaSteps)
        {
            int v = Math.Max(0, Math.Min(max, Value + deltaSteps));
            if (v == Value) return;
            Value = v; Relayout(); Changed?.Invoke(v, true);
        }
    }

    /// <summary>A row in a track / playlist list.</summary>
    public sealed class ItemVM : INotifyPropertyChanged
    {
        static readonly SolidColorBrush Orange = Ui.B("#FF7A3D");
        static readonly SolidColorBrush White = Ui.B("#F2F2F2");
        static readonly SolidColorBrush Red = Ui.B("#FF7A3D");
        static readonly SolidColorBrush Dim = Ui.B("#9A9A9A");

        /// <summary>Set by the main window: is this track liked / reposted by the signed-in account?</summary>
        public static Func<long, bool> IsLiked = _ => false;
        public static Func<long, bool> IsReposted = _ => false;

        public string Kind = "track"; // track | playlist | user | station | header | more
        public ScTrack? Track;
        public ScPlaylist? Playlist;
        public ScUser? User;
        public string StationKey = ""; // soundcloud:track-stations:ID / soundcloud:artist-stations:ID
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string DurationText { get; set; } = "";
        public string ArtUrl = "";

        bool playing;
        ImageSource? art;
        bool artRequested;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public bool IsPlaying
        {
            get => playing;
            set { if (playing == value) return; playing = value; Raise(nameof(IsPlaying)); Raise(nameof(TitleBrush)); }
        }

        public Brush TitleBrush => Kind == "header" ? Orange : (playing ? Orange : White);

        bool Liked => Track != null && IsLiked(Track.Id);
        bool Reposted => Track != null && IsReposted(Track.Id);
        public string LikeGlyph => Liked ? "\uEB52" : "\uEB51";
        public Brush LikeBrush => Liked ? Red : Dim;
        public Brush RepostBrush => Reposted ? Orange : Dim;
        public Visibility ActionsVis => Kind == "track" && Track != null ? Visibility.Visible : Visibility.Collapsed;
        public bool CanPlay => (Kind == "track" && Track != null) || Kind == "playlist" || Kind == "station";
        public Cursor TitleCursor => Kind == "header" ? Cursors.Arrow : Cursors.Hand;

        /// <summary>Call after a like / repost changed so the heart and repost icons in the row repaint.</summary>
        public void RefreshFlags()
        {
            Raise(nameof(LikeGlyph)); Raise(nameof(LikeBrush)); Raise(nameof(RepostBrush));
        }

        public ImageSource? Art
        {
            get
            {
                if (!artRequested && ArtUrl.Length > 0) { artRequested = true; _ = LoadArtAsync(); }
                return art;
            }
        }

        async Task LoadArtAsync()
        {
            var img = await ArtCache.GetAsync(ArtUrl, 96);
            if (img != null) { art = img; Raise(nameof(Art)); }
        }

        public static string BestArt(string? artwork, string? avatar) =>
            !string.IsNullOrEmpty(artwork) ? artwork! : (avatar ?? "");

        public static ItemVM From(ScTrack t) => new ItemVM
        {
            Kind = "track", Track = t,
            Title = t.Title, Artist = t.Via.Length > 0 ? t.Via + "  ·  " + t.ArtistName : t.ArtistName,
            DurationText = Ui.Fmt(TimeSpan.FromMilliseconds(t.Duration)),
            ArtUrl = BestArt(t.ArtworkUrl, t.User?.AvatarUrl),
        };

        public static ItemVM From(ScPlaylist p)
        {
            string art = BestArt(p.ArtworkUrl, "");
            if (art.Length == 0 && p.Tracks != null && p.Tracks.Count > 0) art = BestArt(p.Tracks[0].ArtworkUrl, p.Tracks[0].User?.AvatarUrl);
            if (art.Length == 0) art = p.User?.AvatarUrl ?? "";
            string kind = p.IsSystem ? "Mix" : (string.Equals(p.SetType, "album", StringComparison.OrdinalIgnoreCase) ? "Album" : "Playlist");
            return new ItemVM
            {
                Kind = "playlist", Playlist = p,
                Title = p.Title,
                Artist = (p.Via.Length > 0 ? p.Via + "  ·  " : "") + kind + (p.TrackCount > 0 ? "  ·  " + p.TrackCount + " tracks" : "") + (p.User != null ? "  ·  " + p.User.Username : ""),
                ArtUrl = art,
            };
        }

        public static ItemVM From(ScUser u)
        {
            string meta = u.Followers > 0 ? u.Followers.ToString("N0") + " followers" : "Artist";
            if (u.TrackCount > 0) meta += "  ·  " + u.TrackCount + " tracks";
            return new ItemVM { Kind = "user", User = u, Title = u.Username ?? "", Artist = meta, ArtUrl = u.AvatarUrl ?? "" };
        }

        public static ItemVM Station(ScTrack t) => new ItemVM
        {
            Kind = "station", Track = t, StationKey = "soundcloud:track-stations:" + t.Id,
            Title = "Station: " + t.Title, Artist = "Based on " + t.ArtistName,
            ArtUrl = BestArt(t.ArtworkUrl, t.User?.AvatarUrl),
        };

        public static ItemVM ArtistStation(ScUser u) => new ItemVM
        {
            Kind = "station", User = u, StationKey = "soundcloud:artist-stations:" + u.Id,
            Title = "Station: " + u.Username, Artist = "Artist station", ArtUrl = u.AvatarUrl ?? "",
        };

        public static ItemVM Header(string title) => new ItemVM { Kind = "header", Title = title };

        public static ItemVM More() => new ItemVM { Kind = "more", Title = "Load more…" };
    }

    /// <summary>Small in-memory artwork cache (session only, nothing is written to disk).</summary>
    public static class ArtCache
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>();
        static readonly LinkedList<string> order = new LinkedList<string>();
        static readonly object gate = new object();
        static ScApi? api;

        public static void Init(ScApi a) => api = a;

        public static async Task<ImageSource?> GetAsync(string url, int decodeWidth)
        {
            string key = url + "|" + decodeWidth;
            lock (gate) { if (cache.TryGetValue(key, out var hit)) return hit; }
            try
            {
                if (api == null) return null;
                var bytes = await api.GetBytesAsync(url, default);
                var img = await Task.Run<ImageSource>(() =>
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.DecodePixelWidth = decodeWidth;
                    bi.StreamSource = new MemoryStream(bytes);
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                });
                lock (gate)
                {
                    cache[key] = img; order.AddLast(key);
                    if (order.Count > 160) { cache.Remove(order.First!.Value); order.RemoveFirst(); }
                }
                return img;
            }
            catch { return null; }
        }

        /// <summary>SoundCloud artwork URLs end in a size token (-large, -t500x500, ...).</summary>
        public static string Size(string url, string token)
        {
            int dot = url.LastIndexOf('.');
            int dash = url.LastIndexOf('-');
            if (dash < 0 || dot < dash) return url;
            return url.Substring(0, dash + 1) + token + url.Substring(dot);
        }
    }
}
