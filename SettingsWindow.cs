using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace KaruCloud
{
    /// <summary>Native dark settings panel (same look as the tray flyout; no web view).</summary>
    public sealed class SettingsWindow : Window
    {
        static SolidColorBrush B(string hex) => Ui.B(hex);

        readonly MainWindow main;
        readonly StackPanel body = new StackPanel { Margin = new Thickness(16, 0, 16, 16) };
        TextBlock? rpcStatus;
        Popup? menu;
        string sig = "";

        public SettingsWindow(MainWindow main)
        {
            this.main = main;
            Title = "KaruCloud Settings";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 392;
            Height = Math.Min(780, SystemParameters.WorkArea.Height - 40);
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico")!)
                Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            var header = new Grid { Margin = new Thickness(18, 14, 12, 8), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { Text = "Settings", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var reset = TextBtn("Reset", () =>
            {
                if (MessageBox.Show("Reset presence and app settings to defaults? (Accounts are kept.)", "KaruCloud",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    main.ResetPrefs(); sig = ""; Build();
                }
            });
            var close = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "\uE8BB", FontFamily = Ui.Icons, FontSize = 10, Foreground = B("#cccccc"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            close.MouseEnter += (_, _) => close.Background = B("#26FFFFFF");
            close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
            close.MouseLeftButtonDown += (_, e) => e.Handled = true;
            close.MouseLeftButtonUp += (_, _) => Close();
            Grid.SetColumn(title, 0); Grid.SetColumn(reset, 1); Grid.SetColumn(close, 2);
            header.Children.Add(title); header.Children.Add(reset); header.Children.Add(close);
            header.MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch { } };

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = body };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(header, 0); Grid.SetRow(scroll, 1);
            grid.Children.Add(header); grid.Children.Add(scroll);

            Content = new Border
            {
                Margin = new Thickness(14),
                CornerRadius = new CornerRadius(26),
                Background = B("#F2141414"),
                BorderBrush = B("#2EFFFFFF"),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black },
                Child = grid,
            };

            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Closed += (_, _) => main.SettingsClosed(this);
            Build();
        }

        // ---------- Content ----------
        void Build()
        {
            CloseMenu();
            body.Children.Clear();
            rpcStatus = null;
            var s = main.Prefs;

            // --- Accounts ---
            Section("SoundCloud accounts");
            var acctPanel = new StackPanel();
            acctPanel.Children.Add(new TextBlock
            {
                Text = "You sign in on SoundCloud's own page (e-mail, Google, Apple or Facebook, or create an account there). KaruCloud only keeps the session, never your password.",
                Foreground = B("#7a7a7a"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4),
            });
            var signIn = TextBtn(main.AccountList.Count == 0 ? "Sign in or sign up" : "Add another account", () => main.StartSignIn());
            signIn.HorizontalAlignment = HorizontalAlignment.Left;
            signIn.Margin = new Thickness(-8, 0, 0, 4);
            acctPanel.Children.Add(signIn);
            foreach (var a in main.AccountList.ToList())
            {
                var id = a.Id;
                var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = Field(a.Name, "Name", v => main.RenameAccount(id, v));
                FrameworkElement tag = id == main.CurrentAccount
                    ? Pill(main.IsAccountPlaying(id) ? "active  ♪" : "active")
                    : TextBtn("Switch", () => main.SwitchAccount(id));
                tag.Margin = new Thickness(8, 0, 0, 0);
                var del = TextBtn("Sign out", () =>
                {
                    if (MessageBox.Show("Sign out \"" + a.Name + "\" from KaruCloud? (Your SoundCloud account itself is not touched.)", "KaruCloud",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        main.Disconnect(id);
                });
                del.Margin = new Thickness(2, 0, 0, 0);
                Grid.SetColumn(name, 0); Grid.SetColumn(tag, 1); Grid.SetColumn(del, 2);
                row.Children.Add(name); row.Children.Add(tag); row.Children.Add(del);
                acctPanel.Children.Add(row);
            }
            Card(acctPanel);

            // --- Presence ---
            Section("Discord presence");
            Card(
                Row("Show browsing status", Toggle(s.ShowBrowsing, v => { s.ShowBrowsing = v; Save(); })),
                Row("Show current song", Toggle(s.ShowSong, v => { s.ShowSong = v; Save(); })),
                Row("Hide while paused", Toggle(s.HideWhilePaused, v => { s.HideWhilePaused = v; Save(); })),
                Row("Show timestamps", Toggle(s.ShowTimestamps, v => { s.ShowTimestamps = v; Save(); })),
                Row("Show cover", Toggle(s.ShowCover, v => { s.ShowCover = v; Save(); })),
                Row("Link to song / artist", Toggle(s.LinkButtons, v => { s.LinkButtons = v; Save(); })),
                Row("Status display", Select(new[] { "Artist name", "Song name", "App name" }, new[] { "artist", "song", "app" }, s.StatusDisplay, v => { s.StatusDisplay = v; Save(); })));

            Section("App");
            Card(
                Row("Close button hides to tray", Toggle(s.CloseToTray, v => { s.CloseToTray = v; Save(); })),
                Row("Minimize hides to tray", Toggle(s.MinimizeToTray, v => { s.MinimizeToTray = v; Save(); })),
                Row("Start minimized (tray only)", Toggle(s.StartMinimized, v => { s.StartMinimized = v; Save(); })),
                Row("Launch at Windows startup", Toggle(s.LaunchAtLogin, v => { s.LaunchAtLogin = v; Save(); })));

            body.Children.Add(new TextBlock
            {
                Text = "KaruCloud is an unofficial app. It isn't affiliated with or endorsed by SoundCloud.",
                Foreground = B("#5e5e5e"), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 16, 4, 0),
            });

            sig = Signature();
        }

        string Signature() => JsonSerializer.Serialize(new
        {
            a = main.AccountList,
            c = main.CurrentAccount,
            p = main.AccountList.Select(x => main.IsAccountPlaying(x.Id)).ToList(),
        });

        /// <summary>Called by the main window when accounts / playback / Discord status change.</summary>
        public void Refresh()
        {
            UpdateRpcStatus();
            var now = Signature();
            if (now == sig) return;
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox) return;
            Build();
        }

        void UpdateRpcStatus()
        {
            if (rpcStatus == null) return;
            switch (main.RpcStatus)
            {
                case "ready": rpcStatus.Text = "Connected" + (main.RpcUser.Length > 0 ? " as " + main.RpcUser : ""); rpcStatus.Foreground = B("#6fe39a"); break;
                case "error": rpcStatus.Text = "Discord refused the connection. Restart Discord and KaruCloud."; rpcStatus.Foreground = B("#ff8585"); break;
                case "connecting": rpcStatus.Text = "Connecting…"; rpcStatus.Foreground = B("#9a9a9a"); break;
                case "waiting": rpcStatus.Text = "Waiting for the Discord desktop app…"; rpcStatus.Foreground = B("#9a9a9a"); break;
                default: rpcStatus.Text = "Starting…"; rpcStatus.Foreground = B("#7a7a7a"); break;
            }
        }

        void Save() => main.PrefsChanged();

        // ---------- Building blocks ----------
        void Section(string text) => body.Children.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(), Foreground = B("#777777"), FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 16, 0, 6),
        });

        void Card(params UIElement[] rows)
        {
            var panel = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) panel.Children.Add(new Border { Height = 1, Background = B("#1FFFFFFF") });
                panel.Children.Add(rows[i]);
            }
            body.Children.Add(new Border
            {
                Background = B("#1D1D1D"), CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 0, 16, 0), Child = panel,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        static Grid Row(string label, UIElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 9, 0, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new TextBlock { Text = label, Foreground = B("#e4e4e4"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, 0); Grid.SetColumn(control, 1);
            ((FrameworkElement)control).VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(t); g.Children.Add(control);
            return g;
        }

        static StackPanel Labeled(string label, UIElement field)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            p.Children.Add(new TextBlock { Text = label, Foreground = B("#9a9a9a"), FontSize = 11, Margin = new Thickness(2, 0, 0, 4) });
            p.Children.Add(field);
            return p;
        }

        static Border Toggle(bool on, Action<bool> change)
        {
            var thumb = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var track = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Child = thumb };
            void Apply()
            {
                track.Background = B(on ? "#FF5500" : "#3A3A3A");
                thumb.Margin = new Thickness(on ? 19 : 3, 0, 0, 0);
            }
            Apply();
            track.MouseLeftButtonUp += (_, _) => { on = !on; Apply(); change(on); };
            return track;
        }

        Border Select(string[] labels, string[] values, string current, Action<string> change)
        {
            int idx = Math.Max(0, Array.IndexOf(values, current));
            var text = new TextBlock { Text = labels[idx], Foreground = B("#e6e6e6"), FontSize = 12.5 };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(text);
            row.Children.Add(new TextBlock { Text = "  ▾", Foreground = B("#8a8a8a"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var pill = new Border { Background = B("#2A2A2A"), CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 6, 9, 6), Cursor = Cursors.Hand, Child = row };
            pill.MouseEnter += (_, _) => pill.Background = B("#343434");
            pill.MouseLeave += (_, _) => pill.Background = B("#2A2A2A");
            pill.MouseLeftButtonUp += (_, _) =>
            {
                CloseMenu();
                var list = new StackPanel { Margin = new Thickness(4) };
                for (int i = 0; i < labels.Length; i++)
                {
                    int j = i;
                    var item = new Border
                    {
                        Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(11), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                        Child = new TextBlock { Text = labels[i], FontSize = 12.5, Foreground = i == idx ? B("#FF7A3D") : B("#e6e6e6") },
                    };
                    item.MouseEnter += (_, _) => item.Background = B("#26FFFFFF");
                    item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
                    item.MouseLeftButtonUp += (_, _) =>
                    {
                        idx = j; text.Text = labels[j];
                        CloseMenu();
                        change(values[j]);
                    };
                    list.Children.Add(item);
                }
                menu = new Popup
                {
                    PlacementTarget = pill, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                    Child = new Border
                    {
                        Background = B("#F01C1C1C"), BorderBrush = B("#33FFFFFF"), BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(16), MinWidth = 150, Child = list,
                    },
                };
                menu.IsOpen = true;
            };
            return pill;
        }

        void CloseMenu()
        {
            if (menu != null) { try { menu.IsOpen = false; } catch { } menu = null; }
        }

        static Border Field(string value, string placeholder, Action<string> commit)
        {
            var tb = new TextBox
            {
                Text = value, Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White,
                SelectionBrush = B("#FF5500"), BorderThickness = new Thickness(0), Padding = new Thickness(0),
                FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, Tag = "field",
            };
            var hint = new TextBlock
            {
                Text = placeholder, Foreground = B("#666666"), FontSize = 12.5, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            tb.TextChanged += (_, _) => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            tb.LostFocus += (_, _) => commit(tb.Text.Trim());
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(tb.Text.Trim()); Keyboard.ClearFocus(); } };
            var g = new Grid();
            g.Children.Add(hint); g.Children.Add(tb);
            return new Border { Background = B("#2A2A2A"), CornerRadius = new CornerRadius(13), Padding = new Thickness(12, 8, 12, 8), Child = g };
        }

        static Border TextBtn(string text, Action click)
        {
            var b = new Border
            {
                Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, Foreground = B("#b8b8b8"), FontSize = 12 },
            };
            b.MouseEnter += (_, _) => b.Background = B("#26FFFFFF");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (_, e) => e.Handled = true; // don't start a window drag
            b.MouseLeftButtonUp += (_, _) => click();
            return b;
        }

        static Border Pill(string text) => new Border
        {
            Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(10), Background = B("#33FF5500"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = B("#FF8A4D"), FontSize = 11, FontWeight = FontWeights.SemiBold },
        };
    }
}
