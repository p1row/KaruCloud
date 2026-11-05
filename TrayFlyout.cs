using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WinForms = System.Windows.Forms;

namespace KaruCloud
{
    public sealed class TrayInfo
    {
        public NowPlaying? Np;
        public List<Account> Accounts = new List<Account>();
        public string Current = "";
        public int Volume = 80;
        public bool WindowVisible;
    }

    /// <summary>Small dark flyout shown when you right-click the tray icon.</summary>
    public sealed class TrayFlyout : Window
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        static SolidColorBrush B(string hex) => Ui.B(hex);

        readonly MainWindow main;
        readonly System.Drawing.Point anchor;
        readonly TextBlock playGlyph = new TextBlock();
        readonly TextBlock volGlyph = new TextBlock();
        readonly TextBlock volLabel = new TextBlock();
        bool closing;
        bool playing;

        public TrayFlyout(MainWindow main, TrayInfo info, System.Drawing.Point anchor)
        {
            this.main = main;
            this.anchor = anchor;
            playing = info.Np != null && info.Np.Playing;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Left = -10000; Top = -10000;

            var stack = new StackPanel { Width = 264 };

            bool has = info.Np != null && info.Np.Title.Length > 0;
            stack.Children.Add(new TextBlock
            {
                Text = has ? info.Np!.Title : "Nothing playing",
                Foreground = has ? Brushes.White : B("#8a8a8a"),
                FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (has && info.Np!.Artist.Length > 0)
                stack.Children.Add(new TextBlock
                {
                    Text = info.Np.Artist, Foreground = B("#9a9a9a"), FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
                });

            var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 12) };
            transport.Children.Add(Ui.RoundBtn(new TextBlock { Text = "\uE892" }, 34, Brushes.Transparent, Brushes.White, "#22FFFFFF", () => main.TrayMedia("prev")));
            playGlyph.Text = playing ? "\uE769" : "\uE768";
            var play = Ui.RoundBtn(playGlyph, 42, Brushes.White, Brushes.Black, "#DDDDDD", () =>
            {
                main.TrayMedia("toggle");
                playing = !playing;
                playGlyph.Text = playing ? "\uE769" : "\uE768";
            });
            play.Margin = new Thickness(10, 0, 10, 0);
            transport.Children.Add(play);
            transport.Children.Add(Ui.RoundBtn(new TextBlock { Text = "\uE893" }, 34, Brushes.Transparent, Brushes.White, "#22FFFFFF", () => main.TrayMedia("next")));
            stack.Children.Add(transport);

            var volRow = new Grid { Margin = new Thickness(2, 0, 2, 8) };
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            volGlyph.FontFamily = Ui.Icons; volGlyph.FontSize = 14; volGlyph.Foreground = B("#cccccc");
            volGlyph.VerticalAlignment = VerticalAlignment.Center; volGlyph.Width = 24;
            volLabel.Foreground = B("#8a8a8a"); volLabel.FontSize = 11;
            volLabel.VerticalAlignment = VerticalAlignment.Center; volLabel.HorizontalAlignment = HorizontalAlignment.Right;
            var slider = new MiniSlider(100, info.Volume) { Margin = new Thickness(4, 0, 6, 0) };
            UpdateVolumeLabels(info.Volume);
            slider.Changed += (v, commit) => { UpdateVolumeLabels(v); main.SetVolume(v, commit); };
            slider.MouseWheel += (_, e) => slider.Wheel(e.Delta > 0 ? 5 : -5);
            Grid.SetColumn(volGlyph, 0); Grid.SetColumn(slider, 1); Grid.SetColumn(volLabel, 2);
            volRow.Children.Add(volGlyph); volRow.Children.Add(slider); volRow.Children.Add(volLabel);
            stack.Children.Add(volRow);

            stack.Children.Add(Divider());
            stack.Children.Add(Row(info.WindowVisible ? "Hide KaruCloud" : "Open KaruCloud", () => main.ToggleWindow()));

            if (info.Accounts.Count > 1)
            {
                foreach (var a in info.Accounts)
                {
                    var id = a.Id;
                    stack.Children.Add(Row((a.Id == info.Current ? "●  " : "○  ") + a.Name, () => main.SwitchAccount(id)));
                }
            }

            stack.Children.Add(Row("Settings", () => main.OpenSettings()));
            stack.Children.Add(Divider());
            stack.Children.Add(Row("Quit", () => main.ExitApp()));

            Content = new Border
            {
                Margin = new Thickness(14),
                CornerRadius = new CornerRadius(24),
                Background = B("#E8151515"),
                BorderBrush = B("#2EFFFFFF"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 14, 14, 8),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black },
                Child = stack,
            };

            Deactivated += (_, _) => CloseOnce();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseOnce(); };
            Loaded += (_, _) => Place();
        }

        void CloseOnce()
        {
            if (closing) return;
            closing = true;
            try { Close(); } catch { }
        }

        void UpdateVolumeLabels(int v)
        {
            volGlyph.Text = v == 0 ? "\uE74F" : "\uE767";
            volLabel.Text = v.ToString();
        }

        static Border Divider() => new Border { Height = 1, Background = B("#1FFFFFFF"), Margin = new Thickness(0, 4, 0, 4) };

        Border Row(string text, Action action)
        {
            var b = new Border
            {
                Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(13),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, Foreground = B("#e2e2e2"), FontSize = 13 },
            };
            b.MouseEnter += (_, _) => b.Background = B("#1FFFFFFF");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, _) =>
            {
                CloseOnce();
                Application.Current.Dispatcher.BeginInvoke(action);
            };
            return b;
        }

        void Place()
        {
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            var wa = WinForms.Screen.FromPoint(anchor).WorkingArea;
            double w = ActualWidth * sx, h = ActualHeight * sy;
            double x = anchor.X - w / 2;
            double y = anchor.Y - h - 6 * sy;
            if (y < wa.Top) y = anchor.Y + 6 * sy;
            x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - w - 4));
            y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - h - 4));
            Left = x / sx; Top = y / sy;
            Activate();
            try { SetForegroundWindow(new WindowInteropHelper(this).Handle); } catch { }
        }
    }
}
