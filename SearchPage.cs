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
    /// Search results laid out like soundcloud.com: the heading, a filter column on the left (Everything / Tracks / People / Albums / Playlists),
    /// "Found 7 playlists, 33 tracks, 3 people", the best-matching person on top, then tracks as blocks with a waveform.
    /// </summary>
    public sealed partial class MainWindow
    {
        static readonly (string Scope, string Label)[] SearchScopes =
        {
            ("all", "Everything"), ("tracks", "Tracks"), ("people", "People"), ("albums", "Albums"), ("playlists", "Playlists"),
        };

        /// <summary>Switches an open search between Everything / Tracks / People / Albums / Playlists (same query, new list).</summary>
        void SetSearchScope(PaneTab t, string scope)
        {
            if (ScopeOf(t.Kind) == scope) return;
            searchScope = scope;
            string label = SearchScopes.First(x => x.Scope == scope).Label;
            scopeChip.Text = (scope == "all" ? "All" : label) + " ▾";
            searchHint.Text = scope switch { "tracks" => "Search tracks", "playlists" => "Search playlists", "albums" => "Search albums", "people" => "Search people", _ => "Search SoundCloud" };
            t.Kind = SearchKind();
            t.Loaded = false;
            RefreshTabs(); QueueSave();
            _ = LoadAsync(t, false);
        }

        UIElement BuildSearchPage(PaneTab t)
        {
            var page = new StackPanel { Margin = new Thickness(30, 18, 30, 28), HorizontalAlignment = HorizontalAlignment.Stretch };
            page.Children.Add(new TextBlock
            {
                Text = "Search results for \u201C" + t.Param + "\u201D", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 20),
            });

            var cols = new Grid();
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---- filter column ----
            var filters = new StackPanel { Margin = new Thickness(0, 0, 26, 0), VerticalAlignment = VerticalAlignment.Top };
            string current = ScopeOf(t.Kind);
            foreach (var (scope, label) in SearchScopes)
            {
                bool on = scope == current;
                string sc = scope;
                var text = Txt(label, 14, on ? "#111111" : "#F2F2F2", true);
                text.VerticalAlignment = VerticalAlignment.Center;
                var b = new Border
                {
                    Height = 36, CornerRadius = new CornerRadius(5), Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 0, 4),
                    Background = on ? Brushes.White : Brushes.Transparent, Cursor = on ? Cursors.Arrow : Cursors.Hand, Child = text,
                };
                if (!on) Ui.Hover(b, "#26FFFFFF");
                b.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetSearchScope(t, sc); };
                filters.Children.Add(b);
            }
            Grid.SetColumn(filters, 0);

            // ---- results column ----
            var res = new StackPanel();
            string found = FoundLine(t);
            if (found.Length > 0)
                res.Children.Add(new TextBlock { Text = found, Foreground = Ui.B("#9A9A9A"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 22), TextWrapping = TextWrapping.Wrap });

            var rows = t.Rows.Where(r => (r.Kind == "track" && r.Track != null) || (r.Kind == "playlist" && r.Playlist != null) || (r.Kind == "user" && r.User != null)).ToList();

            // on "Everything" the best-matching person leads the page, as on soundcloud.com
            ItemVM? hero = null;
            if (current == "all")
            {
                int ix = rows.FindIndex(r => r.Kind == "user");
                if (ix >= 0 && ix < 3) hero = rows[ix];
            }
            if (hero != null) res.Children.Add(BuildPersonHero(hero));

            foreach (var r in rows)
            {
                if (ReferenceEquals(r, hero)) continue;
                FrameworkElement? el = r.Kind == "track" ? BuildFeedTrack(t, r, true)
                    : r.Kind == "playlist" ? BuildFeedPlaylist(r, true)
                    : BuildPersonRow(r);
                if (el != null) res.Children.Add(el);
            }
            if (t.Rows.Any(r => r.Kind == "more")) res.Children.Add(BuildMoreButton(t));
            Grid.SetColumn(res, 1);

            cols.Children.Add(filters); cols.Children.Add(res);
            page.Children.Add(cols);
            return page;
        }

        /// <summary>"Found 7 playlists, 33 tracks, 3 people" from SoundCloud's totals; without them, from what has been loaded so far.</summary>
        string FoundLine(PaneTab t)
        {
            long Facet(params string[] keys)
            {
                foreach (var kv in t.Facets)
                    foreach (var k in keys)
                        if (kv.Key.Contains(k)) return kv.Value;
                return 0;
            }
            long playlists = Facet("playlist"), tracks = Facet("track", "sound"), people = Facet("user", "people"), albums = Facet("album");
            bool exact = playlists + tracks + people + albums > 0;
            if (!exact)
            {
                playlists = t.Rows.Count(r => r.Kind == "playlist" && !(r.Playlist != null && string.Equals(r.Playlist.SetType, "album", StringComparison.OrdinalIgnoreCase)));
                albums = t.Rows.Count(r => r.Kind == "playlist" && r.Playlist != null && string.Equals(r.Playlist.SetType, "album", StringComparison.OrdinalIgnoreCase));
                tracks = t.Rows.Count(r => r.Kind == "track");
                people = t.Rows.Count(r => r.Kind == "user");
                if (playlists + tracks + people + albums == 0) return "";
            }
            string more = !exact && t.Next != null ? "+" : "";
            var parts = new List<string>();
            if (playlists > 0) parts.Add(playlists + more + (playlists == 1 && more.Length == 0 ? " playlist" : " playlists"));
            if (albums > 0) parts.Add(albums + more + (albums == 1 && more.Length == 0 ? " album" : " albums"));
            if (tracks > 0) parts.Add(tracks + more + (tracks == 1 && more.Length == 0 ? " track" : " tracks"));
            if (people > 0) parts.Add(people + more + (people == 1 && more.Length == 0 ? " person" : " people"));
            return "Found " + string.Join(", ", parts);
        }

        static string Place(ScUser u)
        {
            string city = (u.City ?? "").Trim(), cc = (u.CountryCode ?? "").Trim().ToUpperInvariant();
            return city.Length > 0 && cc.Length > 0 ? city + ", " + cc : city.Length > 0 ? city : cc;
        }

        /// <summary>The best-matching person: big round picture, name, place, followers and a Follow button.</summary>
        FrameworkElement BuildPersonHero(ItemVM it)
        {
            var u = it.User!;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 30) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var av = ArtBox(u.AvatarUrl ?? "", 132, true, "t200x200", 264);
            av.Cursor = Cursors.Hand;
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(u); };

            var info = new StackPanel { Margin = new Thickness(22, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(PersonName(u, 20));
            string place = Place(u);
            if (place.Length > 0) info.Children.Add(new TextBlock { Text = place, Foreground = Ui.B("#9A9A9A"), FontSize = 13, Margin = new Thickness(0, 3, 0, 0) });
            info.Children.Add(PersonCounts(u, new Thickness(0, 6, 0, 0)));

            var follow = MakeFollowBtn(u);
            follow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(info, 1); Grid.SetColumn(follow, 2);
            g.Children.Add(av); g.Children.Add(info); g.Children.Add(follow);
            return g;
        }

        /// <summary>A person further down the results (People tab, or more than one match): small picture, name, counts, Follow.</summary>
        FrameworkElement BuildPersonRow(ItemVM it)
        {
            var u = it.User!;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var av = ArtBox(u.AvatarUrl ?? "", 72, true, "t200x200", 144);
            av.Cursor = Cursors.Hand;
            av.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenArtist(u); };

            var info = new StackPanel { Margin = new Thickness(16, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(PersonName(u, 15));
            string place = Place(u);
            if (place.Length > 0) info.Children.Add(new TextBlock { Text = place, Foreground = Ui.B("#9A9A9A"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) });
            info.Children.Add(PersonCounts(u, new Thickness(0, 4, 0, 0)));

            var follow = MakeFollowBtn(u);
            follow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(info, 1); Grid.SetColumn(follow, 2);
            g.Children.Add(av); g.Children.Add(info); g.Children.Add(follow);
            g.MouseRightButtonUp += (_, e) => { if (ShowItemMenu(it, g, System.Windows.Controls.Primitives.PlacementMode.MousePoint)) e.Handled = true; };
            return g;
        }

        FrameworkElement PersonName(ScUser u, double size)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Link(u.Username ?? "", size, "#FFFFFF", () => OpenArtist(u), true));
            if (u.Verified == true)
                row.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = Ui.Icons, FontSize = size * 0.6, Foreground = Ui.B("#FF7A3D"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), ToolTip = "Verified" });
            return row;
        }

        FrameworkElement PersonCounts(ScUser u, Thickness margin)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
            if (u.Followers > 0)
            {
                row.Children.Add(new TextBlock { Text = "\uE77B", FontFamily = Ui.Icons, FontSize = 12, Foreground = Ui.B("#9A9A9A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                row.Children.Add(Txt(Ui.Count(u.Followers), 12.5, "#9A9A9A"));
            }
            if (u.TrackCount > 0)
            {
                row.Children.Add(new TextBlock { Text = "\uE8D6", FontFamily = Ui.Icons, FontSize = 12, Foreground = Ui.B("#9A9A9A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(u.Followers > 0 ? 16 : 0, 0, 6, 0) });
                row.Children.Add(Txt(Ui.Count(u.TrackCount), 12.5, "#9A9A9A"));
            }
            return row;
        }
    }
}
