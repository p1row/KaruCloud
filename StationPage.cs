using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KaruCloud
{
    /// <summary>
    /// A station (made from a song or an artist) shown like soundcloud.com's station page: tinted banner with the play button,
    /// the song's title, "Track station", a track / time badge and the STATION card, then the numbered list next to the
    /// "Artists featured" column.
    /// </summary>
    public sealed partial class MainWindow
    {
        UIElement BuildStationDetail(PaneTab t)
        {
            var pl = t.DPlaylist!;
            var refreshers = new List<Action>();
            detailRefresh = () => { foreach (var a in refreshers.ToList()) a(); };
            cardTitles.Clear();

            var trackRows = t.Rows.Where(r => r.Kind == "track" && r.Track != null).ToList();
            long totalMs = trackRows.Sum(r => r.Track!.Duration);
            var seed = t.StationSeed;
            var seedArtist = t.StationArtist;

            string title = seed?.Title is { Length: > 0 } st ? st : (seedArtist?.Username is { Length: > 0 } sn ? sn : (pl.Title.Length > 0 ? pl.Title : "Station"));
            string kindText = seedArtist != null ? "Artist station" : "Track station";
            string art = seed != null ? ItemVM.BestArt(seed.ArtworkUrl, seed.User?.AvatarUrl)
                : seedArtist != null ? (seedArtist.AvatarUrl ?? "")
                : (trackRows.FirstOrDefault()?.ArtUrl ?? "");
            string basedOnUrl = seed?.PermalinkUrl ?? seedArtist?.PermalinkUrl ?? "";

            // people on this station, most frequent first
            var featured = trackRows.Select(r => r.Track!.User).Where(u => u != null && u.Id != 0 && !string.IsNullOrWhiteSpace(u.Username))
                .GroupBy(u => u!.Id).OrderByDescending(g => g.Count()).Select(g => g.First()!).Take(8).ToList();

            var page = new StackPanel { Margin = new Thickness(20, 14, 20, 28) };

            // ---- banner ----
            var banner = new Border
            {
                CornerRadius = new CornerRadius(16), Height = 270, Padding = new Thickness(26, 24, 26, 24),
                Background = new LinearGradientBrush(Ui.B("#FF8C6F7C").Color, Ui.B("#FF4A5E66").Color, 20),
            };
            var bg = new Grid();
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new Grid { Margin = new Thickness(0, 0, 22, 0) };
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var playGlyph = new TextBlock { Text = "\uE768", FontFamily = Ui.Icons, FontSize = 24, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            var playBtn = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Ui.B("#FF111111"), Cursor = Cursors.Hand, Child = playGlyph,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0), ToolTip = "Play / pause",
            };
            Ui.Hover(playBtn, "#FF000000");
            playBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; PlayPlaylistPage(t, trackRows); };
            bool InThisList() => current != null && trackRows.Any(r => r.Track!.Id == current.Id);
            detailPlaylistSync = () => playGlyph.Text = player.IsPlaying && InThisList() ? "\uE769" : "\uE768";
            detailPlaylistSync();

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            info.Children.Add(new Border
            {
                Background = Ui.B("#FF111111"), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            });
            var kindRow = new StackPanel { Orientation = Orientation.Horizontal };
            kindRow.Children.Add(new TextBlock { Text = "\u25C9", FontSize = 12, Foreground = Ui.B("#9A9A9A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            kindRow.Children.Add(new TextBlock { Text = kindText, Foreground = Ui.B("#9A9A9A"), FontSize = 14, FontWeight = FontWeights.SemiBold });
            info.Children.Add(new Border { Background = Ui.B("#FF111111"), Padding = new Thickness(8, 3, 10, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Child = kindRow });
            Grid.SetColumn(playBtn, 0); Grid.SetColumn(info, 1);
            top.Children.Add(playBtn); top.Children.Add(info);
            Grid.SetRow(top, 0); left.Children.Add(top);

            // track count / total time badge (bottom left)
            var badgeText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            int count = trackRows.Count;
            badgeText.Children.Add(new TextBlock { Text = count.ToString(), Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            badgeText.Children.Add(new TextBlock { Text = count == 1 ? "TRACK" : "TRACKS", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            if (totalMs > 0) badgeText.Children.Add(new TextBlock { Text = Ui.Fmt(TimeSpan.FromMilliseconds(totalMs)), Foreground = Ui.B("#BBBBBB"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
            var badge = new Border
            {
                Width = 112, Height = 112, CornerRadius = new CornerRadius(56), Background = Ui.B("#FF111111"), Child = badgeText,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetRow(badge, 1); left.Children.Add(badge);
            Grid.SetColumn(left, 0); bg.Children.Add(left);

            // the STATION card: round cover, two of the artists, the label
            var card = new Grid { Width = 222, Height = 222, VerticalAlignment = VerticalAlignment.Center };
            card.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Ui.B("#FF111111") });
            var cover = ArtBox(art, 172, true, "t300x300", 344);
            cover.HorizontalAlignment = HorizontalAlignment.Center; cover.VerticalAlignment = VerticalAlignment.Top; cover.Margin = new Thickness(0, 10, 0, 0);
            card.Children.Add(cover);
            Border Bubble(ScUser u, double size, HorizontalAlignment h, VerticalAlignment v, Thickness m)
            {
                var ring = new Border
                {
                    Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderBrush = Ui.B("#FF111111"), BorderThickness = new Thickness(3),
                    HorizontalAlignment = h, VerticalAlignment = v, Margin = m, Child = ArtBox(u.AvatarUrl ?? "", size - 6, true, "t200x200", 128), Background = Ui.B("#FF111111"),
                };
                return ring;
            }
            if (featured.Count > 0) card.Children.Add(Bubble(featured[0], 62, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(8, 8, 0, 0)));
            if (featured.Count > 1) card.Children.Add(Bubble(featured[1], 54, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 118, 6, 0)));
            var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 0, 12) };
            label.Children.Add(new TextBlock { Text = "\u25C9", FontSize = 16, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            label.Children.Add(new TextBlock { Text = "STATION", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold });
            card.Children.Add(label);
            Grid.SetColumn(card, 1); bg.Children.Add(card);
            banner.Child = bg;
            _ = TintBannerAsync(art, banner);
            page.Children.Add(banner);

            // ---- action buttons ----
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 4) };
            if (seed != null)
            {
                var like = new FeedBtn("\uEB51", "Like", "Like the song this station is based on", () => ToggleLikeOf(seed));
                void SyncLike()
                {
                    bool l = likedIds.Contains(seed.Id);
                    like.Set(l ? "\uEB52" : "\uEB51", l ? "Liked" : "Like", l ? "#FF7A3D" : "#F2F2F2");
                }
                SyncLike(); refreshers.Add(SyncLike);
                actions.Children.Add(like.Root);
            }
            actions.Children.Add(new FeedBtn("\uE72D", "Share", "Copy link", () => { if (basedOnUrl.Length > 0) { CopyLink(basedOnUrl); ShowToast("Link copied", true); } }).Root);
            actions.Children.Add(new FeedBtn("\uE8F4", "Add to Next up", "Add all to the queue", () =>
            {
                foreach (var r in trackRows) QueueAddQuiet(r.Track!);
                ShowToast("Added " + trackRows.Count + " tracks to the queue", true);
            }).Root);
            actions.Children.Add(new FeedBtn("\uE8B1", "Shuffle", "Shuffle play", () => ShufflePlaylistPage(t, trackRows)).Root);
            if (basedOnUrl.Length > 0) actions.Children.Add(new FeedBtn("\uE8A7", "", "Open on SoundCloud", () => OpenUrl(basedOnUrl)).Root);
            page.Children.Add(actions);

            // ---- body: numbered list | based on + artists featured ----
            var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rightCol = new ColumnDefinition { Width = new GridLength(300) };
            body.ColumnDefinitions.Add(rightCol);

            var mid = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };
            if (trackRows.Count == 0) mid.Children.Add(Txt("No tracks on this station yet.", 13, "#808080"));
            int n = 0;
            foreach (var r in trackRows) mid.Children.Add(BuildPlaylistRow(t, r, ++n, trackRows, refreshers));
            Grid.SetColumn(mid, 0); body.Children.Add(mid);

            var right = new StackPanel();
            if (title.Length > 0)
            {
                var based = new WrapPanel { Margin = new Thickness(0, 2, 0, 22) };
                based.Children.Add(Txt("Based on  ", 13, "#9A9A9A", true));
                string name = seed != null ? (seed.ArtistName.Length > 0 ? seed.ArtistName + " - " : "") + seed.Title : title;
                based.Children.Add(seed != null ? Link(name, 13, "#F2F2F2", () => OpenTrack(seed), true) : Link(name, 13, "#F2F2F2", () => OpenArtist(seedArtist), true));
                right.Children.Add(based);
            }
            if (featured.Count > 0)
            {
                right.Children.Add(new TextBlock { Text = "ARTISTS FEATURED", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 14) });
                foreach (var u in featured) right.Children.Add(FeaturedArtistRow(u));
            }
            Grid.SetColumn(right, 1); body.Children.Add(right);
            body.SizeChanged += (_, e) => rightCol.Width = e.NewSize.Width < 820 ? new GridLength(0) : new GridLength(300);
            page.Children.Add(body);
            return page;
        }

        /// <summary>Avatar, name, followers / tracks and a Follow button for one artist on a station.</summary>
        FrameworkElement FeaturedArtistRow(ScUser u)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var av = ArtBox(u.AvatarUrl ?? "", 50, true, "t200x200", 100);
            av.Cursor = Cursors.Hand; av.VerticalAlignment = VerticalAlignment.Center;
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(u); };

            var info = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = Link(u.Username ?? "", 13.5, "#FFFFFF", () => OpenArtist(u), true);
            name.MaxWidth = 120;
            info.Children.Add(name);
            if (u.Followers > 0 || u.TrackCount > 0) info.Children.Add(PersonCounts(u, new Thickness(0, 3, 0, 0)));

            var follow = MakeFollowBtn(u);
            follow.MinWidth = 88; follow.Margin = new Thickness(0); follow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(info, 1); Grid.SetColumn(follow, 2);
            g.Children.Add(av); g.Children.Add(info); g.Children.Add(follow);
            return g;
        }
    }
}
