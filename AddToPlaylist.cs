using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KaruCloud
{
    /// <summary>The "Add to playlist" popup (soundcloud.com's own): your playlists with a filter box, or a "Create a playlist" tab.</summary>
    public sealed partial class MainWindow
    {
        void ShowAddToPlaylist(ScTrack tr, FrameworkElement anchor)
        {
            if (api.Active == null) { ShowToast("Sign in to SoundCloud first."); return; }

            var root = new StackPanel { Margin = new Thickness(4), Width = 300 };

            // ---- tabs ----
            var addTabText = new TextBlock { Text = "Add to playlist", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 18, 8) };
            var createTabText = new TextBlock { Text = "Create a playlist", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 8) };
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            tabs.Children.Add(addTabText); tabs.Children.Add(createTabText);
            root.Children.Add(tabs);
            root.Children.Add(new Border { Height = 1, Background = Ui.B("#2a2a2a"), Margin = new Thickness(0, 0, 0, 10) });

            var body = new StackPanel();
            root.Children.Add(body);

            List<ScPlaylist>? mine = null;
            bool loading = false, loadFailed = false;

            void SetTab(bool add)
            {
                addTabText.Foreground = Ui.B(add ? "#FFFFFF" : "#8a8a8a");
                createTabText.Foreground = Ui.B(add ? "#8a8a8a" : "#FFFFFF");
                body.Children.Clear();
                if (add) BuildAddView(); else BuildCreateView();
            }
            addTabText.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetTab(true); };
            createTabText.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetTab(false); };

            void BuildAddView()
            {
                var listHost = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                var filterBox = new TextBox
                {
                    Background = Ui.B("#1AFFFFFF"), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 7, 10, 7), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 10),
                };
                var filterGrid = new Grid();
                var filterHint = new TextBlock { Text = "Filter playlists", Foreground = Ui.B("#707070"), FontSize = 12.5, Margin = new Thickness(10, 7, 10, 7), IsHitTestVisible = false };
                filterBox.TextChanged += (_, _) => { filterHint.Visibility = filterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RenderList(); };
                filterGrid.Children.Add(new Border { Background = Ui.B("#1AFFFFFF"), CornerRadius = new CornerRadius(6), Child = filterBox });
                filterGrid.Children.Add(filterHint);
                body.Children.Add(filterGrid);

                body.Children.Add(listHost);

                void RenderList()
                {
                    listHost.Children.Clear();
                    if (loading) { listHost.Children.Add(new TextBlock { Text = "Loading your playlists…", Foreground = Ui.B("#8a8a8a"), FontSize = 12.5, Margin = new Thickness(2, 6, 2, 6) }); return; }
                    if (loadFailed) { listHost.Children.Add(new TextBlock { Text = "Couldn't load your playlists.", Foreground = Ui.B("#ff8a80"), FontSize = 12.5, Margin = new Thickness(2, 6, 2, 6) }); return; }
                    var all = mine ?? new List<ScPlaylist>();
                    string q = filterBox.Text.Trim();
                    var shown = (q.Length == 0 ? all : all.Where(p => p.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                    if (shown.Count == 0) { listHost.Children.Add(new TextBlock { Text = all.Count == 0 ? "You don't have any playlists yet." : "No playlists match.", Foreground = Ui.B("#8a8a8a"), FontSize = 12.5, Margin = new Thickness(2, 6, 2, 6) }); return; }
                    foreach (var pl in shown) listHost.Children.Add(PlaylistPickRow(pl, tr));
                }
                RenderList();

                if (mine == null && !loading)
                {
                    loading = true;
                    _ = LoadMineAsync();
                }

                async System.Threading.Tasks.Task LoadMineAsync()
                {
                    try { mine = await api.GetMyPlaylistsAsync(); }
                    catch (Exception ex) { loadFailed = true; Store.Log("add to playlist", ex); }
                    finally { loading = false; if (popup != null && popup.IsOpen) RenderList(); }
                }
            }

            void BuildCreateView()
            {
                var titleBox = new TextBox
                {
                    Background = Ui.B("#1AFFFFFF"), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 7, 10, 7), FontSize = 12.5,
                };
                var titleGrid = new Grid();
                var titleHint = new TextBlock { Text = "Playlist title", Foreground = Ui.B("#707070"), FontSize = 12.5, Margin = new Thickness(10, 7, 10, 7), IsHitTestVisible = false };
                titleBox.TextChanged += (_, _) => titleHint.Visibility = titleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                titleGrid.Children.Add(new Border { Background = Ui.B("#1AFFFFFF"), CornerRadius = new CornerRadius(6), Child = titleBox });
                titleGrid.Children.Add(titleHint);
                body.Children.Add(titleGrid);

                bool isPrivate = false;
                var privRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 12, 0, 16), Cursor = Cursors.Hand };
                var toggle = MiniToggle(false, v => isPrivate = v);
                var privLabel = new TextBlock { Text = "Private", Foreground = Ui.B("#cccccc"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                privRow.Children.Add(toggle); privRow.Children.Add(privLabel);
                body.Children.Add(privRow);

                var createBtn = new Border
                {
                    Height = 34, CornerRadius = new CornerRadius(17), Background = Ui.B("#FF5500"), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = new TextBlock { Text = "Create", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
                Ui.Hover(createBtn, "#FF7733");
                createBtn.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    string title = titleBox.Text.Trim();
                    if (title.Length == 0) { ShowToast("Give the playlist a title first."); return; }
                    CloseMenu();
                    _ = CreateAsync(title, isPrivate);
                };
                body.Children.Add(createBtn);

                async System.Threading.Tasks.Task CreateAsync(string title, bool priv)
                {
                    try { await api.CreatePlaylistAsync(title, priv, tr.Id); ShowToast("Created “" + title + "” and added the track", true); }
                    catch (Exception ex) { Store.Log("create playlist", ex); ShowToast(ex.Message); }
                }
            }

            SetTab(true);
            OpenMenu(anchor, root);
        }

        Border PlaylistPickRow(ScPlaylist pl, ScTrack tr)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var art = ArtBox(pl.ArtworkUrl ?? "", 40, false, "t200x200", 80);
            art.CornerRadius = new CornerRadius(5);

            var texts = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = pl.Title, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 });
            if (pl.IsPrivate)
            {
                titleRow.Children.Add(new Border
                {
                    Background = Ui.B("#333333"), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "PRIVATE", Foreground = Ui.B("#b0b0b0"), FontSize = 9, FontWeight = FontWeights.Bold },
                });
            }
            texts.Children.Add(titleRow);
            texts.Children.Add(new TextBlock { Text = pl.TrackCount + (pl.TrackCount == 1 ? " track" : " tracks"), Foreground = Ui.B("#8a8a8a"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });

            var addBtn = new Border
            {
                Height = 26, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(13), Background = Ui.B("#2E2E2E"), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "Add to Playlist", Foreground = Ui.B("#F2F2F2"), FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            };
            Ui.Hover(addBtn, "#3C3C3C");
            addBtn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                CloseMenu();
                _ = AddAsync();
            };
            async System.Threading.Tasks.Task AddAsync()
            {
                try { await api.AddTrackToPlaylistAsync(pl.Id, tr.Id); ShowToast("Added to " + pl.Title, true); }
                catch (Exception ex) { Store.Log("add to playlist", ex); ShowToast(ex.Message); }
            }

            Grid.SetColumn(art, 0); Grid.SetColumn(texts, 1); Grid.SetColumn(addBtn, 2);
            g.Children.Add(art); g.Children.Add(texts); g.Children.Add(addBtn);

            return new Border { Padding = new Thickness(2, 4, 2, 4), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = g };
        }
    }
}
