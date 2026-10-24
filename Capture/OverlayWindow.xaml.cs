using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SnipSnip
{
    /// <summary>Vollbild-Overlay für genau einen Monitor; zeigt den eingefrorenen Bildschirm.</summary>
    public partial class OverlayWindow : Window
    {
        readonly CaptureSession session;
        readonly Rect mon; // physische Monitorgrenzen
        readonly ImageBrush loupeBrush;

        public OverlayWindow(CaptureSession s, Rect monitor, BitmapSource shot)
        {
            InitializeComponent();
            session = s;
            mon = monitor;
            Shot.Source = shot;

            loupeBrush = new ImageBrush(s.Frozen) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(loupeBrush, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetBitmapScalingMode(LoupeImg, BitmapScalingMode.NearestNeighbor);
            LoupeImg.Fill = loupeBrush;

            SourceInitialized += (_, _) => ApplyBounds();
            Loaded += (_, _) => { ApplyBounds(); Render(); };
            DpiChanged += (_, _) => Dispatcher.InvokeAsync(ApplyBounds, DispatcherPriority.Background);
            SizeChanged += (_, _) => Render();
        }

        /// <summary>Exakt auf die physischen Monitorgrenzen setzen (unabhängig von der DPI-Skalierung).</summary>
        void ApplyBounds()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, (int)mon.X, (int)mon.Y, (int)mon.Width, (int)mon.Height, Native.SWP_NOACTIVATE);
        }

        public void AttachToolbar(CaptureToolbar tb) => ToolbarHost.Content = tb;

        bool IsInToolbar(object? src)
        {
            var d = src as DependencyObject;
            while (d != null)
            {
                if (d == ToolbarHost) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        /// <summary>
        /// Liegt der Punkt im Layout-Bereich der Leiste? Geometrisch geprüft – unabhängig davon,
        /// ob die Leiste gerade noch einfährt (dann trifft der WPF-Hit-Test das Bild darunter).
        /// </summary>
        /// <summary>Position im Fenster (DIP) -> physische Bildschirmkoordinate zum Zeitpunkt des Ereignisses.</summary>
        Point ToPhys(Point local) =>
            Root.ActualWidth > 0
                ? new Point(mon.X + local.X * mon.Width / Root.ActualWidth, mon.Y + local.Y * mon.Height / Root.ActualHeight)
                : session.Cursor;

        bool IsOverToolbar(Point p)
        {
            if (ToolbarHost.Content == null || !ToolbarHost.IsHitTestVisible || ToolbarHost.ActualWidth <= 0) return false;
            var r = ToolbarHost.TransformToAncestor(Root).TransformBounds(new Rect(ToolbarHost.RenderSize));
            r.Inflate(4, 4);
            return r.Contains(p);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.Handled || IsInToolbar(e.OriginalSource) || IsOverToolbar(e.GetPosition(Root))) return;
            CaptureMouse();
            session.OnMouseDown(ToPhys(e.GetPosition(Root)));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            session.OnMouseMove();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            // Nur eine Auswahl abschließen, die in diesem Fenster begonnen wurde
            if (!IsMouseCaptured) return;
            var p = ToPhys(e.GetPosition(Root));
            ReleaseMouseCapture();
            session.OnMouseUp(p);
        }
        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            if (IsMouseCaptured) ReleaseMouseCapture();
            session.OnRightClick();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!e.Handled) session.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            session.OnKeyUp(e);
        }

        public void Render()
        {
            double W = Root.ActualWidth, H = Root.ActualHeight;
            if (W <= 0 || H <= 0 || session.Frozen == null) return;
            double sx = W / mon.Width, sy = H / mon.Height;
            Point L(Point p) => new((p.X - mon.X) * sx, (p.Y - mon.Y) * sy);

            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new RectangleGeometry(new Rect(0, 0, W, H)));

            // Auswahl / Hover-Rechteck
            if (session.Selection is Rect sel && sel.Width > 0 && sel.Height > 0)
            {
                var r = new Rect(L(sel.TopLeft), L(sel.BottomRight));
                g.Children.Add(new RectangleGeometry(r));
                Place(SelBorder, r, 1);
                Place(SelGlow, r, 3);
                SelBorder.Visibility = Visibility.Visible;
                SelGlow.Visibility = Visibility.Visible;

                SizeText.Text = $"{(int)Math.Round(sel.Width)} × {(int)Math.Round(sel.Height)}";
                SizeTag.Visibility = Visibility.Visible;
                SizeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double tx = Math.Clamp(r.X, 4, Math.Max(4, W - SizeTag.DesiredSize.Width - 4));
                double ty = r.Y >= 34 ? r.Y - 30 : r.Y + 8;
                // Nicht unter der Leiste verschwinden lassen -> dann innen anzeigen
                if (ToolbarHost.Content != null && ToolbarHost.IsHitTestVisible && ToolbarHost.ActualWidth > 0)
                {
                    var bar = ToolbarHost.TransformToAncestor(Root).TransformBounds(new Rect(ToolbarHost.RenderSize));
                    if (bar.IntersectsWith(new Rect(tx, ty, SizeTag.DesiredSize.Width, SizeTag.DesiredSize.Height)))
                        ty = r.Y + 8;
                }
                Canvas.SetLeft(SizeTag, tx);
                Canvas.SetTop(SizeTag, ty);
            }
            else
            {
                SelBorder.Visibility = Visibility.Collapsed;
                SelGlow.Visibility = Visibility.Collapsed;
                SizeTag.Visibility = Visibility.Collapsed;
            }

            // Freihand-Linie
            if (session.Lasso is { Count: > 1 } lasso)
            {
                LassoLine.Points = new PointCollection(lasso.Select(L));
                LassoLine.Visibility = Visibility.Visible;
            }
            else LassoLine.Visibility = Visibility.Collapsed;

            Dim.Data = g;

            // Fadenkreuz + Lupe nur auf dem Monitor mit dem Mauszeiger
            var cur = session.Cursor;
            bool here = cur.X >= mon.Left && cur.X < mon.Right && cur.Y >= mon.Top && cur.Y < mon.Bottom;
            bool areaMode = session.Mode is CaptureMode.Rectangle or CaptureMode.Freeform;
            var c = L(cur);

            bool cross = here && areaMode && !session.Dragging;
            CrossH.Visibility = CrossV.Visibility = cross ? Visibility.Visible : Visibility.Collapsed;
            if (cross)
            {
                double px = Math.Floor(c.X) + 0.5, py = Math.Floor(c.Y) + 0.5;
                CrossH.X1 = 0; CrossH.X2 = W; CrossH.Y1 = CrossH.Y2 = py;
                CrossV.Y1 = 0; CrossV.Y2 = H; CrossV.X1 = CrossV.X2 = px;
            }

            bool loupe = here && areaMode && App.Settings.ShowMagnifier;
            Loupe.Visibility = loupe ? Visibility.Visible : Visibility.Collapsed;
            if (loupe)
            {
                double ix = Math.Floor(cur.X - session.Virtual.X), iy = Math.Floor(cur.Y - session.Virtual.Y);
                // 136 px Lupe / 13.6 Pixel = 10x; aktueller Pixel liegt bei 63..73 (Markierung im XAML)
                loupeBrush.Viewbox = new Rect(ix - 6.3, iy - 6.3, 13.6, 13.6);
                var col = session.ColorAt(cur);
                LoupeHex.Text = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
                LoupeSwatch.Background = new SolidColorBrush(col);
                LoupePos.Text = $"{(int)(cur.X - mon.X)}, {(int)(cur.Y - mon.Y)}";

                Loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lw = Loupe.DesiredSize.Width, lh = Loupe.DesiredSize.Height;
                double lx = c.X + 22, ly = c.Y + 22;
                if (lx + lw > W - 4) lx = c.X - 22 - lw;
                if (ly + lh > H - 4) ly = c.Y - 22 - lh;
                Canvas.SetLeft(Loupe, lx);
                Canvas.SetTop(Loupe, ly);
            }

            // Leiste beim Aufziehen ausblenden
            if (ToolbarHost.Content != null)
            {
                double target = session.Dragging ? 0 : 1;
                if (Math.Abs(ToolbarHost.Opacity - target) > 0.01 && ToolbarHost.Tag as double? != target)
                {
                    ToolbarHost.Tag = target;
                    ToolbarHost.IsHitTestVisible = !session.Dragging;
                    ToolbarHost.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
                }
            }
        }

        static void Place(FrameworkElement el, Rect r, double inflate)
        {
            Canvas.SetLeft(el, r.X - inflate);
            Canvas.SetTop(el, r.Y - inflate);
            el.Width = r.Width + 2 * inflate;
            el.Height = r.Height + 2 * inflate;
        }
    }
}
