using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KaruCloud
{
    /// <summary>
    /// Listening history (soundcloud.com style: "Recently played" cards, then every track as a block with waveform and a comment box)
    /// and the "Comments by ..." page that the profile's "Latest comments - View all" opens.
    /// </summary>
    public sealed partial class MainWindow
    {
        // ================= HISTORY =================
        UIElement BuildHistoryPage(PaneTab t)
        {
            var page = new StackPanel { Margin = new Thickness(30, 18, 30, 28), HorizontalAlignment = HorizontalAlignment.Stretch };
            var trackRows = t.Rows.Where(r => r.Kind == "track" && r.Track != null).ToList();

            // ---- "Recently played:"  [Clear all history] [Filter] ----
            var bar = new Grid { Margin = new Thickness(0, 4, 0, 18) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new TextBlock { Text = "Recently played:", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };

            var clear = new TextBlock
            {
                Text = "Clear all history", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0),
            };
            clear.MouseEnter += (_, _) => clear.TextDecorations = TextDecorations.Underline;
            clear.MouseLeave += (_, _) => clear.TextDecorations = null;
            clear.MouseLeftButtonUp += (_, e) => { e.Handled = true; ClearHistory(t); };
            Grid.SetColumn(clear, 1);

            var filterHost = new Grid { Width = 245, Height = 32, VerticalAlignment = VerticalAlignment.Center };
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
            bar.Children.Add(heading); bar.Children.Add(clear); bar.Children.Add(filterHost);
            page.Children.Add(bar);

            // ---- round artist cards (the people behind your latest plays) ----
            var people = new List<ScUser>();
            foreach (var r in trackRows)
            {
                var u = r.Track!.User;
                if (u == null || u.Id == 0 || people.Any(p => p.Id == u.Id)) continue;
                people.Add(u);
                if (people.Count >= 6) break;
            }
            if (people.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 30) };
                foreach (var u in people) wrap.Children.Add(BuildRecentPerson(u));
                page.Children.Add(wrap);
            }

            page.Children.Add(new TextBlock
            {
                Text = "Hear the tracks you\u2019ve played:", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 22),
            });

            // ---- the tracks ----
            var host = new StackPanel();
            page.Children.Add(host);

            void Fill()
            {
                cardTitles.Clear(); pageRefreshers.Clear(); pageWaves.Clear();
                host.Children.Clear();
                string q = t.Filter.Trim();
                var rows = trackRows.Where(r => q.Length == 0
                    || r.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || r.Track!.ArtistName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (rows.Count == 0)
                {
                    host.Children.Add(new TextBlock { Text = q.Length > 0 ? "No played tracks match \u201C" + q + "\u201D." : "Nothing here yet.", Foreground = Ui.B("#808080"), FontSize = 13 });
                    return;
                }
                foreach (var r in rows) host.Children.Add(BuildFeedTrack(t, r, false, true));
            }

            filter.TextChanged += (_, _) =>
            {
                t.Filter = filter.Text;
                hint.Visibility = filter.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                Fill();
            };
            Fill();

            if (t.Rows.Any(r => r.Kind == "more")) page.Children.Add(BuildMoreButton(t));
            return page;
        }

        // ================= REPOSTS =================
        /// <summary>Your reposts as blocks (cover, play button, waveform, comment box, actions), the same look as the listening history.</summary>
        UIElement BuildRepostsPage(PaneTab t)
        {
            var page = new StackPanel { Margin = new Thickness(30, 18, 30, 28), HorizontalAlignment = HorizontalAlignment.Stretch };

            var bar = new Grid { Margin = new Thickness(0, 4, 0, 22) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new TextBlock { Text = "Hear the tracks you\u2019ve reposted:", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };

            var filterHost = new Grid { Width = 245, Height = 32, VerticalAlignment = VerticalAlignment.Center };
            var filterBg = new Border { CornerRadius = new CornerRadius(4), Background = Ui.B("#2E2E2E") };
            var filter = new TextBox
            {
                Text = t.Filter, Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13,
            };
            var hint = new TextBlock { Text = "Filter", Foreground = Ui.B("#8A8A8A"), FontSize = 13, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            hint.Visibility = t.Filter.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            filterHost.Children.Add(filterBg); filterHost.Children.Add(filter); filterHost.Children.Add(hint);
            Grid.SetColumn(filterHost, 1);
            bar.Children.Add(heading); bar.Children.Add(filterHost);
            page.Children.Add(bar);

            var host = new StackPanel();
            page.Children.Add(host);

            void Fill()
            {
                cardTitles.Clear(); pageRefreshers.Clear(); pageWaves.Clear();
                host.Children.Clear();
                string q = t.Filter.Trim();
                var rows = t.Rows.Where(r => (r.Kind == "track" && r.Track != null) || (r.Kind == "playlist" && r.Playlist != null))
                    .Where(r => q.Length == 0
                        || r.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || (r.Track != null && r.Track.ArtistName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (r.Playlist?.User?.Username ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (rows.Count == 0)
                {
                    host.Children.Add(new TextBlock { Text = q.Length > 0 ? "No reposts match \u201C" + q + "\u201D." : "Nothing here yet.", Foreground = Ui.B("#808080"), FontSize = 13 });
                    return;
                }
                foreach (var r in rows)
                    host.Children.Add(r.Kind == "track" ? BuildFeedTrack(t, r, false, true) : BuildFeedPlaylist(r, true));
            }

            filter.TextChanged += (_, _) =>
            {
                t.Filter = filter.Text;
                hint.Visibility = filter.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                Fill();
            };
            Fill();

            if (t.Rows.Any(r => r.Kind == "more")) page.Children.Add(BuildMoreButton(t));
            return page;
        }

        FrameworkElement BuildRecentPerson(ScUser u)
        {
            var card = new StackPanel { Width = 144, Margin = new Thickness(0, 0, 18, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var av = ArtBox(u.AvatarUrl ?? "", 144, true, "t300x300", 300);
            card.Children.Add(av);
            var name = new TextBlock
            {
                Text = u.Username ?? "", Foreground = Ui.B("#F2F2F2"), FontSize = 12.5, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 144,
            };
            card.Children.Add(name);
            if (u.Followers > 0)
            {
                var f = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) };
                f.Children.Add(new TextBlock { Text = "\uE77B", FontFamily = Ui.Icons, FontSize = 10, Foreground = Ui.B("#8A8A8A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
                f.Children.Add(new TextBlock { Text = u.Followers.ToString("N0") + (u.Followers == 1 ? " follower" : " followers"), Foreground = Ui.B("#8A8A8A"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
                card.Children.Add(f);
            }
            card.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(u); };
            card.Tag = (Action)(() => OpenArtist(u));
            return card;
        }

        async void ClearHistory(PaneTab t)
        {
            var ask = MessageBox.Show(this, "Clear your whole listening history on SoundCloud?\nThis can't be undone.", "KaruCloud", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.OK) return;
            try
            {
                await api.ClearHistoryAsync();
                t.Rows.Clear(); t.Next = null; t.Loaded = true; t.Message = EmptyText(t);
                if (t == active) ShowMessage(t);
                ShowToast("Listening history cleared", true);
            }
            catch (Exception ex)
            {
                Store.Log("clear history", ex);
                ShowToast("Couldn't clear the history: " + ex.Message);
            }
        }

        /// <summary>The "Write a comment" row under a track's waveform.</summary>
        FrameworkElement BuildHistoryCommentBox(ScTrack tr)
        {
            var me = api.Active;
            var g = new Grid { Margin = new Thickness(0, 4, 0, 14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var av = ArtBox(me?.AvatarUrl ?? "", 30, true, "t200x200", 60);
            av.Margin = new Thickness(0, 0, 10, 0); av.VerticalAlignment = VerticalAlignment.Center;

            var host = new Grid { Height = 30 };
            var bg = new Border { CornerRadius = new CornerRadius(4), Background = Ui.B("#2E2E2E") };
            var box = new TextBox
            {
                Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13, MaxLength = 300,
            };
            var hint = new TextBlock { Text = "Write a comment", Foreground = Ui.B("#8A8A8A"), FontSize = 13, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            host.Children.Add(bg); host.Children.Add(box); host.Children.Add(hint);
            Grid.SetColumn(host, 1);

            var send = new Border
            {
                Width = 44, Height = 30, CornerRadius = new CornerRadius(4), Background = Ui.B("#2E2E2E"), Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Post comment",
                Child = new TextBlock { Text = "\uE724", FontFamily = Ui.Icons, FontSize = 13, Foreground = Ui.B("#BBBBBB"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(send, "#3C3C3C");
            Grid.SetColumn(send, 2);

            async void Post()
            {
                string text = box.Text.Trim();
                if (text.Length == 0) return;
                long ts = -1;
                if (current != null && current.Id == tr.Id && player.HasTrack) ts = (long)player.Position.TotalMilliseconds;
                try
                {
                    box.IsEnabled = false;
                    await api.PostCommentAsync(tr.Id, text, ts, tr.PermalinkUrl);
                    commentCache.Remove(tr.Id); // the song's page reloads its comments next time
                    box.Text = "";
                    ShowToast("Comment posted", true);
                }
                catch (Exception ex)
                {
                    Store.Log("history comment", ex);
                    ShowToast(ex.Message);
                }
                finally { box.IsEnabled = true; }
            }
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Post(); } };
            send.MouseLeftButtonUp += (_, e) => { e.Handled = true; Post(); };

            g.Children.Add(av); g.Children.Add(host); g.Children.Add(send);
            return g;
        }

        // ================= COMMENTS BY <person> =================
        void OpenComments(ScUser? u)
        {
            if (u == null || u.Id == 0) return;
            string id = u.Id.ToString();
            NavigateHere("usercomments", Cut("Comments \u00B7 " + (u.Username ?? ""), 24), id);
            var t = active;
            if (t != null && t.Kind == "usercomments" && t.Param == id && t.DUser == null) t.DUser = u; // header without another request
        }

        /// <summary>Open a profile straight on one of its tabs (Likes / Following / Followers).</summary>
        void OpenProfileTab(ScUser? u, string sub)
        {
            if (u == null || u.Id == 0) return;
            OpenArtist(u);
            var t = active;
            if (t != null && t.Kind == "user" && t.Param == u.Id.ToString() && !t.Loaded) t.PendingSub = sub;
        }

        async Task LoadUserCommentsAsync(PaneTab t, int g, bool append, string url)
        {
            var userTask = (append || t.DUser != null) ? null : api.GetUserAsync(t.Param);
            var (items, next) = await api.GetUserCommentsPageAsync(url);
            if (g != t.Gen) return;
            t.Comments.AddRange(items);
            t.Next = next;
            if (userTask != null)
            {
                try
                {
                    var u = await userTask;
                    if (g != t.Gen) return;
                    t.DUser = u;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }

        UIElement BuildCommentsPage(PaneTab t)
        {
            var u = t.DUser;
            var page = new StackPanel { Margin = new Thickness(30, 22, 30, 28) };

            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var av = ArtBox(u?.AvatarUrl ?? "", 76, true, "t200x200", 152);
            av.Margin = new Thickness(0, 0, 22, 0); av.VerticalAlignment = VerticalAlignment.Center;
            if (u != null)
            {
                av.Cursor = Cursors.Hand;
                av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(u); };
                av.Tag = (Action)(() => OpenArtist(u));
            }
            head.Children.Add(av);
            head.Children.Add(new TextBlock
            {
                Text = "Comments by " + (u?.Username ?? "this person"), Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            page.Children.Add(head);

            // Likes / Following / Followers jump back to the profile on that tab
            var subTabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 26) };
            foreach (var (key, label) in new[] { ("likes", "Likes"), ("following", "Following"), ("followers", "Followers") })
            {
                string k = key;
                var tb = Txt(label, 12.5, "#9a9a9a", true);
                tb.Cursor = Cursors.Hand; tb.Margin = new Thickness(0, 0, 24, 0);
                tb.MouseEnter += (_, _) => tb.Foreground = Brushes.White;
                tb.MouseLeave += (_, _) => tb.Foreground = Ui.B("#9a9a9a");
                tb.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenProfileTab(u, k); };
                subTabs.Children.Add(tb);
            }
            page.Children.Add(subTabs);

            if (t.Comments.Count == 0)
                page.Children.Add(Txt("No comments yet.", 13, "#808080"));

            foreach (var c in t.Comments)
            {
                var c0 = c;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 24) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel();
                if (c.Track != null && c.Track.Title.Length > 0)
                {
                    var line = new WrapPanel();
                    line.Children.Add(Txt("on ", 12.5, "#8a8a8a"));
                    line.Children.Add(Link(c.Track.Title, 12.5, "#ffffff", () => OpenTrack(c0.Track), true));
                    left.Children.Add(line);
                }
                var body = SelText("\u201C" + (c.Body ?? "") + "\u201D", 12.5, "#e6e6e6");
                body.Margin = new Thickness(0, 5, 0, 0);
                left.Children.Add(body);
                row.Children.Add(left);

                string ago = Ui.Ago(c.CreatedAt);
                if (ago.Length > 0)
                {
                    var when = Txt(ago, 12, "#8a8a8a");
                    when.VerticalAlignment = VerticalAlignment.Top; when.Margin = new Thickness(18, 0, 0, 0);
                    Grid.SetColumn(when, 1);
                    row.Children.Add(when);
                }
                page.Children.Add(row);
            }

            if (t.Next != null)
                page.Children.Add(new FeedBtn("\uE72C", "Load more", "Load more comments", () => _ = LoadAsync(t, true)).Root);
            return page;
        }
    }
}
