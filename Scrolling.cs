using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KaruCloud
{
    // Moving tabs around (drag), the wheel over the tab strip, and browser-style middle-click auto-scroll.
    public sealed partial class MainWindow
    {
        // ================= drag a tab to a new position =================
        PaneTab? dragTab;
        Point dragStart;
        bool dragging;
        readonly Dictionary<PaneTab, Border> tabBorders = new Dictionary<PaneTab, Border>();

        /// <summary>Called once from the constructor.</summary>
        void HookScrolling()
        {
            tabStrip.PreviewMouseMove += (_, e) => OnTabDragMove(e);
            tabStrip.PreviewMouseLeftButtonUp += (_, _) => EndTabDrag();
            tabStrip.LostMouseCapture += (_, _) => EndTabDrag();

            // Hover the tab strip and turn the wheel: the tabs slide left / right (useful when there are more tabs than fit).
            tabScroll.PreviewMouseWheel += (_, e) =>
            {
                tabScroll.ScrollToHorizontalOffset(tabScroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            };

            // Song lists scroll by pixels (not whole rows) so the wheel and auto-scroll feel smooth.
            VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);

            // the little auto-scroll marker (drawn above everything, never takes the mouse)
            asLayer.IsHitTestVisible = false;
            asLayer.Visibility = Visibility.Collapsed;
            asLayer.ClipToBounds = true;
            Grid.SetRowSpan(asLayer, 3);
            asLayer.Children.Add(BuildAutoScrollMark());
            rootGrid.Children.Add(asLayer);

            asTimer.Tick += (_, _) => AutoScrollTick();
        }

        void BeginTabDrag(PaneTab t, MouseButtonEventArgs e)
        {
            dragTab = t;
            dragging = false;
            dragStart = e.GetPosition(tabStrip);
        }

        void OnTabDragMove(MouseEventArgs e)
        {
            if (dragTab == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndTabDrag(); return; }
            var p = e.GetPosition(tabStrip);
            if (!dragging)
            {
                if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance + 2) return;
                if (!tabBorders.ContainsKey(dragTab)) { EndTabDrag(); return; }
                dragging = true;
                tabStrip.CaptureMouse();
                Mouse.OverrideCursor = Cursors.SizeWE;
                tabBorders[dragTab].Opacity = 0.75;
            }

            // near either end of the strip: slide the strip so far-away positions can be reached
            var pw = e.GetPosition(tabScroll);
            if (pw.X < 28) tabScroll.ScrollToHorizontalOffset(tabScroll.HorizontalOffset - 14);
            else if (pw.X > tabScroll.ActualWidth - 28) tabScroll.ScrollToHorizontalOffset(tabScroll.HorizontalOffset + 14);

            if (!tabBorders.TryGetValue(dragTab, out var me)) return;
            int cur = tabStrip.Children.IndexOf(me);
            if (cur < 0) return;
            int target = 0;
            foreach (UIElement c in tabStrip.Children)
            {
                if (ReferenceEquals(c, me) || c is not FrameworkElement fe) continue;
                double mid = fe.TranslatePoint(new Point(fe.ActualWidth / 2, 0), tabStrip).X;
                if (mid < p.X) target++;
            }
            if (target != cur)
            {
                tabStrip.Children.RemoveAt(cur);
                tabStrip.Children.Insert(target, me);
                tabStrip.UpdateLayout();
            }
        }

        void EndTabDrag()
        {
            bool was = dragging;
            dragTab = null;
            dragging = false;
            if (!was) return;
            if (tabStrip.IsMouseCaptured) tabStrip.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;

            // the order on screen becomes the real order (only this account's tabs move; other accounts' tabs keep their places)
            var order = tabStrip.Children.OfType<Border>().Select(b => b.Tag as PaneTab).Where(x => x != null).Select(x => x!).ToList();
            var slots = new List<int>();
            for (int i = 0; i < tabs.Count; i++) if (tabs[i].Acct == cfg.Current) slots.Add(i);
            if (slots.Count == order.Count)
                for (int k = 0; k < slots.Count; k++) tabs[slots[k]] = order[k];
            RefreshTabs();
            QueueSave();
        }

        // ================= middle-click auto-scroll (like a browser) =================
        // Middle-click on empty page space: a marker appears; move the mouse up / down and the page scrolls,
        // faster the further you are from the marker. Click again (any button), press Esc or turn the wheel to stop.
        // Or hold the middle button, drag, and let go to stop.
        ScrollViewer? asSv;
        Point asOrigin;
        bool asHoldDrag;
        MouseButton? asSwallow;
        long asLast;
        readonly Stopwatch asWatch = new Stopwatch();
        readonly Canvas asLayer = new Canvas();
        readonly DispatcherTimer asTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };

        static UIElement BuildAutoScrollMark()
        {
            var c = new Canvas { Width = 32, Height = 32, Tag = "asMark" };
            c.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 32, Height = 32, Fill = Ui.B("#E6181818"), Stroke = Ui.B("#99FFFFFF"), StrokeThickness = 1.5,
            });
            c.Children.Add(new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = Brushes.White });
            Canvas.SetLeft((UIElement)c.Children[1], 14); Canvas.SetTop((UIElement)c.Children[1], 14);
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M16,5 L11,12 L21,12 Z"), Fill = Brushes.White, Opacity = 0.9 });
            c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M16,27 L11,20 L21,20 Z"), Fill = Brushes.White, Opacity = 0.9 });
            return c;
        }

        /// <summary>The nearest scrollable-up-and-down area under the mouse (the song page, a card page, the song list, the sidebar...).</summary>
        static ScrollViewer? FindVerticalScroller(DependencyObject? src)
        {
            for (var d = src; d != null; d = ParentOf(d))
                if (d is ScrollViewer sv && sv.ScrollableHeight > 0.5 && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                    return sv;
            return null;
        }

        /// <summary>Returns true when the click was used by auto-scroll (started or stopped it), so the caller stops handling it.</summary>
        bool AutoScrollMouseDown(MouseButtonEventArgs e)
        {
            if (asSv != null)
            {
                // while it runs, any click stops it (and the click itself does nothing else, like in a browser)
                StopAutoScroll();
                asSwallow = e.ChangedButton;
                e.Handled = true;
                return true;
            }
            if (e.ChangedButton != MouseButton.Middle) return false;
            if (TryMiddleTarget(e, out _)) return false; // over a link / song title: middle click opens it in a new tab instead
            var sv = FindVerticalScroller(e.OriginalSource as DependencyObject);
            if (sv == null) return false;
            StartAutoScroll(sv, e.GetPosition(rootGrid));
            e.Handled = true;
            return true;
        }

        /// <summary>Returns true when the mouse-up belongs to auto-scroll (so it must not click or open anything).</summary>
        bool AutoScrollMouseUp(MouseButtonEventArgs e)
        {
            if (asSwallow != null && e.ChangedButton == asSwallow)
            {
                asSwallow = null;
                e.Handled = true;
                return true;
            }
            if (asSv != null && e.ChangedButton == MouseButton.Middle)
            {
                if (asHoldDrag) StopAutoScroll(); // held, dragged and released: done
                e.Handled = true;
                return true;
            }
            return false;
        }

        void StartAutoScroll(ScrollViewer sv, Point origin)
        {
            asSv = sv;
            asOrigin = origin;
            asHoldDrag = false;
            asSwallow = null;
            var mark = asLayer.Children.OfType<Canvas>().First();
            Canvas.SetLeft(mark, origin.X - 16);
            Canvas.SetTop(mark, origin.Y - 16);
            asLayer.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.ScrollNS;
            asWatch.Restart();
            asLast = 0;
            asTimer.Start();
        }

        void StopAutoScroll()
        {
            if (asSv == null) return;
            asSv = null;
            asTimer.Stop();
            asWatch.Stop();
            asLayer.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
        }

        void AutoScrollTick()
        {
            var sv = asSv;
            if (sv == null) { asTimer.Stop(); return; }
            // the page went away (another tab, minimized, another window in front): stop
            if (!sv.IsVisible || !IsVisible || !IsActive) { StopAutoScroll(); return; }

            long now = asWatch.ElapsedTicks;
            double dt = (now - asLast) / (double)Stopwatch.Frequency;
            asLast = now;
            if (dt <= 0) return;
            if (dt > 0.05) dt = 0.05;

            double dy = Mouse.GetPosition(rootGrid).Y - asOrigin.Y;
            if (Mouse.MiddleButton == MouseButtonState.Pressed && Math.Abs(dy) > 10) asHoldDrag = true;
            const double dead = 12;
            if (Math.Abs(dy) <= dead) return;
            double speed = Math.Sign(dy) * Math.Min(4000, Math.Pow(Math.Abs(dy) - dead, 1.25) * 5); // pixels per second
            sv.ScrollToVerticalOffset(sv.VerticalOffset + speed * dt);
        }

        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            if (asSv != null) StopAutoScroll();
            base.OnPreviewMouseWheel(e);
        }

        protected override void OnDeactivated(EventArgs e)
        {
            StopAutoScroll();
            if (dragging) EndTabDrag();
            base.OnDeactivated(e);
        }
    }
}
