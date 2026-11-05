using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KaruCloud
{
    public sealed partial class MainWindow
    {
        Action? detailPlaylistSync; // repaints the big play button of the open playlist page

        // ================= playlist / album page =================
        UIElement BuildPlaylistDetail(PaneTab t)
        {
            if (t.IsStation) return BuildStationDetail(t);
            var pl = t.DPlaylist!;
            var refreshers = new List<Action>();
            detailRefresh = () => { foreach (var a in refreshers.ToList()) a(); };
            cardTitles.Clear();

            var trackRows = t.Rows.Where(r => r.Kind == "track" && r.Track != null).ToList();
            long totalMs = trackRows.Sum(r => r.Track!.Duration);
            var page = new StackPanel { Margin = new Thickness(20, 14, 20, 28) };

            // ---- banner: play + title + owner on the left, cover on the right ----
            var banner = new Border
            {
                CornerRadius = new CornerRadius(16), Height = 270, Padding = new Thickness(26, 24, 26, 24),
                Background = new LinearGradientBrush(Ui.B("#FF6E6A6A").Color, Ui.B("#FF3A3737").Color, 20),
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
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
                Child = new TextBlock { Text = pl.Title, Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            });
            if (pl.User != null)
                info.Children.Add(new Border
                {
                    Background = Ui.B("#FF111111"), Padding = new Thickness(8, 2, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
                    Child = Link(pl.User.Username ?? "", 14, "#C8C8C8", () => OpenArtist(pl.User)),
                });
            Grid.SetColumn(playBtn, 0); Grid.SetColumn(info, 1);
            top.Children.Add(playBtn); top.Children.Add(info);

            string when = Ui.Ago(!string.IsNullOrEmpty(pl.LastModified) ? pl.LastModified : pl.CreatedAt);
            if (when.Length > 0)
            {
                var upd = Txt("Updated " + when, 12, "#F0F0F0", true);
                upd.VerticalAlignment = VerticalAlignment.Top; upd.Margin = new Thickness(12, 4, 0, 0);
                Grid.SetColumn(upd, 2); top.Children.Add(upd);
            }
            Grid.SetRow(top, 0); left.Children.Add(top);

            // track count / total time badge (bottom left)
            var badgeText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            int count = trackRows.Count > 0 ? trackRows.Count : pl.TrackCount;
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

            // cover (the playlist's artwork, or the first song's)
            string art = !string.IsNullOrEmpty(pl.ArtworkUrl) ? pl.ArtworkUrl! : (trackRows.FirstOrDefault()?.ArtUrl ?? "");
            var cover = ArtBox(art, 222, false, "t500x500", 444);
            cover.VerticalAlignment = VerticalAlignment.Center;
            cover.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35 };
            Grid.SetColumn(cover, 1); bg.Children.Add(cover);
            banner.Child = bg;
            _ = TintBannerAsync(art, banner);
            page.Children.Add(banner);

            // ---- action buttons ----
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
            actions.Children.Add(SquareBtn("\uE72D", "Copy link", () => { if (!string.IsNullOrEmpty(pl.PermalinkUrl)) { CopyLink(pl.PermalinkUrl); ShowToast("Link copied", true); } }));
            actions.Children.Add(SquareBtn("\uE8F4", "Add all to queue", () =>
            {
                foreach (var r in trackRows) QueueAddQuiet(r.Track!);
                ShowToast("Added " + trackRows.Count + " tracks to the queue", true);
            }));
            actions.Children.Add(SquareBtn("\uE8B1", "Shuffle play", () => ShufflePlaylistPage(t, trackRows)));
            if (pl.Id != 0 && !pl.IsSystem)
            {
                actions.Children.Add(SquareBtn("\uEB51", "Like playlist", () => _ = LikePlaylistAsync(pl, true)));
                actions.Children.Add(SquareBtn("\uEB52", "Unlike playlist", () => _ = LikePlaylistAsync(pl, false)));
            }
            if (!string.IsNullOrEmpty(pl.PermalinkUrl)) actions.Children.Add(SquareBtn("\uE8A7", "Open on SoundCloud", () => OpenUrl(pl.PermalinkUrl)));
            page.Children.Add(actions);

            // ---- body: owner column | track list | info column ----
            var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rightCol = new ColumnDefinition { Width = new GridLength(250) };
            body.ColumnDefinitions.Add(rightCol);

            // owner
            var owner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Width = 100 };
            if (pl.User != null)
            {
                var av = ArtBox(pl.User.AvatarUrl ?? "", 94, true, "t200x200", 190);
                av.HorizontalAlignment = HorizontalAlignment.Left; av.Cursor = Cursors.Hand;
                av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(pl.User); };
                owner.Children.Add(av);
                var nm = Link(pl.User.Username ?? "", 12.5, "#FFFFFF", () => OpenArtist(pl.User), true);
                nm.Margin = new Thickness(0, 8, 0, 0); nm.MaxWidth = 100;
                owner.Children.Add(nm);
                if (pl.User.Followers > 0)
                {
                    var f = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                    f.Children.Add(new TextBlock { Text = "\uE77B", FontFamily = Ui.Icons, FontSize = 11, Foreground = Ui.B("#9a9a9a"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                    f.Children.Add(Txt(Ui.Count(pl.User.Followers), 11.5, "#9a9a9a"));
                    owner.Children.Add(f);
                }
            }
            Grid.SetColumn(owner, 0); body.Children.Add(owner);

            // list column
            var mid = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
            if (pl.IsPrivate)
            {
                var lockRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
                lockRow.Children.Add(new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Background = Ui.B("#FF2A2A2A"), Margin = new Thickness(0, 0, 8, 0),
                    Child = new TextBlock { Text = "\uE72E", FontFamily = Ui.Icons, FontSize = 11, Foreground = Ui.B("#bbbbbb"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                lockRow.Children.Add(new TextBlock { Text = "This playlist is private.", Foreground = Ui.B("#9a9a9a"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                mid.Children.Add(lockRow);
            }
            var tags = PlaylistTags(pl);
            if (tags.Count > 0)
            {
                var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
                foreach (var tg in tags)
                {
                    string q = tg;
                    var chip = new Border
                    {
                        CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 6), Background = Ui.B("#FF262626"), Cursor = Cursors.Hand,
                        Child = new TextBlock { Text = "# " + tg, Foreground = Ui.B("#dddddd"), FontSize = 12, FontWeight = FontWeights.SemiBold },
                    };
                    Ui.Hover(chip, "#FF333333");
                    chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; NavigateHere(SearchKind(), Cut(q, 24), q); };
                    chip.Tag = (Action)(() => NavigateHere(SearchKind(), Cut(q, 24), q));
                    chips.Children.Add(chip);
                }
                mid.Children.Add(chips);
            }

            if (trackRows.Count == 0) mid.Children.Add(Txt("No playable tracks in this playlist.", 13, "#808080"));
            int n = 0;
            foreach (var r in trackRows) mid.Children.Add(BuildPlaylistRow(t, r, ++n, trackRows, refreshers));
            Grid.SetColumn(mid, 1); body.Children.Add(mid);

            // info column: counters and description
            var right = new StackPanel();
            var stats = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            void Stat(string glyph, string text)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 12, Foreground = Ui.B("#9a9a9a"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                sp.Children.Add(Txt(text, 12.5, "#bbbbbb", true));
                stats.Children.Add(sp);
            }
            if (pl.LikesCount > 0) Stat("\uEB51", Ui.Count(pl.LikesCount));
            Stat("\uE8D6", count + (count == 1 ? " track" : " tracks"));
            right.Children.Add(stats);
            if (!string.IsNullOrWhiteSpace(pl.Description))
                right.Children.Add(SelText(pl.Description!.Trim(), 12.5, "#c4c4c4"));
            Grid.SetColumn(right, 2); body.Children.Add(right);
            body.SizeChanged += (_, e) => rightCol.Width = e.NewSize.Width < 820 ? new GridLength(0) : new GridLength(250);
            page.Children.Add(body);
            return page;
        }

        // numbered row: "1  [cover]  artist · title                 ▶ plays  ♡  3:41"
        FrameworkElement BuildPlaylistRow(PaneTab t, ItemVM it, int number, List<ItemVM> all, List<Action> refreshers)
        {
            var tr = it.Track!;
            var row = new Border { Height = 50, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Padding = new Thickness(4, 0, 6, 0) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var num = new TextBlock { Text = number.ToString(), Foreground = Ui.B("#8a8a8a"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var playOv = new TextBlock { Text = "\uE768", FontFamily = Ui.Icons, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Cursor = Cursors.Hand, ToolTip = "Play" };
            playOv.MouseLeftButtonUp += (_, e) => { e.Handled = true; PlayFromList(all, it); };
            var numHost = new Grid(); numHost.Children.Add(num); numHost.Children.Add(playOv);
            Grid.SetColumn(numHost, 0);

            var art = ArtBox(it.ArtUrl, 38, false, "t200x200", 80);
            art.CornerRadius = new CornerRadius(4);
            art.HorizontalAlignment = HorizontalAlignment.Left; art.VerticalAlignment = VerticalAlignment.Center; art.Cursor = Cursors.Hand;
            art.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenTrack(tr); };
            art.Tag = (Action)(() => OpenTrack(tr));
            Grid.SetColumn(art, 1);

            // "artist · title" on one line, like soundcloud.com's playlist rows
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var artist = Link(it.Artist, 12.5, "#8A8A8A", () => OpenArtist(tr.User));
            artist.MaxWidth = 190;
            var dot = Txt("  ·  ", 12.5, "#6a6a6a");
            var title = Link(it.Title, 13.5, "#F2F2F2", () => OpenTrack(tr), true);
            title.Foreground = it.TitleBrush;
            cardTitles.Add((it, title));
            line.Children.Add(artist); line.Children.Add(dot); line.Children.Add(title);
            var lineHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
            lineHost.Children.Add(line);
            lineHost.ClipToBounds = true;
            Grid.SetColumn(lineHost, 2);

            var plays = Txt((tr.PlaybackCount ?? 0) > 0 ? "▶ " + Ui.Count(tr.PlaybackCount ?? 0) : "", 11.5, "#777777");
            plays.VerticalAlignment = VerticalAlignment.Center; plays.Margin = new Thickness(10, 0, 6, 0);
            Grid.SetColumn(plays, 3);
            var like = IconBtn("\uEB51", "Like", () => ToggleLikeOf(tr), 28);
            Grid.SetColumn(like, 4);
            var dur = Txt(it.DurationText, 12, "#777777");
            dur.VerticalAlignment = VerticalAlignment.Center; dur.Margin = new Thickness(6, 0, 4, 0); dur.Width = 44; dur.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(dur, 5);

            void Sync()
            {
                bool l = likedIds.Contains(tr.Id);
                var lt = (TextBlock)like.Child; lt.Text = l ? "\uEB52" : "\uEB51"; lt.Foreground = Ui.B(l ? "#FF7A3D" : "#cccccc");
                bool now = current != null && current.Id == tr.Id;
                num.Foreground = Ui.B(now ? "#FF7A3D" : "#8a8a8a");
            }
            Sync();
            refreshers.Add(Sync);

            g.Children.Add(numHost); g.Children.Add(art); g.Children.Add(lineHost); g.Children.Add(plays); g.Children.Add(like); g.Children.Add(dur);
            row.Child = g;
            row.MouseEnter += (_, _) => { row.Background = Ui.B("#1AFFFFFF"); num.Visibility = Visibility.Collapsed; playOv.Visibility = Visibility.Visible; };
            row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; num.Visibility = Visibility.Visible; playOv.Visibility = Visibility.Collapsed; };
            row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; PlayFromList(all, it); } };
            row.MouseRightButtonUp += (_, e) => { if (ShowItemMenu(it, row, PlacementMode.MousePoint)) e.Handled = true; };
            return row;
        }

        void PlayPlaylistPage(PaneTab t, List<ItemVM> trackRows)
        {
            if (trackRows.Count == 0) return;
            if (current != null && player.HasTrack && trackRows.Any(r => r.Track!.Id == current.Id)) { TogglePlay(); return; }
            PlayFromList(trackRows, trackRows[0]);
        }

        void ShufflePlaylistPage(PaneTab t, List<ItemVM> trackRows)
        {
            if (trackRows.Count == 0) return;
            var mixed = trackRows.OrderBy(_ => rng.Next()).ToList();
            PlayFromList(mixed, mixed[0]);
        }

        /// <summary>Adds to the end of the queue without a toast per track.</summary>
        void QueueAddQuiet(ScTrack tr)
        {
            if (current == null || qi < 0 || qi >= queue.Count) { queue = new List<ScTrack> { tr }; qi = 0; _ = PlayCurrentAsync(); }
            else queue.Add(tr);
            RefreshQueueTab();
        }

        static List<string> PlaylistTags(ScPlaylist pl)
        {
            var list = new List<string>();
            void Add(string? s) { s = (s ?? "").Trim(); if (s.Length > 0 && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s); }
            Add(pl.Genre);
            // tag_list looks like:  mainstream "hip hop" phonk
            foreach (Match m in Regex.Matches(pl.TagList ?? "", "\"([^\"]+)\"|(\\S+)"))
                Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            return list.Take(8).ToList();
        }

        Border SquareBtn(string glyph, string tip, Action click)
        {
            var b = new Border
            {
                Width = 42, Height = 32, CornerRadius = new CornerRadius(8), Background = Ui.B("#FF1F1F1F"), Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(1), BorderBrush = Ui.B("#22FFFFFF"),
                Child = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 14, Foreground = Ui.B("#dddddd"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(b, "#FF333333");
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return b;
        }

        /// <summary>The banner takes its colour from the cover (softened towards grey, like soundcloud.com's header).</summary>
        async System.Threading.Tasks.Task TintBannerAsync(string artUrl, Border banner)
        {
            if (artUrl.Length == 0) return;
            try
            {
                var img = await ArtCache.GetAsync(ArtCache.Size(artUrl, "t200x200"), 64);
                if (img == null) return;
                var rtb = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                using (var dc = dv.RenderOpen()) dc.DrawImage(img, new Rect(0, 0, 1, 1));
                rtb.Render(dv);
                var px = new byte[4];
                rtb.CopyPixels(px, 4, 0);
                double a = px[3] == 0 ? 255 : px[3];
                double r = px[2] * 255 / a, gch = px[1] * 255 / a, b = px[0] * 255 / a;
                const double grey = 112, mix = 0.5;
                byte R(double v) => (byte)Math.Max(0, Math.Min(255, v * (1 - mix) + grey * mix));
                var c1 = Color.FromRgb(R(r), R(gch), R(b));
                var c2 = Color.FromRgb((byte)(c1.R * 0.52), (byte)(c1.G * 0.52), (byte)(c1.B * 0.52));
                banner.Background = new LinearGradientBrush(c1, c2, 20);
            }
            catch { }
        }
    }
}
