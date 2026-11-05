using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KaruCloud
{
    /// <summary>Feed page (post blocks with waveform) and Likes page (artwork grid / list + filter), styled like soundcloud.com.</summary>
    public sealed partial class MainWindow
    {
        // Things on the current card page that must repaint when like / repost / play state changes.
        readonly List<Action> pageRefreshers = new List<Action>();
        // Waveforms on the current page (feed), so playback progress shows on the one that is playing.
        readonly List<(ScTrack Tr, WaveformView Wave)> pageWaves = new List<(ScTrack, WaveformView)>();
        readonly SemaphoreSlim waveGate = new SemaphoreSlim(4);

        void RefreshPage()
        {
            if (pageRefreshers.Count == 0) return;
            foreach (var a in pageRefreshers.ToList()) { try { a(); } catch { } }
        }

        void UpdateFeedProgress(TimeSpan p, TimeSpan d)
        {
            if (pageWaves.Count == 0) return;
            foreach (var (tr, w) in pageWaves)
                w.SetProgress(current != null && current.Id == tr.Id && d.TotalSeconds > 0 ? p.TotalSeconds / d.TotalSeconds : 0);
        }

        void ResetFeedProgress()
        {
            foreach (var (_, w) in pageWaves) w.SetProgress(0);
        }

        async Task LoadWaveGatedAsync(WaveformView wave, ScTrack tr)
        {
            await waveGate.WaitAsync();
            try { await LoadWaveAsync(wave, tr); }
            finally { waveGate.Release(); }
        }

        /// <summary>Rebuild the card page in place (keeps the scroll position).</summary>
        void RebuildCardPage(PaneTab t)
        {
            if (!ReferenceEquals(cardTab, t)) return;
            double keep = cardScroll.VerticalOffset;
            cardScroll.Content = BuildCards(t);
            cardScroll.UpdateLayout();
            cardScroll.ScrollToVerticalOffset(keep);
        }

        // ---------- small shared pieces ----------

        /// <summary>Dark rectangular button with icon + text (like / repost / share ... in the feed).</summary>
        sealed class FeedBtn
        {
            public readonly Border Root;
            readonly TextBlock glyph = new TextBlock(), label = new TextBlock();

            public FeedBtn(string g, string text, string tip, Action click)
            {
                glyph.FontFamily = Ui.Icons; glyph.FontSize = 14; glyph.VerticalAlignment = VerticalAlignment.Center;
                label.FontSize = 13; label.FontWeight = FontWeights.SemiBold; label.VerticalAlignment = VerticalAlignment.Center;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(glyph); sp.Children.Add(label);
                Root = new Border
                {
                    Height = 34, MinWidth = 40, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0),
                    CornerRadius = new CornerRadius(4), Background = Ui.B("#2E2E2E"), Cursor = Cursors.Hand, ToolTip = tip, Child = sp,
                };
                Ui.Hover(Root, "#3C3C3C");
                Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
                Set(g, text, "#F2F2F2");
            }

            public void Set(string g, string text, string hex)
            {
                var fg = Ui.B(hex);
                glyph.Text = g; glyph.Foreground = fg;
                label.Text = text; label.Foreground = fg;
                label.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                label.Margin = new Thickness(text.Length == 0 ? 0 : 8, 0, 0, 0);
            }
        }

        Border BigPlay(double size, Action click)
        {
            var glyph = new TextBlock
            {
                Text = "\uE768", FontFamily = Ui.Icons, FontSize = size * 0.38, Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0),
            };
            var b = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Brushes.White, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top, Child = glyph, Tag = glyph, ToolTip = "Play",
            };
            Ui.Hover(b, "#FFDDDDDD");
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return b;
        }

        static void SetBigPlayGlyph(Border b, bool playing)
        {
            if (b.Tag is TextBlock g)
            {
                g.Text = playing ? "\uE769" : "\uE768";
                g.Margin = new Thickness(playing ? 0 : 2, 0, 0, 0);
            }
        }

        // ================= FEED =================
        UIElement BuildFeedPage(PaneTab t)
        {
            var page = new StackPanel { Margin = new Thickness(30, 18, 30, 28), HorizontalAlignment = HorizontalAlignment.Stretch };

            // heading + "Reposts" switch
            var top = new Grid { Margin = new Thickness(0, 4, 0, 26) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var head = new TextBlock
            {
                Text = "Hear the latest posts from the people you\u2019re following:", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            var sw = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            sw.Children.Add(new TextBlock { Text = "Reposts", Foreground = Ui.B("#9A9A9A"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) });
            var knob = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
            var track = new Border
            {
                Width = 44, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.B(t.ShowReposts ? "#FF5500" : "#555555"),
                Child = knob,
            };
            knob.HorizontalAlignment = t.ShowReposts ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            sw.Children.Add(track);
            sw.ToolTip = "Show or hide reposts";
            sw.MouseLeftButtonUp += (_, e) => { e.Handled = true; t.ShowReposts = !t.ShowReposts; RebuildCardPage(t); };
            Grid.SetColumn(sw, 1);
            top.Children.Add(head); top.Children.Add(sw);
            page.Children.Add(top);

            int shown = 0;
            foreach (var r in t.Rows)
            {
                if (r.Kind == "header" || r.Kind == "more") continue;
                string via = r.Kind == "track" ? (r.Track?.Via ?? "") : (r.Playlist?.Via ?? "");
                if (!t.ShowReposts && via.Length > 0) continue;
                FrameworkElement? post = r.Kind == "track" && r.Track != null ? BuildFeedTrack(t, r)
                    : r.Kind == "playlist" && r.Playlist != null ? BuildFeedPlaylist(r) : null;
                if (post == null) continue;
                page.Children.Add(post);
                shown++;
            }
            if (shown == 0)
                page.Children.Add(new TextBlock { Text = t.ShowReposts ? "Nothing new from the people you follow." : "No original posts here. Switch Reposts on to see everything.", Foreground = Ui.B("#808080"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0) });
            if (t.Rows.Any(r => r.Kind == "more")) page.Children.Add(BuildMoreButton(t));
            return page;
        }

        FrameworkElement PostHeader(ScUser? who, string verb, string noun, string iso)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var av = ArtBox(who?.AvatarUrl ?? "", 32, true, "t200x200", 64);
            av.Cursor = Cursors.Hand; av.Margin = new Thickness(0, 0, 10, 0);
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(who); };
            row.Children.Add(av);
            var name = Link(who?.Username ?? "", 13.5, "#F2F2F2", () => OpenArtist(who), true);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            string ago = Ui.Ago(iso);
            var what = Txt(" " + verb + " a " + noun + (ago.Length > 0 ? " " + ago : ""), 13.5, "#8A8A8A");
            what.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(what);
            return row;
        }

        /// <summary>One track as a block: cover, play button, waveform, actions. search = the look of soundcloud.com's search results (no "posted a track" line, labels on a dark strip).</summary>
        FrameworkElement BuildFeedTrack(PaneTab t, ItemVM it, bool search = false, bool history = false)
        {
            var tr = it.Track!;
            var who = tr.Poster ?? tr.User;
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, history ? 26 : (search ? 30 : 34)), Background = Brushes.Transparent };
            if (!search && !history) box.Children.Add(PostHeader(who, tr.Poster != null ? "reposted" : "posted", "track", tr.PostedAt));

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            double artSize = history ? 128 : (search ? 160 : 184);
            var art = ArtBox(it.ArtUrl, artSize, false, "t300x300", 300);
            art.CornerRadius = new CornerRadius(4); art.VerticalAlignment = VerticalAlignment.Top; art.Cursor = Cursors.Hand;
            art.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenTrack(tr); };
            art.Tag = (Action)(() => OpenTrack(tr));
            Grid.SetColumn(art, 0);

            var content = new Grid { Margin = new Thickness(search || history ? 14 : 18, 0, 0, 0) };
            if (!history) content.Height = artSize;
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = history ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (history) content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(content, 1);

            // top: play, artist + title, genre
            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var play = BigPlay(44, () => PlayDetailTrack(t, tr));
            var texts = new StackPanel { Margin = new Thickness(14, 2, 10, 0) };
            var artist = Link(tr.ArtistName, 13, "#9A9A9A", () => OpenArtist(tr.User));
            var title = Link(it.Title, 14.5, "#F2F2F2", () => OpenTrack(tr), true);
            title.Foreground = it.TitleBrush; title.Margin = new Thickness(0, 3, 0, 0);
            cardTitles.Add((it, title));
            if (search)
            {
                // artist and title sit on a dark strip, like soundcloud.com's search results
                artist.Foreground = Ui.B("#C8C8C8"); title.Margin = new Thickness(0);
                texts.Children.Add(new Border { Background = Ui.B("#FF111111"), Padding = new Thickness(6, 1, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, Child = artist });
                texts.Children.Add(new Border { Background = Ui.B("#FF111111"), Padding = new Thickness(6, 1, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0), Child = title });
            }
            else { texts.Children.Add(artist); texts.Children.Add(title); }
            Grid.SetColumn(texts, 1);
            topRow.Children.Add(play); topRow.Children.Add(texts);
            string age = (search || history) ? Ui.Ago(tr.CreatedAt) : "";
            TextBlock? ageEl = null;
            Border? pillEl = null;
            if (age.Length > 0)
            {
                ageEl = Txt(age, 12, "#DDDDDD");
                ageEl.VerticalAlignment = VerticalAlignment.Top; ageEl.Margin = new Thickness(0, 3, 0, 0);
            }
            if (!string.IsNullOrWhiteSpace(tr.Genre) && !search)
            {
                pillEl = new Border
                {
                    CornerRadius = new CornerRadius(12), Background = Ui.B("#33FFFFFF"), Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "# " + tr.Genre!.Trim(), Foreground = Ui.B("#E0E0E0"), FontSize = 12, FontWeight = FontWeights.SemiBold },
                };
            }
            if (history)
            {
                // listening history: "2 months ago" with the genre pill underneath, both on the right
                var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                if (ageEl != null) { ageEl.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(ageEl); }
                if (pillEl != null) { pillEl.HorizontalAlignment = HorizontalAlignment.Right; pillEl.Margin = new Thickness(0, ageEl != null ? 8 : 0, 0, 0); right.Children.Add(pillEl); }
                Grid.SetColumn(right, 2);
                topRow.Children.Add(right);
            }
            else
            {
                if (ageEl != null) { Grid.SetColumn(ageEl, 2); topRow.Children.Add(ageEl); }
                if (pillEl != null) { Grid.SetColumn(pillEl, 2); topRow.Children.Add(pillEl); }
            }
            Grid.SetRow(topRow, 0);

            // waveform with the duration in its corner
            var waveBox = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
            var wave = new WaveformView { Height = 64 };
            wave.Seeked += frac => SeekDetail(t, tr, frac);
            var dur = new Border
            {
                Background = Ui.B("#CC111111"), Padding = new Thickness(3, 0, 3, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 14), IsHitTestVisible = false,
                Child = new TextBlock { Text = it.DurationText, Foreground = Ui.B("#DDDDDD"), FontSize = 10.5 },
            };
            waveBox.Children.Add(wave); waveBox.Children.Add(dur);
            Grid.SetRow(waveBox, 1);
            pageWaves.Add((tr, wave));
            _ = LoadWaveGatedAsync(wave, tr);

            // actions + counters
            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            var like = new FeedBtn("\uEB51", "", "Like", () => ToggleLikeOf(tr));
            var repost = new FeedBtn("\uE8EE", "", "Repost", () => ToggleRepostOf(tr));
            var share = new FeedBtn("\uE72D", "", "Open on SoundCloud", () => OpenUrl(tr.PermalinkUrl));
            var copy = new FeedBtn("\uE8C8", "", "Copy link", () => { CopyLink(tr.PermalinkUrl); ShowToast("Link copied", true); });
            var addpl = new FeedBtn("\uE710", "", "Add to playlist", () => { });
            addpl.Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowAddToPlaylist(tr, addpl.Root); };
            var more = new FeedBtn("\uE712", "", "More", () => { });
            more.Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowQueueRowMenu(tr, more.Root); };
            btns.Children.Add(like.Root); btns.Children.Add(repost.Root); btns.Children.Add(share.Root); btns.Children.Add(copy.Root); btns.Children.Add(addpl.Root); btns.Children.Add(more.Root);

            var stats = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var plays = Txt("", 12.5, "#8A8A8A");
            var comments = Txt("", 12.5, "#8A8A8A");
            plays.Margin = new Thickness(0, 0, 14, 0);
            stats.Children.Add(plays); stats.Children.Add(comments);
            Grid.SetColumn(stats, 1);
            actions.Children.Add(btns); actions.Children.Add(stats);
            Grid.SetRow(actions, history ? 3 : 2);

            void Sync()
            {
                bool l = likedIds.Contains(tr.Id), r = repostedIds.Contains(tr.Id);
                long lc = Math.Max(0, (tr.LikesCount ?? 0) + Delta(likeDelta, tr.Id));
                long rc = Math.Max(0, (tr.RepostsCount ?? 0) + Delta(repostDelta, tr.Id));
                like.Set(l ? "\uEB52" : "\uEB51", Ui.Count(lc), l ? "#FF7A3D" : "#F2F2F2");
                repost.Set("\uE8EE", Ui.Count(rc), r ? "#FF7A3D" : "#F2F2F2");
                plays.Text = (tr.PlaybackCount ?? 0) > 0 ? "\u25B6 " + Ui.Count(tr.PlaybackCount ?? 0) : "";
                comments.Text = (tr.CommentCount ?? 0) > 0 ? "\uE8BD " + Ui.Count(tr.CommentCount ?? 0) : "";
                SetBigPlayGlyph(play, player.IsPlaying && current != null && current.Id == tr.Id);
            }
            comments.FontFamily = new FontFamily("Segoe UI, Segoe MDL2 Assets");
            Sync();
            pageRefreshers.Add(Sync);

            content.Children.Add(topRow); content.Children.Add(waveBox); content.Children.Add(actions);
            if (history)
            {
                var cm = BuildHistoryCommentBox(tr);
                Grid.SetRow(cm, 2);
                content.Children.Add(cm);
            }
            body.Children.Add(art); body.Children.Add(content);
            box.Children.Add(body);
            box.MouseRightButtonUp += (_, e) => { if (ShowItemMenu(it, box, PlacementMode.MousePoint)) e.Handled = true; };

            if (current != null && current.Id == tr.Id && player.HasTrack && player.Duration.TotalSeconds > 0)
                wave.SetProgress(player.Position.TotalSeconds / player.Duration.TotalSeconds);
            return box;
        }

        FrameworkElement BuildFeedPlaylist(ItemVM it, bool search = false)
        {
            var p = it.Playlist!;
            var who = p.Poster ?? p.User;
            string kind = p.IsSystem ? "mix" : (string.Equals(p.SetType, "album", StringComparison.OrdinalIgnoreCase) ? "album" : "playlist");
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, search ? 30 : 34), Background = Brushes.Transparent };
            if (!search) box.Children.Add(PostHeader(who, p.Poster != null ? "reposted" : "posted", kind, p.PostedAt));

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            double artSize = search ? 160 : 150;
            var art = ArtBox(it.ArtUrl, artSize, false, "t300x300", 300);
            art.CornerRadius = new CornerRadius(4); art.VerticalAlignment = VerticalAlignment.Top; art.Cursor = Cursors.Hand;
            art.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenCard(it); };

            var content = new StackPanel { Margin = new Thickness(search ? 14 : 18, 0, 0, 0) };

            // top: play, owner + title, kind / track count
            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var play = BigPlay(40, () => PlayItem(it));
            var texts = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Link(p.User?.Username ?? "", 13, "#9A9A9A", () => OpenArtist(p.User)));
            var title = Link(it.Title, 15, "#F2F2F2", () => OpenCard(it), true);
            title.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(title);
            texts.Children.Add(Txt(char.ToUpper(kind[0]) + kind.Substring(1) + (p.TrackCount > 0 ? "  \u00B7  " + p.TrackCount + " tracks" : ""), 12, "#8A8A8A"));
            ((TextBlock)texts.Children[2]).Margin = new Thickness(0, 5, 0, 0);
            Grid.SetColumn(texts, 1);
            topRow.Children.Add(play); topRow.Children.Add(texts);
            content.Children.Add(topRow);

            // a handful of tracks, numbered like soundcloud.com's playlist cards
            var preview = (p.Tracks ?? new List<ScTrack>()).Where(x => x != null && x.Id != 0).Take(5).ToList();
            if (preview.Count > 0)
            {
                var listHost = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
                for (int i = 0; i < preview.Count; i++) listHost.Children.Add(PlaylistPreviewRow(i + 1, preview[i]));
                content.Children.Add(listHost);
                if (p.TrackCount > preview.Count)
                {
                    var viewAll = Link("View " + p.TrackCount + " tracks", 12.5, "#FF7A3D", () => OpenCard(it), true);
                    viewAll.Margin = new Thickness(2, 8, 0, 0);
                    content.Children.Add(viewAll);
                }
            }

            // actions + counters
            var actions = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            bool liked = false;
            var like = new FeedBtn("\uEB51", Ui.Count(p.LikesCount), "Like", () => { });
            like.Root.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                bool want = !liked;
                if (!await LikePlaylistAsync(p, want)) return; // the heart changes only after SoundCloud confirmed
                liked = want;
                like.Set(liked ? "\uEB52" : "\uEB51", Ui.Count(p.LikesCount + (liked ? 1 : 0)), liked ? "#FF7A3D" : "#F2F2F2");
            };
            var share = new FeedBtn("\uE72D", "", "Open on SoundCloud", () => OpenUrl(p.PermalinkUrl));
            var copy = new FeedBtn("\uE8C8", "", "Copy link", () => { if (!string.IsNullOrEmpty(p.PermalinkUrl)) { CopyLink(p.PermalinkUrl); ShowToast("Link copied", true); } });
            var more = new FeedBtn("\uE712", "", "More", () => { });
            more.Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowItemMenu(it, more.Root, PlacementMode.Bottom); };
            btns.Children.Add(like.Root); btns.Children.Add(share.Root); btns.Children.Add(copy.Root); btns.Children.Add(more.Root);
            actions.Children.Add(btns);
            content.Children.Add(actions);

            Grid.SetColumn(art, 0); Grid.SetColumn(content, 1);
            body.Children.Add(art); body.Children.Add(content);
            box.Children.Add(body);
            box.MouseRightButtonUp += (_, e) => { if (ShowItemMenu(it, box, PlacementMode.MousePoint)) e.Handled = true; };
            return box;
        }

        /// <summary>One numbered track inside a playlist card's preview: "1 \u00B7 uploader - title", play count on the right.</summary>
        FrameworkElement PlaylistPreviewRow(int index, ScTrack tr)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var num = Txt(index.ToString(), 12, "#8A8A8A");
            num.Width = 16; num.VerticalAlignment = VerticalAlignment.Center;

            var line = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            line.Inlines.Add(new System.Windows.Documents.Run((tr.ArtistName.Length > 0 ? tr.ArtistName : "unknown") + "  \u00B7  ") { Foreground = Ui.B("#9A9A9A") });
            line.Inlines.Add(new System.Windows.Documents.Run(tr.Title) { Foreground = Ui.B("#E6E6E6") });

            var plays = Txt((tr.PlaybackCount ?? 0) > 0 ? "\u25B6 " + Ui.Count(tr.PlaybackCount ?? 0) : "", 11, "#808080");
            plays.VerticalAlignment = VerticalAlignment.Center; plays.Margin = new Thickness(10, 0, 0, 0);

            Grid.SetColumn(num, 0); Grid.SetColumn(line, 1); Grid.SetColumn(plays, 2);
            g.Children.Add(num); g.Children.Add(line); g.Children.Add(plays);

            var row = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(-4, 0, -4, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g };
            Ui.Hover(row, "#12FFFFFF");
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenTrack(tr); };
            row.Tag = (Action)(() => OpenTrack(tr));
            return row;
        }

        // ================= LIKES =================
        UIElement BuildLikesPage(PaneTab t)
        {
            var page = new StackPanel { Margin = new Thickness(30, 18, 26, 28) };

            // toolbar: heading, View (grid / list), Filter
            var bar = new Grid { Margin = new Thickness(0, 4, 0, 24) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new TextBlock { Text = "Hear the tracks you\u2019ve liked:", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };

            var viewBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
            viewBox.Children.Add(new TextBlock { Text = "View", Foreground = Ui.B("#9A9A9A"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            Border ViewBtn(string glyph, bool grid, string tip)
            {
                var g = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var b = new Border { Width = 40, Height = 32, CornerRadius = new CornerRadius(4), Background = Ui.B("#2E2E2E"), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), Child = g, ToolTip = tip, Tag = g };
                b.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (t.GridView == grid) return; t.GridView = grid; RebuildCardPage(t); };
                g.Foreground = Ui.B(t.GridView == grid ? "#FF5500" : "#F2F2F2");
                return b;
            }
            viewBox.Children.Add(ViewBtn("\uE80A", true, "Grid"));
            viewBox.Children.Add(ViewBtn("\uE8FD", false, "List"));
            Grid.SetColumn(viewBox, 1);

            var filterHost = new Grid { Width = 300, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            var filterBg = new Border { CornerRadius = new CornerRadius(4), Background = Ui.B("#2E2E2E") };
            var filter = new TextBox
            {
                Text = t.Filter, Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13,
            };
            var hint = new TextBlock { Text = "Filter", Foreground = Ui.B("#8A8A8A"), FontSize = 13, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            hint.Visibility = t.Filter.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            filterHost.Children.Add(filterBg); filterHost.Children.Add(filter); filterHost.Children.Add(hint);
            Grid.SetColumn(filterHost, 2);

            bar.Children.Add(heading); bar.Children.Add(viewBox); bar.Children.Add(filterHost);
            page.Children.Add(bar);

            var host = new Border();
            page.Children.Add(host);

            int lastCols = -1;
            int ColsFor(double w) => Math.Max(2, (int)(w / 196));
            double AvailWidth() => page.ActualWidth > 100 ? page.ActualWidth : Math.Max(500, cardScroll.ActualWidth - 56);

            void Fill()
            {
                cardTitles.Clear(); pageRefreshers.Clear();
                string q = t.Filter.Trim();
                var rows = t.Rows.Where(r => r.Kind == "track" && r.Track != null
                    && (q.Length == 0 || r.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || r.Track.ArtistName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                if (rows.Count == 0)
                {
                    host.Child = new TextBlock { Text = q.Length > 0 ? "No liked tracks match \u201C" + q + "\u201D." : "Nothing here yet.", Foreground = Ui.B("#808080"), FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };
                    return;
                }
                if (t.GridView)
                {
                    int cols = ColsFor(AvailWidth());
                    lastCols = cols;
                    double tile = Math.Floor(AvailWidth() / cols) - 20;
                    var wrap = new WrapPanel();
                    foreach (var r in rows) wrap.Children.Add(BuildLikeTile(r, rows, tile));
                    host.Child = wrap;
                }
                else
                {
                    host.Child = BuildItems(rows, it => PlayFromList(rows, it), pageRefreshers);
                }
            }

            filter.TextChanged += (_, _) =>
            {
                t.Filter = filter.Text;
                hint.Visibility = filter.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                Fill();
            };
            page.SizeChanged += (_, e) =>
            {
                if (t.GridView && e.WidthChanged && ColsFor(AvailWidth()) != lastCols) Fill();
            };
            Fill();

            if (t.Rows.Any(r => r.Kind == "more")) page.Children.Add(BuildMoreButton(t));
            return page;
        }

        FrameworkElement BuildLikeTile(ItemVM it, List<ItemVM> rows, double size)
        {
            var tr = it.Track!;
            var radius = new CornerRadius(4);
            var art = new Border { CornerRadius = radius, Background = Ui.B("#262626") };
            var dim = new Border { CornerRadius = radius, Background = Ui.B("#66000000"), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            var artGrid = new Grid { Width = size, Height = size };
            artGrid.Children.Add(art); artGrid.Children.Add(dim);

            var playBtn = new Border
            {
                Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = Ui.B("#FFFF5500"), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Play",
                Child = new TextBlock { Text = "\uE768", FontFamily = Ui.Icons, FontSize = 17, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) },
            };
            Ui.Hover(playBtn, "#FFFF7A3D");
            playBtn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (current != null && current.Id == tr.Id && (player.HasTrack || player.IsLoading)) TogglePlay();
                else PlayFromList(rows, it);
            };
            artGrid.Children.Add(playBtn);
            if (it.ArtUrl.Length > 0) _ = LoadCardArtAsync(it.ArtUrl, art);
            bool hot = false;
            // the playing tile keeps its (pause) button and dim layer on, so you can see what is playing without hovering
            void PaintPlay()
            {
                bool on = current != null && current.Id == tr.Id && player.IsPlaying;
                var g = (TextBlock)playBtn.Child;
                g.Text = on ? "\uE769" : "\uE768";
                g.Margin = new Thickness(on ? 0 : 2, 0, 0, 0);
                playBtn.ToolTip = on ? "Pause" : "Play";
                var v = (hot || on) ? Visibility.Visible : Visibility.Collapsed;
                dim.Visibility = v; playBtn.Visibility = v;
            }

            var heart = new TextBlock
            {
                FontFamily = Ui.Icons, FontSize = 12, Foreground = Ui.B("#8A8A8A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0),
                Cursor = Cursors.Hand, ToolTip = "Like / unlike",
            };
            heart.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleLikeOf(tr); };
            var title = Link(it.Title, 13, "#F2F2F2", () => OpenTrack(tr), true);
            title.Foreground = it.TitleBrush; title.ToolTip = it.Title;
            cardTitles.Add((it, title));
            var titleRow = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(title, 1);
            titleRow.Children.Add(heart); titleRow.Children.Add(title);
            var artist = Link(tr.ArtistName, 12.5, "#8A8A8A", () => OpenArtist(tr.User));
            artist.Margin = new Thickness(0, 3, 0, 0);

            var tile = new StackPanel { Width = size, Margin = new Thickness(0, 0, 20, 26), Background = Brushes.Transparent };
            tile.Children.Add(artGrid); tile.Children.Add(titleRow); tile.Children.Add(artist);

            void Sync() { heart.Text = likedIds.Contains(tr.Id) ? "\uEB52" : "\uEB51"; PaintPlay(); }
            Sync();
            pageRefreshers.Add(Sync);

            artGrid.Cursor = Cursors.Hand;
            artGrid.MouseLeftButtonUp += (_, _) => OpenTrack(tr);
            artGrid.Tag = (Action)(() => OpenTrack(tr));
            tile.MouseEnter += (_, _) => { hot = true; PaintPlay(); };
            tile.MouseLeave += (_, _) => { hot = false; PaintPlay(); };
            tile.MouseRightButtonUp += (_, e) => { if (ShowItemMenu(it, tile, PlacementMode.MousePoint)) e.Handled = true; };
            return tile;
        }
    }
}
