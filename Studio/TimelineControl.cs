using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipSnip
{
    public enum TrackKind { None, Clip, Zoom, Mask, Text, Layout }

    /// <summary>
    /// Zeitleiste des Studios (Quellzeit): Clip-Spur mit Vorschaubildern, Schnitten und Tempo,
    /// Zoom-, Masken-, Text- und Layout-Spur. Segmente lassen sich auswählen, verschieben und in der Länge ändern;
    /// Doppelklick in eine leere Stelle legt ein neues Segment an.
    /// </summary>
    public sealed class TimelineControl : FrameworkElement
    {
        const double RulerH = 22, ClipH = 58, RowH = 26, Gap = 6, Pad = 12;

        StudioProject? project;
        public List<BitmapSource> Thumbs { get; } = new();
        public double Position { get; private set; }
        public TrackKind SelectedKind { get; private set; }
        public int SelectedIndex { get; private set; } = -1;
        public bool ShowLayoutRow { get; set; }

        /// <summary>Vor einer Änderung (für Rückgängig).</summary>
        public event Action? BeforeEdit;
        /// <summary>Nach einer Änderung an Segmenten.</summary>
        public event Action? Edited;
        public event Action<double>? SeekRequested;
        public event Action? SelectionChanged;

        // Ziehen
        enum DragMode { None, Seek, Move, ResizeStart, ResizeEnd }
        DragMode drag;
        double dragOffset;
        bool dragStarted;

        static readonly Brush Bg = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)));
        static readonly Brush RowBg = Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x20)));
        static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0xA8)));
        static readonly Brush ZoomFill = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0x4F, 0x8C, 0xFF)));
        static readonly Brush ZoomAuto = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0x4F, 0x8C, 0xFF)));
        static readonly Brush MaskFill = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0xE8, 0x5D, 0x75)));
        static readonly Brush TextFill = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0xE0, 0x9A, 0x2A)));
        static readonly Brush LayoutFill = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x2D, 0xBF, 0x9A)));
        static readonly Brush SpeedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xB0, 0x20)));
        static readonly Brush Removed = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x0C, 0x0C, 0x0E)));
        static readonly Pen SelPen = FrozenPen(new Pen(Brushes.White, 2));
        static readonly Pen CutPen = FrozenPen(new Pen(Brushes.White, 2.5));
        static readonly Pen TickPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1));
        static readonly Brush Hatch = CreateHatch();
        static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
        static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

        static Brush CreateHatch()
        {
            var b = new DrawingBrush(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x4D, 0x4F)), 2),
                new LineGeometry(new Point(0, 10), new Point(10, 0))))
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 10, 10),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 10, 10),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            b.Freeze();
            return b;
        }

        public TimelineControl()
        {
            Focusable = true;
            Cursor = Cursors.Arrow;
            ClipToBounds = true;
        }

        public void SetProject(StudioProject p)
        {
            project = p;
            InvalidateMeasure();
            InvalidateVisual();
        }

        public void SetPosition(double srcT)
        {
            Position = srcT;
            InvalidateVisual();
        }

        public void Select(TrackKind kind, int index)
        {
            SelectedKind = kind;
            SelectedIndex = index;
            InvalidateVisual();
            SelectionChanged?.Invoke();
        }

        double Duration => Math.Max(0.1, project?.Meta.Duration ?? 1);
        double Width0 => Math.Max(10, ActualWidth - 2 * Pad);
        double X(double t) => Pad + t / Duration * Width0;
        double T(double x) => Math.Clamp((x - Pad) / Width0 * Duration, 0, Duration);

        double ClipTop => RulerH + 2;
        double ZoomTop => ClipTop + ClipH + Gap;
        double MaskTop => ZoomTop + RowH + Gap;
        double TextTop => MaskTop + RowH + Gap;
        double LayoutTop => TextTop + RowH + Gap;

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 600 : available.Width, (ShowLayoutRow ? LayoutTop + RowH : TextTop + RowH) + 8);

        // ================================================================== Zeichnen

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (project == null) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Lineal
            double step = NiceStep(Duration / Math.Max(1, Width0 / 90));
            for (double t = 0; t <= Duration + 1e-6; t += step)
            {
                double x = X(t);
                dc.DrawLine(TickPen, new Point(x, RulerH - 6), new Point(x, RulerH));
                var ft = Text(Fmt(t), 10, Dim, dpi);
                dc.DrawText(ft, new Point(x + 3, 3));
            }

            // Clip-Spur: Vorschaubilder
            var clipRect = new Rect(Pad, ClipTop, Width0, ClipH);
            dc.PushClip(new RectangleGeometry(clipRect, 8, 8));
            dc.DrawRectangle(RowBg, null, clipRect);
            if (Thumbs.Count > 0)
            {
                double tw = Width0 / Thumbs.Count;
                for (int i = 0; i < Thumbs.Count; i++)
                {
                    var th = Thumbs[i];
                    double sh = ClipH, sw = th.PixelWidth * sh / Math.Max(1, th.PixelHeight);
                    dc.PushClip(new RectangleGeometry(new Rect(Pad + i * tw, ClipTop, tw, ClipH)));
                    dc.PushOpacity(0.8);
                    dc.DrawImage(th, new Rect(Pad + i * tw + (tw - sw) / 2, ClipTop, sw, sh));
                    dc.Pop();
                    dc.Pop();
                }
            }
            var clips = project.Edits.Clips;
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                var r = new Rect(X(c.Start), ClipTop, Math.Max(0, X(c.End) - X(c.Start)), ClipH);
                if (c.Removed)
                {
                    dc.DrawRectangle(Removed, null, r);
                    dc.DrawRectangle(Hatch, null, r);
                }
                else if (Math.Abs(c.Speed - 1) > 0.01)
                {
                    dc.DrawRectangle(SpeedFill, null, r);
                    var ft = Text($"{c.Speed:0.##}×", 12, Brushes.White, dpi);
                    if (r.Width > ft.Width + 10) dc.DrawText(ft, new Point(r.X + 6, r.Y + 4));
                }
                if (i > 0) dc.DrawLine(CutPen, new Point(r.X, ClipTop), new Point(r.X, ClipTop + ClipH));
            }
            // Klicks (weiße Punkte) und Tastenkürzel (blaue Striche)
            foreach (var t in project.Input.Clicks)
                dc.DrawEllipse(Brushes.White, null, new Point(X(t), ClipTop + ClipH - 6), 2.2, 2.2);
            foreach (var k in project.Input.Keys.Where(k => k.Text.Length > 1))
                dc.DrawRectangle(ZoomFill, null, new Rect(X(k.T) - 1, ClipTop + ClipH - 12, 2, 8));
            dc.Pop();
            if (SelectedKind == TrackKind.Clip && SelectedIndex >= 0 && SelectedIndex < clips.Count)
            {
                var c = clips[SelectedIndex];
                dc.DrawRoundedRectangle(null, SelPen, new Rect(X(c.Start) + 1, ClipTop + 1, Math.Max(2, X(c.End) - X(c.Start) - 2), ClipH - 2), 7, 7);
            }

            // Segment-Spuren
            DrawRow(dc, ZoomTop, "Zoom", project.Edits.Zooms.Select(z => (z.Start, z.End, $"{(z.Type == ZoomType.Loupe ? "Lupe " : "")}{z.Level:0.0}×", z.Auto ? ZoomAuto : ZoomFill)).ToList(), TrackKind.Zoom, dpi);
            DrawRow(dc, MaskTop, "Maske", project.Edits.Masks.Select(m => (m.Start, m.End, MaskName(m.Kind) + MotionSuffix(m.Motion), MaskFill)).ToList(), TrackKind.Mask, dpi);
            // Keyframes als kleine Rauten
            foreach (var m in project.Edits.Masks.Where(m => m.Motion == MaskMotion.Keyframes))
                foreach (var k in m.Keys.Where(k => k.T >= m.Start && k.T <= m.End))
                {
                    double kx = X(k.T), ky = MaskTop + RowH / 2;
                    var g = new StreamGeometry();
                    using (var gc = g.Open())
                    {
                        gc.BeginFigure(new Point(kx, ky - 5), true, true);
                        gc.PolyLineTo(new[] { new Point(kx + 5, ky), new Point(kx, ky + 5), new Point(kx - 5, ky) }, true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(Brushes.White, KeyPen, g);
                }
            DrawRow(dc, TextTop, "Text", project.Edits.Texts.Select(x => (x.Start, x.End, TextLabel(x), TextFill)).ToList(), TrackKind.Text, dpi);
            if (ShowLayoutRow)
                DrawRow(dc, LayoutTop, "Layout", project.Edits.Layouts.Select(l => (l.Start, l.End, LayoutName(l.Layout), LayoutFill)).ToList(), TrackKind.Layout, dpi);

            // Abspielposition
            double px = X(Position);
            dc.DrawRectangle(Brushes.White, null, new Rect(px - 1, RulerH - 4, 2, ActualHeight - RulerH + 4));
            var knob = new StreamGeometry();
            using (var g = knob.Open())
            {
                g.BeginFigure(new Point(px - 6, RulerH - 12), true, true);
                g.LineTo(new Point(px + 6, RulerH - 12), true, true);
                g.LineTo(new Point(px + 6, RulerH - 5), true, true);
                g.LineTo(new Point(px, RulerH), true, true);
                g.LineTo(new Point(px - 6, RulerH - 5), true, true);
            }
            dc.DrawGeometry(Brushes.White, null, knob);
        }

        void DrawRow(DrawingContext dc, double top, string label, List<(double S, double E, string Text, Brush Fill)> segs, TrackKind kind, double dpi)
        {
            var row = new Rect(Pad, top, Width0, RowH);
            dc.DrawRoundedRectangle(RowBg, null, row, 6, 6);
            if (segs.Count == 0)
            {
                var hint = Text($"{label}: Doppelklick zum Hinzufügen", 10.5, Dim, dpi);
                dc.DrawText(hint, new Point(Pad + 8, top + (RowH - hint.Height) / 2));
            }
            for (int i = 0; i < segs.Count; i++)
            {
                var (s, e, text, fill) = segs[i];
                var r = new Rect(X(s), top + 2, Math.Max(4, X(e) - X(s)), RowH - 4);
                dc.DrawRoundedRectangle(fill, SelectedKind == kind && SelectedIndex == i ? SelPen : null, r, 5, 5);
                var ft = Text(text, 11, Brushes.White, dpi);
                if (r.Width > ft.Width + 10)
                {
                    dc.PushClip(new RectangleGeometry(r));
                    dc.DrawText(ft, new Point(r.X + 6, r.Y + (r.Height - ft.Height) / 2));
                    dc.Pop();
                }
                // Griffe zum Ziehen
                if (SelectedKind == kind && SelectedIndex == i)
                {
                    dc.DrawRoundedRectangle(Brushes.White, null, new Rect(r.Left + 2, r.Top + 5, 3, r.Height - 10), 1.5, 1.5);
                    dc.DrawRoundedRectangle(Brushes.White, null, new Rect(r.Right - 5, r.Top + 5, 3, r.Height - 10), 1.5, 1.5);
                }
            }
        }

        static readonly Pen KeyPen = FrozenPen(new SolidColorBrush(Color.FromArgb(0xC0, 0x30, 0x10, 0x18)), 1);

        static Pen FrozenPen(Brush b, double w)
        {
            var pen = new Pen(b, w);
            pen.Freeze();
            return pen;
        }

        static string MotionSuffix(MaskMotion m) => m switch
        {
            MaskMotion.Track => "  ·  folgt",
            MaskMotion.Keyframes => "  ·  Keyframes",
            _ => ""
        };

        static string TextLabel(TextSegment s)
        {
            string first = s.Text.Replace("\r", "").Split('\n')[0];
            return first.Length > 40 ? first[..40] + "…" : first;
        }

        public static string TextStyleName(TextStyle s) => s switch
        {
            TextStyle.Title => "Titel",
            TextStyle.Subtitle => "Untertitel",
            TextStyle.Callout => "Hinweis",
            _ => "Bauchbinde"
        };

        public static string MaskName(MaskKind k) => k switch
        {
            MaskKind.Blur => "Weichzeichnen",
            MaskKind.Pixelate => "Verpixeln",
            MaskKind.Fill => "Abdecken",
            _ => "Hervorheben"
        };

        public static string LayoutName(CamLayout l) => l switch
        {
            CamLayout.ScreenOnly => "Nur Bildschirm",
            CamLayout.Overlay => "Kamera-Overlay",
            CamLayout.Split => "Geteilt",
            _ => "Nur Kamera"
        };

        static FormattedText Text(string s, double size, Brush b, double dpi) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, b, dpi);

        static string Fmt(double t) => t >= 3600 ? TimeSpan.FromSeconds(t).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(t).ToString(@"m\:ss");

        static double NiceStep(double raw)
        {
            foreach (var s in new[] { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
                if (s >= raw) return s;
            return 1200;
        }

        // ================================================================== Maus

        (TrackKind Kind, int Index, bool StartEdge, bool EndEdge) HitTest(Point p)
        {
            if (project == null) return (TrackKind.None, -1, false, false);
            TrackKind kind = p.Y >= ZoomTop && p.Y < ZoomTop + RowH ? TrackKind.Zoom
                : p.Y >= MaskTop && p.Y < MaskTop + RowH ? TrackKind.Mask
                : p.Y >= TextTop && p.Y < TextTop + RowH ? TrackKind.Text
                : ShowLayoutRow && p.Y >= LayoutTop && p.Y < LayoutTop + RowH ? TrackKind.Layout
                : p.Y >= ClipTop && p.Y < ClipTop + ClipH ? TrackKind.Clip : TrackKind.None;
            var segs = Segments(kind);
            for (int i = segs.Count - 1; i >= 0; i--)
            {
                double x0 = X(segs[i].Start), x1 = X(segs[i].End);
                if (p.X >= x0 - 4 && p.X <= x1 + 4)
                    return (kind, i, kind != TrackKind.Clip && Math.Abs(p.X - x0) <= 6, kind != TrackKind.Clip && Math.Abs(p.X - x1) <= 6);
            }
            return (kind, -1, false, false);
        }

        List<(double Start, double End)> Segments(TrackKind kind) => kind switch
        {
            TrackKind.Clip => project!.Edits.Clips.Select(c => (c.Start, c.End)).ToList(),
            TrackKind.Zoom => project!.Edits.Zooms.Select(c => (c.Start, c.End)).ToList(),
            TrackKind.Mask => project!.Edits.Masks.Select(c => (c.Start, c.End)).ToList(),
            TrackKind.Layout => project!.Edits.Layouts.Select(c => (c.Start, c.End)).ToList(),
            TrackKind.Text => project!.Edits.Texts.Select(c => (c.Start, c.End)).ToList(),
            _ => new List<(double, double)>()
        };

        void SetSegment(TrackKind kind, int i, double s, double e)
        {
            switch (kind)
            {
                case TrackKind.Zoom: project!.Edits.Zooms[i].Start = s; project.Edits.Zooms[i].End = e; project.Edits.Zooms[i].Auto = false; break;
                case TrackKind.Mask: project!.Edits.Masks[i].Start = s; project.Edits.Masks[i].End = e; break;
                case TrackKind.Layout: project!.Edits.Layouts[i].Start = s; project.Edits.Layouts[i].End = e; break;
                case TrackKind.Text: project!.Edits.Texts[i].Start = s; project.Edits.Texts[i].End = e; break;
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (project == null) return;
            Focus();
            var p = e.GetPosition(this);
            var hit = HitTest(p);

            if (e.ClickCount == 2 && hit.Kind is TrackKind.Zoom or TrackKind.Mask or TrackKind.Text or TrackKind.Layout && hit.Index < 0)
            {
                AddSegment(hit.Kind, T(p.X));
                e.Handled = true;
                return;
            }

            if (hit.Kind is TrackKind.Zoom or TrackKind.Mask or TrackKind.Text or TrackKind.Layout && hit.Index >= 0)
            {
                Select(hit.Kind, hit.Index);
                var seg = Segments(hit.Kind)[hit.Index];
                drag = hit.StartEdge ? DragMode.ResizeStart : hit.EndEdge ? DragMode.ResizeEnd : DragMode.Move;
                dragOffset = T(p.X) - seg.Start;
                dragStarted = false;
                CaptureMouse();
                SeekRequested?.Invoke(Math.Clamp(T(p.X), seg.Start, seg.End));
            }
            else
            {
                drag = DragMode.Seek;
                CaptureMouse();
                double t = T(p.X);
                if (hit.Kind == TrackKind.Clip && hit.Index >= 0) Select(TrackKind.Clip, hit.Index);
                else if (hit.Kind != TrackKind.None) Select(TrackKind.None, -1);
                SeekRequested?.Invoke(t);
            }
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (project == null) return;
            var p = e.GetPosition(this);
            if (drag == DragMode.None)
            {
                var hit = HitTest(p);
                Cursor = hit.StartEdge || hit.EndEdge ? Cursors.SizeWE : hit.Index >= 0 && hit.Kind != TrackKind.Clip ? Cursors.Hand : Cursors.Arrow;
                return;
            }
            double t = T(p.X);
            if (drag == DragMode.Seek) { SeekRequested?.Invoke(t); return; }

            if (!dragStarted) { BeforeEdit?.Invoke(); dragStarted = true; }
            var segs = Segments(SelectedKind);
            if (SelectedIndex < 0 || SelectedIndex >= segs.Count) return;
            var (s, en) = segs[SelectedIndex];
            const double Min = 0.3;
            switch (drag)
            {
                case DragMode.Move:
                {
                    double len = en - s;
                    double ns = Math.Clamp(t - dragOffset, 0, Duration - len);
                    SetSegment(SelectedKind, SelectedIndex, ns, ns + len);
                    break;
                }
                case DragMode.ResizeStart:
                    SetSegment(SelectedKind, SelectedIndex, Math.Clamp(t, 0, en - Min), en);
                    break;
                case DragMode.ResizeEnd:
                    SetSegment(SelectedKind, SelectedIndex, s, Math.Clamp(t, s + Min, Duration));
                    break;
            }
            InvalidateVisual();
            Edited?.Invoke();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (drag != DragMode.None)
            {
                drag = DragMode.None;
                ReleaseMouseCapture();
            }
        }

        public void AddSegment(TrackKind kind, double t)
        {
            if (project == null) return;
            BeforeEdit?.Invoke();
            double len = Math.Min(3, Duration);
            double s = Math.Clamp(t - 0.3, 0, Math.Max(0, Duration - len)), en = Math.Min(Duration, s + len);
            int index;
            switch (kind)
            {
                case TrackKind.Zoom:
                    project.Edits.Zooms.Add(new ZoomSegment { Start = s, End = en, Level = project.Edits.Style.ZoomLevel, FollowCursor = project.Input.HasCursor });
                    project.Edits.Zooms.Sort((a, b) => a.Start.CompareTo(b.Start));
                    index = project.Edits.Zooms.FindIndex(z => z.Start == s);
                    break;
                case TrackKind.Mask:
                    project.Edits.Masks.Add(new MaskSegment { Start = s, End = en });
                    index = project.Edits.Masks.Count - 1;
                    break;
                case TrackKind.Text:
                    project.Edits.Texts.Add(new TextSegment { Start = s, End = en });
                    index = project.Edits.Texts.Count - 1;
                    break;
                case TrackKind.Layout:
                    project.Edits.Layouts.Add(new LayoutSegment { Start = s, End = en });
                    index = project.Edits.Layouts.Count - 1;
                    break;
                default:
                    return;
            }
            Select(kind, index);
            InvalidateVisual();
            Edited?.Invoke();
        }

        public void DeleteSelected()
        {
            if (project == null || SelectedIndex < 0) return;
            switch (SelectedKind)
            {
                case TrackKind.Zoom when SelectedIndex < project.Edits.Zooms.Count:
                    BeforeEdit?.Invoke(); project.Edits.Zooms.RemoveAt(SelectedIndex); break;
                case TrackKind.Mask when SelectedIndex < project.Edits.Masks.Count:
                    BeforeEdit?.Invoke(); project.Edits.Masks.RemoveAt(SelectedIndex); break;
                case TrackKind.Text when SelectedIndex < project.Edits.Texts.Count:
                    BeforeEdit?.Invoke(); project.Edits.Texts.RemoveAt(SelectedIndex); break;
                case TrackKind.Layout when SelectedIndex < project.Edits.Layouts.Count:
                    BeforeEdit?.Invoke(); project.Edits.Layouts.RemoveAt(SelectedIndex); break;
                case TrackKind.Clip when SelectedIndex < project.Edits.Clips.Count:
                {
                    var c = project.Edits.Clips[SelectedIndex];
                    if (!c.Removed && project.Edits.Clips.Count(x => !x.Removed) <= 1) return;
                    BeforeEdit?.Invoke();
                    c.Removed = !c.Removed;
                    InvalidateVisual();
                    Edited?.Invoke();
                    return;
                }
                default: return;
            }
            Select(TrackKind.None, -1);
            Edited?.Invoke();
        }
    }
}
