using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipSnip
{
    /// <summary>
    /// Eine laufende Bildschirmaufnahme: friert den Bildschirm ein, zeigt pro Monitor ein Overlay
    /// (mit der Werkzeugleiste oben zentriert auf dem Monitor mit dem Mauszeiger) und liefert den Ausschnitt.
    /// Alle Koordinaten sind physische Bildschirm-Pixel.
    /// </summary>
    public sealed class CaptureSession
    {
        public static CaptureSession? Current { get; private set; }

        public BitmapSource Frozen { get; private set; } = null!;
        public Rect Virtual { get; private set; }
        public List<Rect> Monitors { get; private set; } = new();
        public CaptureMode Mode { get; private set; }
        public AfterCapture Action { get; private set; }
        /// <summary>true = Video aufnehmen statt Bildschirmfoto.</summary>
        public bool Video { get; private set; }

        // Zustand für das Rendern der Overlays
        public Rect? Selection { get; private set; }
        public List<Point>? Lasso { get; private set; }
        public Point Cursor { get; private set; }
        public bool Dragging { get; private set; }

        List<(Rect Bounds, IntPtr Hwnd)> windows = new();
        IntPtr hoverHwnd;
        readonly List<OverlayWindow> overlays = new();
        CaptureToolbar? toolbar;
        OverlayWindow? toolbarHost;
        readonly Window? hidden;
        Point dragStart;
        bool ended;

        CaptureSession(Window? hide)
        {
            hidden = hide;
            Mode = App.Settings.Mode;
            Action = App.Settings.Action;
            Video = App.Settings.VideoMode;
            if (Video && Mode == CaptureMode.Freeform) Mode = CaptureMode.Rectangle;
        }

        /// <summary>Session ohne echte Bildschirmaufnahme – nur für das Rendern der Doku-Bilder.</summary>
        internal static CaptureSession CreateForDocs(BitmapSource frozen, Rect monitor, CaptureMode mode,
            AfterCapture action, Rect? selection, Point cursor, bool video = false) =>
            new(null)
            {
                Video = video,
                Frozen = frozen,
                Virtual = monitor,
                Monitors = new List<Rect> { monitor },
                Mode = mode,
                Action = action,
                Selection = selection,
                Cursor = cursor
            };

        public static async void Start(Window? hide = null)
        {
            if (Current != null)
            {
                Current.toolbarHost?.Activate();
                return;
            }
            var s = new CaptureSession(hide);
            Current = s;
            if (hide != null)
            {
                hide.Hide();
                await Task.Delay(250); // Fenster-Animation abwarten
            }
            s.Begin();
        }

        void Begin()
        {
            (Frozen, Virtual) = ScreenCapture.CaptureVirtualScreen();
            Monitors = ScreenCapture.GetMonitors();
            windows = WindowFinder.GetWindows();
            Cursor = CursorPhys();
            Dragging = false;
            Selection = null;
            Lasso = null;

            Rect cursorMon = MonitorAt(Cursor) ?? ScreenCapture.PrimaryMonitor();
            foreach (var mon in Monitors)
            {
                var ir = ToImageRect(mon);
                if (ir.Width <= 0 || ir.Height <= 0) continue;
                var crop = new CroppedBitmap(Frozen, ir);
                crop.Freeze();
                var ow = new OverlayWindow(this, mon, crop);
                if (mon == cursorMon)
                {
                    toolbar = new CaptureToolbar(this);
                    ow.AttachToolbar(toolbar);
                    toolbarHost = ow;
                }
                overlays.Add(ow);
            }

            foreach (var ow in overlays) ow.Show();
            toolbarHost?.Activate();
            toolbarHost?.Focus();
            UpdateHover();
            RenderAll();
        }

        // ------------------------------------------------------------------ Hilfen

        static Point CursorPhys()
        {
            Native.GetCursorPos(out var p);
            return new Point(p.X, p.Y);
        }

        Rect? MonitorAt(Point p)
        {
            foreach (var m in Monitors)
                if (p.X >= m.Left && p.X < m.Right && p.Y >= m.Top && p.Y < m.Bottom) return m;
            return null;
        }

        Rect? WindowAt(Point p)
        {
            foreach (var (w, hwnd) in windows)
            {
                if (p.X >= w.Left && p.X < w.Right && p.Y >= w.Top && p.Y < w.Bottom)
                {
                    var r = Rect.Intersect(w, Virtual);
                    if (!r.IsEmpty && r.Width > 4 && r.Height > 4) { hoverHwnd = hwnd; return r; }
                }
            }
            hoverHwnd = IntPtr.Zero;
            return MonitorAt(p);
        }

        Int32Rect ToImageRect(Rect r)
        {
            int x0 = (int)Math.Floor(r.Left - Virtual.X), y0 = (int)Math.Floor(r.Top - Virtual.Y);
            int x1 = (int)Math.Ceiling(r.Right - Virtual.X), y1 = (int)Math.Ceiling(r.Bottom - Virtual.Y);
            x0 = Math.Clamp(x0, 0, Frozen.PixelWidth); x1 = Math.Clamp(x1, 0, Frozen.PixelWidth);
            y0 = Math.Clamp(y0, 0, Frozen.PixelHeight); y1 = Math.Clamp(y1, 0, Frozen.PixelHeight);
            return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }

        public Color ColorAt(Point p)
        {
            int x = (int)(p.X - Virtual.X), y = (int)(p.Y - Virtual.Y);
            if (x < 0 || y < 0 || x >= Frozen.PixelWidth || y >= Frozen.PixelHeight) return Colors.Black;
            var px = new byte[4];
            Frozen.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            return Color.FromRgb(px[2], px[1], px[0]);
        }

        void RenderAll()
        {
            foreach (var o in overlays) o.Render();
        }

        void UpdateHover()
        {
            if (Dragging) return;
            Selection = Mode switch
            {
                CaptureMode.Window => WindowAt(Cursor),
                CaptureMode.Fullscreen => Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Virtual : MonitorAt(Cursor),
                _ => null
            };
        }

        // ------------------------------------------------------------------ Toolbar-Aktionen

        public void SetMode(CaptureMode m)
        {
            Mode = m;
            App.Settings.Mode = m;
            Dragging = false;
            Lasso = null;
            toolbar?.Sync();
            UpdateHover();
            RenderAll();
        }

        public void SetVideo(bool video)
        {
            Video = video;
            App.Settings.VideoMode = video;
            if (video && Mode == CaptureMode.Freeform) Mode = CaptureMode.Rectangle;
            Dragging = false;
            Lasso = null;
            toolbar?.Sync();
            UpdateHover();
            RenderAll();
        }

        public void SetAction(AfterCapture a)
        {
            Action = a;
            App.Settings.Action = a;
        }

        public async void StartDelay(int seconds)
        {
            CloseOverlays();
            var cd = new CountdownWindow();
            cd.Show();
            for (int i = seconds; i > 0; i--)
            {
                cd.Set(i);
                await Task.Delay(1000);
                if (ended) { cd.Close(); return; }
            }
            cd.Close();
            await Task.Delay(220);
            if (!ended) Begin();
        }

        public void Cancel() => End();

        // ------------------------------------------------------------------ Maus / Tastatur

        /// <param name="at">Physische Position zum Zeitpunkt des Klicks (nicht erst bei der Verarbeitung).</param>
        public void OnMouseDown(Point at)
        {
            Cursor = new Point(Math.Round(at.X), Math.Round(at.Y));
            switch (Mode)
            {
                case CaptureMode.Rectangle:
                    dragStart = Cursor;
                    Dragging = true;
                    Selection = new Rect(Cursor, Cursor);
                    break;
                case CaptureMode.Freeform:
                    Dragging = true;
                    Lasso = new List<Point> { Cursor };
                    break;
                case CaptureMode.Window:
                case CaptureMode.Fullscreen:
                    UpdateHover();
                    if (Selection is Rect r) { Finish(r); return; }
                    break;
            }
            RenderAll();
        }

        public void OnMouseMove()
        {
            var c = CursorPhys();
            if (c == Cursor && !Dragging) return;
            Cursor = c;

            if (Dragging && Mode == CaptureMode.Rectangle)
            {
                Selection = new Rect(dragStart, Cursor);
            }
            else if (Dragging && Mode == CaptureMode.Freeform && Lasso != null)
            {
                var last = Lasso[^1];
                if (Math.Abs(last.X - c.X) + Math.Abs(last.Y - c.Y) >= 2) Lasso.Add(c);
            }
            else UpdateHover();

            RenderAll();
        }

        public void OnMouseUp(Point at)
        {
            if (!Dragging) return;
            Cursor = new Point(Math.Round(at.X), Math.Round(at.Y));
            Dragging = false;

            if (Mode == CaptureMode.Rectangle)
            {
                // Endpunkt direkt beim Loslassen nehmen (der letzte MouseMove kann fehlen)
                var r = new Rect(dragStart, Cursor);
                if (r.Width >= 4 && r.Height >= 4) { Finish(r); return; }
                // Einfacher Klick ohne Ziehen -> Fenster unter dem Mauszeiger
                Selection = null;
                if (WindowAt(Cursor) is Rect w) { Finish(w); return; }
            }
            else if (Mode == CaptureMode.Freeform && Lasso != null && Lasso.Count > 3)
            {
                Lasso.Add(Cursor);
                FinishLasso(Lasso);
                return;
            }

            Lasso = null;
            UpdateHover();
            RenderAll();
        }

        public void OnRightClick()
        {
            if (Dragging)
            {
                Dragging = false;
                Selection = null;
                Lasso = null;
                UpdateHover();
                RenderAll();
            }
            else Cancel();
        }

        public void OnKeyDown(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            switch (key)
            {
                case Key.Escape:
                    if (Dragging) OnRightClick(); else Cancel();
                    break;
                case Key.D1: case Key.NumPad1: SetMode(CaptureMode.Rectangle); break;
                case Key.D2: case Key.NumPad2: SetMode(CaptureMode.Window); break;
                case Key.D3: case Key.NumPad3: if (!Video) SetMode(CaptureMode.Freeform); break;
                case Key.D4: case Key.NumPad4: SetMode(CaptureMode.Fullscreen); break;
                case Key.Left: Nudge(-1, 0); break;
                case Key.Right: Nudge(1, 0); break;
                case Key.Up: Nudge(0, -1); break;
                case Key.Down: Nudge(0, 1); break;
                case Key.LeftCtrl:
                case Key.RightCtrl:
                    UpdateHover();
                    RenderAll();
                    break;
                case Key.Enter:
                    if (!Dragging && Selection is Rect r) Finish(r);
                    break;
                case Key.C:
                    if (!Dragging)
                    {
                        var col = ColorAt(Cursor);
                        string hex = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
                        ImageUtil.CopyText(hex);
                        Toast.Notify("Farbe kopiert", hex, null, null, 3);
                    }
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        public void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl)
            {
                UpdateHover();
                RenderAll();
            }
        }

        void Nudge(int dx, int dy)
        {
            var c = CursorPhys();
            Native.SetCursorPos((int)c.X + dx, (int)c.Y + dy);
            OnMouseMove();
        }

        // ------------------------------------------------------------------ Abschluss

        void Finish(Rect r)
        {
            if (Video)
            {
                var region = Rect.Intersect(r, Virtual);
                if (region.IsEmpty || region.Width < 8 || region.Height < 8) return;
                var act = Action;
                var hid = hidden;
                var hwnd = Mode == CaptureMode.Window ? hoverHwnd : IntPtr.Zero;
                End(restoreHidden: false); // das versteckte Fenster erst nach der Aufnahme zurückholen
                App.Instance.StartRecording(region, act, hid, hwnd);
                return;
            }

            var ir = ToImageRect(r);
            if (ir.Width < 1 || ir.Height < 1) return;
            var img = ImageUtil.ToBgra(new CroppedBitmap(Frozen, ir));
            var action = Action;
            End();
            App.Instance.HandleCapture(img, action);
        }

        void FinishLasso(List<Point> pts)
        {
            double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
            var ir = ToImageRect(new Rect(new Point(minX, minY), new Point(maxX + 1, maxY + 1)));
            if (ir.Width < 4 || ir.Height < 4)
            {
                Lasso = null;
                RenderAll();
                return;
            }

            var crop = new CroppedBitmap(Frozen, ir);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                Point Local(Point p) => new(p.X - Virtual.X - ir.X, p.Y - Virtual.Y - ir.Y);
                ctx.BeginFigure(Local(pts[0]), true, true);
                ctx.PolyLineTo(pts.Skip(1).Select(Local).ToList(), true, true);
            }
            geo.Freeze();

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushClip(geo);
                dc.DrawImage(crop, new Rect(0, 0, ir.Width, ir.Height));
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(ir.Width, ir.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var img = ImageUtil.ToBgra(rtb);

            var action = Action;
            End();
            App.Instance.HandleCapture(img, action);
        }

        void CloseOverlays()
        {
            foreach (var o in overlays) o.Close();
            overlays.Clear();
            toolbar = null;
            toolbarHost = null;
        }

        void End(bool restoreHidden = true)
        {
            if (ended) return;
            ended = true;
            CloseOverlays();
            Current = null;
            Frozen = null!;
            App.Settings.Save();
            if (restoreHidden && hidden != null)
            {
                hidden.Show();
                hidden.Activate();
            }
        }
    }
}
