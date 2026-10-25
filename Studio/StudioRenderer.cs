using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace SnipSnip
{
    /// <summary>
    /// Setzt ein Ausgabebild zusammen: Hintergrund, Rahmen mit Schatten, Kamerafahrt (Zoom), Aufnahme,
    /// Masken, Lupe, Cursor mit Klick-Effekten, Webcam und Tasten-Einblendung.
    /// Vorschau und Export nutzen exakt denselben Code.
    /// </summary>
    public sealed class StudioRenderer : IDisposable
    {
        readonly StudioProject p;
        readonly Dictionary<int, SKImage> cursorImages = new();
        double cursorBase = 1;
        float[] micEnv = Array.Empty<float>();   // 10 Hz, 1 = Sprache
        float[] camSide = Array.Empty<float>();  // 10 Hz, 0 = links, 1 = rechts
        float[] camAlpha = Array.Empty<float>(); // 10 Hz

        SKImage? bgCache;
        string bgKey = "";
        SKImage? shadowCache;
        string shadowKey = "";
        SKSurface? accSurface, tmpSurface;

        public CursorPath Cursor { get; private set; } = null!;
        public CameraPath Camera { get; private set; } = null!;

        static readonly SKSamplingOptions Hq = new(SKCubicResampler.Mitchell);
        static readonly SKSamplingOptions Lin = new(SKFilterMode.Linear, SKMipmapMode.None);

        public StudioRenderer(StudioProject project)
        {
            p = project;
            LoadCursorImages();
            LoadMicEnvelope();
            Rebuild();
        }

        StudioStyle St => p.Edits.Style;

        /// <summary>Nach Änderungen an Zooms, Glättung oder Webcam-Optionen neu berechnen.</summary>
        public void Rebuild()
        {
            Cursor = new CursorPath(p.Input, St.CursorSmoothing, p.Meta.Duration);
            Camera = new CameraPath(p, Cursor);
            BuildCamTracks();
        }

        public void Dispose()
        {
            foreach (var i in cursorImages.Values) i.Dispose();
            bgCache?.Dispose();
            shadowCache?.Dispose();
            accSurface?.Dispose();
            tmpSurface?.Dispose();
        }

        // ================================================================== Vorbereitung

        void LoadCursorImages()
        {
            foreach (var s in p.Input.Shapes)
            {
                string f = Path.Combine(p.Dir, s.File);
                if (!File.Exists(f)) continue;
                var img = SKImage.FromEncodedData(f);
                if (img == null) continue;
                cursorImages[s.Id] = img;
                if (s.Kind == "arrow") cursorBase = Math.Max(0.6, img.Height / 20.0);
            }
        }

        void LoadMicEnvelope()
        {
            if (p.MicAudio == null || !File.Exists(p.MicAudio)) return;
            try
            {
                using var wr = new NAudio.Wave.WaveFileReader(p.MicAudio);
                var r = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(wr);
                int block = r.WaveFormat.SampleRate * r.WaveFormat.Channels / 10;
                var buf = new float[block];
                var env = new List<float>();
                int n;
                while ((n = r.Read(buf, 0, block)) > 0)
                {
                    double sum = 0;
                    for (int i = 0; i < n; i++) sum += buf[i] * buf[i];
                    double db = 10 * Math.Log10(sum / Math.Max(1, n) + 1e-12);
                    env.Add(db > -42 ? 1 : 0);
                }
                micEnv = env.ToArray();
            }
            catch { micEnv = Array.Empty<float>(); }
        }

        /// <summary>Webcam: Ecke weg vom Cursor (smart) und Ausblenden bei Stille – vorab mit 10 Hz.</summary>
        void BuildCamTracks()
        {
            int n = (int)(p.Meta.Duration * 10) + 2;
            camSide = new float[n];
            camAlpha = new float[n];
            bool rightPref = St.CamCorner is CamCorner.BottomRight or CamCorner.TopRight;
            bool bottom = St.CamCorner is CamCorner.BottomRight or CamCorner.BottomLeft;
            bool right = rightPref;
            double lastSwitch = -10, side = right ? 1 : 0, alpha = 1;
            for (int i = 0; i < n; i++)
            {
                double t = i / 10.0;
                if (St.CamSmartPosition && p.Input.HasCursor && t - lastSwitch > 1.5)
                {
                    var c = Cursor.At(t);
                    double rx = c.X / Math.Max(1, p.Meta.Width), ry = c.Y / Math.Max(1, p.Meta.Height);
                    bool inRow = bottom ? ry > 0.62 : ry < 0.38;
                    if (inRow && right && rx > 0.68) { right = false; lastSwitch = t; }
                    else if (inRow && !right && rx < 0.32) { right = true; lastSwitch = t; }
                    else if (right != rightPref && !(inRow && (rightPref ? rx > 0.6 : rx < 0.4)) && t - lastSwitch > 3) { right = rightPref; lastSwitch = t; }
                }
                // im ersten Bild direkt an der richtigen Stelle beginnen, danach weich gleiten
                side = i == 0 ? (right ? 1 : 0) : side + ((right ? 1 : 0) - side) * 0.28;
                camSide[i] = (float)side;

                double target = 1;
                if (St.CamHideOnSilence && micEnv.Length > 0)
                {
                    bool speech = false;
                    for (int k = Math.Max(0, i - 15); k <= Math.Min(micEnv.Length - 1, i + 3) && !speech; k++) speech = micEnv[k] > 0;
                    target = speech ? 1 : 0;
                }
                alpha += (target - alpha) * 0.3;
                camAlpha[i] = (float)alpha;
            }
        }

        static float Sample(float[] a, double t, float fallback)
        {
            if (a.Length == 0) return fallback;
            double f = Math.Clamp(t * 10, 0, a.Length - 1.001);
            int i = (int)f;
            return (float)(a[i] + (a[i + 1] - a[i]) * (f - i));
        }

        // ================================================================== Geometrie

        public double CropAspect =>
            p.Meta.Width * p.Edits.CropW / Math.Max(1, p.Meta.Height * p.Edits.CropH);

        public double OutputAspect => St.Aspect switch
        {
            "16:9" => 16.0 / 9,
            "1:1" => 1,
            "9:16" => 9.0 / 16,
            "4:3" => 4.0 / 3,
            "4:5" => 0.8,
            _ => CropAspect
        };

        /// <summary>Ausgabegröße: shortSide = kürzere Kante (z. B. 1080).</summary>
        public (int W, int H) OutputSize(int shortSide)
        {
            double a = OutputAspect;
            int w, h;
            if (a >= 1) { h = shortSide; w = (int)Math.Round(h * a); }
            else { w = shortSide; h = (int)Math.Round(w / a); }
            return (w & ~1, h & ~1);
        }

        readonly struct Layout
        {
            public readonly SKRect Content, Cam;
            public readonly float ContentAlpha, CamAlpha, CamRadius;
            public Layout(SKRect content, SKRect cam, float ca, float wa, float r) { Content = content; Cam = cam; ContentAlpha = ca; CamAlpha = wa; CamRadius = r; }

            public static Layout Lerp(Layout a, Layout b, float f) => new(
                LerpRect(a.Content, b.Content, f), LerpRect(a.Cam, b.Cam, f),
                a.ContentAlpha + (b.ContentAlpha - a.ContentAlpha) * f,
                a.CamAlpha + (b.CamAlpha - a.CamAlpha) * f,
                a.CamRadius + (b.CamRadius - a.CamRadius) * f);
        }

        static SKRect LerpRect(SKRect a, SKRect b, float f) => new(
            a.Left + (b.Left - a.Left) * f, a.Top + (b.Top - a.Top) * f,
            a.Right + (b.Right - a.Right) * f, a.Bottom + (b.Bottom - a.Bottom) * f);

        static SKRect Fit(SKRect area, double aspect)
        {
            float w = area.Width, h = (float)(w / aspect);
            if (h > area.Height) { h = area.Height; w = (float)(h * aspect); }
            return SKRect.Create(area.MidX - w / 2, area.MidY - h / 2, w, h);
        }

        Layout LayoutFor(CamLayout l, int W, int H, double t, double zoom, bool hasCam)
        {
            float m = Math.Min(W, H);
            float pad = (float)(St.Padding / 100 * m);
            var inner = new SKRect(pad, pad, W - pad, H - pad);
            if (inner.Width < 10 || inner.Height < 10) inner = new SKRect(0, 0, W, H);
            float radius = (float)(St.Radius / 1000 * m * 2);
            if (!hasCam) l = CamLayout.ScreenOnly;

            switch (l)
            {
                case CamLayout.Split:
                {
                    float gap = Math.Max(pad * 0.5f, m * 0.02f);
                    bool wide = W >= H;
                    var screenArea = wide ? new SKRect(inner.Left, inner.Top, inner.Left + inner.Width * 0.68f, inner.Bottom)
                                          : new SKRect(inner.Left, inner.Top, inner.Right, inner.Top + inner.Height * 0.62f);
                    var camArea = wide ? new SKRect(screenArea.Right + gap, inner.Top, inner.Right, inner.Bottom)
                                       : new SKRect(inner.Left, screenArea.Bottom + gap, inner.Right, inner.Bottom);
                    return new Layout(Fit(screenArea, CropAspect), camArea, 1, 1, radius);
                }
                case CamLayout.CameraOnly:
                    return new Layout(Fit(inner, CropAspect), inner, 0, 1, radius);
                case CamLayout.Overlay:
                {
                    var content = Fit(inner, CropAspect);
                    float size = (float)(St.CamSize / 100 * m);
                    if (St.CamShrinkOnZoom) size *= (float)(1 - 0.3 * Math.Clamp(zoom - 1, 0, 1));
                    float margin = Math.Max(m * 0.025f, pad * 0.5f);
                    float side = Sample(camSide, t, 1);
                    bool bottom = St.CamCorner is CamCorner.BottomRight or CamCorner.BottomLeft;
                    float x = margin + (W - 2 * margin - size) * side;
                    float y = bottom ? H - margin - size : margin;
                    float camR = St.CamShape switch { CamShape.Circle => size / 2, CamShape.Rounded => size * 0.22f, _ => size * 0.04f };
                    return new Layout(content, SKRect.Create(x, y, size, size), 1, Sample(camAlpha, t, 1), camR);
                }
                default:
                    return new Layout(Fit(inner, CropAspect), SKRect.Empty, 1, 0, radius);
            }
        }

        Layout CurrentLayout(int W, int H, double t, double zoom, bool hasCam)
        {
            const double Tr = 0.45;
            CamLayout At(double tt) => p.Edits.Layouts.FirstOrDefault(s => tt >= s.Start && tt < s.End)?.Layout ?? St.Layout;
            var now = At(t);
            var cur = LayoutFor(now, W, H, t, zoom, hasCam);
            // Übergänge an Segmentgrenzen weich überblenden
            foreach (var s in p.Edits.Layouts)
            {
                foreach (double edge in new[] { s.Start, s.End })
                {
                    double d = t - edge;
                    if (d >= 0 && d < Tr)
                    {
                        var before = LayoutFor(At(edge - 0.001), W, H, t, zoom, hasCam);
                        float f = (float)Smooth(d / Tr);
                        return Layout.Lerp(before, cur, f);
                    }
                }
            }
            return cur;
        }

        static double Smooth(double x)
        {
            x = Math.Clamp(x, 0, 1);
            return x * x * (3 - 2 * x);
        }

        /// <summary>Bildfläche der Aufnahme innerhalb des Rahmens (abzüglich Inset).</summary>
        SKRect InnerOf(SKRect content)
        {
            float inset = (float)(St.Inset / 100 * Math.Min(content.Width, content.Height));
            return SKRect.Inflate(content, -inset, -inset);
        }

        SKRect SourceCrop => SKRect.Create((float)(p.Edits.CropX * p.Meta.Width), (float)(p.Edits.CropY * p.Meta.Height),
            (float)(p.Edits.CropW * p.Meta.Width), (float)(p.Edits.CropH * p.Meta.Height));

        SKPoint ToScene(SKRect inner, double x, double y)
        {
            var crop = SourceCrop;
            return new SKPoint(inner.Left + (float)((x - crop.Left) / crop.Width * inner.Width),
                               inner.Top + (float)((y - crop.Top) / crop.Height * inner.Height));
        }

        static float ClampAxis(float f, float half, float cMin, float cMax, float canvasMax)
        {
            float lo = half, hi = canvasMax - half;
            float clo = cMin + half, chi = cMax - half;
            if (clo <= chi) { lo = Math.Max(lo, clo); hi = Math.Min(hi, chi); }
            if (lo > hi) return (lo + hi) / 2;
            return Math.Clamp(f, lo, hi);
        }

        /// <summary>Kamera-Transformation (Szene → Ausgabe) zur Quellzeit t.</summary>
        SKMatrix CameraMatrix(int W, int H, SKRect inner, double t)
        {
            var (z, fx, fy) = Camera.At(t);
            if (z <= 1.0005) return SKMatrix.Identity;
            var f = ToScene(inner, fx, fy);
            float hw = (float)(W / (2 * z)), hh = (float)(H / (2 * z));
            // etwas innerhalb der Aufnahme bleiben, damit gezoomt keine abgerundeten Ecken/Hintergrund sichtbar werden
            float rad = (float)(St.Radius / 1000 * Math.Min(W, H) * 2) * 0.6f;
            float cx = ClampAxis(f.X, hw, inner.Left + rad, inner.Right - rad, W);
            float cy = ClampAxis(f.Y, hh, inner.Top + rad, inner.Bottom - rad, H);
            var m = SKMatrix.CreateTranslation(-cx, -cy);
            m = m.PostConcat(SKMatrix.CreateScale((float)z, (float)z));
            m = m.PostConcat(SKMatrix.CreateTranslation(W / 2f, H / 2f));
            return m;
        }

        // ================================================================== Abbildungen für die Oberfläche

        SKMatrix SceneMatrix(int W, int H, double t, bool hasCam, out SKRect inner)
        {
            var (z, _, _) = Camera.At(t);
            var layout = CurrentLayout(W, H, t, z, hasCam);
            inner = InnerOf(layout.Content);
            return CameraMatrix(W, H, inner, t);
        }

        /// <summary>Relative Aufnahme-Koordinate (0..1) → Pixel im Ausgabebild zur Quellzeit t.</summary>
        public SKPoint SourceRelToCanvas(double rx, double ry, int W, int H, double t, bool hasCam)
        {
            var m = SceneMatrix(W, H, t, hasCam, out var inner);
            return m.MapPoint(ToScene(inner, rx * p.Meta.Width, ry * p.Meta.Height));
        }

        /// <summary>Pixel im Ausgabebild → relative Aufnahme-Koordinate (0..1).</summary>
        public (double X, double Y) CanvasToSourceRel(SKPoint pt, int W, int H, double t, bool hasCam)
        {
            var m = SceneMatrix(W, H, t, hasCam, out var inner);
            if (!m.TryInvert(out var inv)) inv = SKMatrix.Identity;
            var sp = inv.MapPoint(pt);
            var crop = SourceCrop;
            double x = crop.Left + (sp.X - inner.Left) / inner.Width * crop.Width;
            double y = crop.Top + (sp.Y - inner.Top) / inner.Height * crop.Height;
            return (x / Math.Max(1, p.Meta.Width), y / Math.Max(1, p.Meta.Height));
        }

        /// <summary>Diagnose-Ausgabe für --render-frame.</summary>
        public string DebugInfo(int W, int H, double t)
        {
            var (z, fx, fy) = Camera.At(t);
            var l = CurrentLayout(W, H, t, z, p.HasWebcam);
            var c = Cursor.At(t);
            return $"t={t} W={W} H={H} zoom={z:0.00} focus=({fx:0},{fy:0}) cursor=({c.X:0},{c.Y:0})\n" +
                   $"content={l.Content} cam={l.Cam} camAlpha={l.CamAlpha:0.00} camSide={Sample(camSide, t, -1):0.00}\n" +
                   $"camSide[0..5]={string.Join(",", camSide.Take(6).Select(v => v.ToString("0.00")))} n={camSide.Length}";
        }

        // ================================================================== Rendern

        /// <param name="t">Quellzeit (Sekunden der Aufnahme)</param>
        /// <param name="outT">Zeit im fertigen Video (für Cursor-Loop)</param>
        /// <param name="export">true = höchste Qualität (Bewegungsunschärfe mit mehr Teilbildern)</param>
        public void Render(SKCanvas c, int W, int H, double t, double outT, SKImage? screen, SKImage? cam, bool export, bool previewBlur = false)
        {
            bool hasCam = cam != null;
            var (zoom, _, _) = Camera.At(t);
            var layout = CurrentLayout(W, H, t, zoom, hasCam);

            int samples = 1;
            if (St.MotionBlur && St.MotionBlurAmount > 0.01 && (export || previewBlur))
            {
                double motion = Camera.MotionAt(t);
                if (motion > 0.15) samples = export ? 6 : 3;
            }

            if (samples == 1)
            {
                DrawScene(c, W, H, layout, t, outT, screen);
            }
            else
            {
                EnsureSurfaces(W, H);
                var acc = accSurface!.Canvas;
                var tmp = tmpSurface!.Canvas;
                double span = St.MotionBlurAmount * 0.03; // Belichtungszeit in Sekunden (dezent, wie eine 180°-Blende)
                for (int i = 0; i < samples; i++)
                {
                    double ts = t + span * (i / (double)(samples - 1) - 0.5);
                    tmp.Clear(SKColors.Transparent);
                    DrawScene(tmp, W, H, layout, ts, outT, screen, fast: true);
                    using var snap = tmpSurface.Snapshot();
                    using var pa = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 / (i + 1))) };
                    if (i == 0) acc.Clear(SKColors.Transparent);
                    acc.DrawImage(snap, 0, 0, Lin, i == 0 ? null : pa);
                }
                using var result = accSurface.Snapshot();
                c.DrawImage(result, 0, 0);
            }

            if (hasCam && layout.CamAlpha > 0.01 && !layout.Cam.IsEmpty) DrawWebcam(c, layout, cam!);
            if (St.ShowKeys) DrawKeys(c, W, H, t);
        }

        void EnsureSurfaces(int W, int H)
        {
            if (accSurface != null && accSurface.Canvas.DeviceClipBounds.Width == W && accSurface.Canvas.DeviceClipBounds.Height == H) return;
            accSurface?.Dispose();
            tmpSurface?.Dispose();
            var info = new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            accSurface = SKSurface.Create(info);
            tmpSurface = SKSurface.Create(info);
        }

        SKImage Background(int W, int H)
        {
            string key = $"{W}x{H}|{St.Background}|{St.Preset}|{St.SolidColor}|{St.ImagePath}|{St.BackgroundBlur}";
            if (bgCache == null || key != bgKey)
            {
                bgCache?.Dispose();
                bgCache = Backgrounds.Render(St, W, H);
                bgKey = key;
            }
            return bgCache;
        }

        SKImage Shadow(SKRect content, float radius)
        {
            string key = $"{content}|{radius}|{St.ShadowBlur}|{St.ShadowDistance}|{St.ShadowAngle}|{St.ShadowOpacity}";
            if (shadowCache != null && key == shadowKey) return shadowCache;
            shadowCache?.Dispose();
            float blur = (float)St.ShadowBlur * Math.Max(content.Width, content.Height) / 1400f;
            float pad = blur * 3 + (float)St.ShadowDistance * 2 + 4;
            var info = new SKImageInfo((int)(content.Width + pad * 2), (int)(content.Height + pad * 2), SKColorType.Bgra8888, SKAlphaType.Premul);
            using var s = SKSurface.Create(info);
            using var pa = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.Black.WithAlpha((byte)(Math.Clamp(St.ShadowOpacity, 0, 1) * 255)),
                MaskFilter = blur > 0.5f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur) : null
            };
            s.Canvas.DrawRoundRect(SKRect.Create(pad, pad, content.Width, content.Height), radius, radius, pa);
            shadowCache = s.Snapshot();
            shadowKey = key;
            return shadowCache;
        }

        void DrawScene(SKCanvas c, int W, int H, Layout layout, double t, double outT, SKImage? screen, bool fast = false)
        {
            var inner = InnerOf(layout.Content);
            c.Save();
            c.Concat(CameraMatrix(W, H, inner, t));

            c.DrawImage(Background(W, H), 0, 0);

            if (layout.ContentAlpha > 0.01)
            {
                var content = layout.Content;
                float radius = (float)(St.Radius / 1000 * Math.Min(W, H) * 2);
                byte alpha = (byte)(255 * layout.ContentAlpha);

                // Schatten
                if (St.ShadowOpacity > 0.01)
                {
                    var sh = Shadow(content, radius);
                    double a = St.ShadowAngle * Math.PI / 180;
                    float dx = (float)(Math.Cos(a) * St.ShadowDistance), dy = (float)(Math.Sin(a) * St.ShadowDistance);
                    float pad = (sh.Width - content.Width) / 2;
                    using var spa = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
                    c.DrawImage(sh, content.Left - pad + dx, content.Top - pad + dy, Lin, spa);
                }

                c.Save();
                var rr = new SKRoundRect(content, radius, radius);
                c.ClipRoundRect(rr, SKClipOperation.Intersect, true);
                if (St.Inset > 0.01)
                {
                    using var frame = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(28 * layout.ContentAlpha)) };
                    c.DrawRect(content, frame);
                    float ir = Math.Max(0, radius - (content.Width - inner.Width) / 2);
                    c.ClipRoundRect(new SKRoundRect(inner, ir, ir), SKClipOperation.Intersect, true);
                }
                if (screen != null)
                {
                    float s = screen.Width / (float)Math.Max(1, p.Meta.Width);
                    var crop = SourceCrop;
                    var src = SKRect.Create(crop.Left * s, crop.Top * s, crop.Width * s, crop.Height * s);
                    using var ipa = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
                    c.DrawImage(screen, src, inner, fast ? Lin : Hq, ipa);
                    DrawMasks(c, inner, t, screen, src);
                    DrawLoupe(c, inner, t, screen, src);
                }
                else
                {
                    using var ph = new SKPaint { Color = new SKColor(20, 20, 24) };
                    c.DrawRect(inner, ph);
                }
                DrawCursor(c, inner, t, outT, layout.ContentAlpha);
                c.Restore();
            }
            c.Restore();
        }

        // ================================================================== Masken & Hervorheben

        SKRect MaskRect(SKRect inner, MaskSegment m)
        {
            var a = ToScene(inner, m.X * p.Meta.Width, m.Y * p.Meta.Height);
            var b = ToScene(inner, (m.X + m.W) * p.Meta.Width, (m.Y + m.H) * p.Meta.Height);
            return new SKRect(a.X, a.Y, b.X, b.Y);
        }

        void DrawMasks(SKCanvas c, SKRect inner, double t, SKImage screen, SKRect src)
        {
            foreach (var m in p.Edits.Masks)
            {
                if (t < m.Start || t >= m.End) continue;
                var r = MaskRect(inner, m);
                if (r.Width < 2 || r.Height < 2) continue;
                // sanft ein-/ausblenden
                float fade = (float)Math.Min(Smooth((t - m.Start) / 0.25), Smooth((m.End - t) / 0.25));
                float sx = src.Width / inner.Width;
                var srcSub = SKRect.Create(src.Left + (r.Left - inner.Left) * sx, src.Top + (r.Top - inner.Top) * sx, r.Width * sx, r.Height * sx);

                switch (m.Kind)
                {
                    case MaskKind.Highlight:
                    {
                        c.Save();
                        float hr = Math.Min(r.Width, r.Height) * 0.08f;
                        c.ClipRoundRect(new SKRoundRect(r, hr, hr), SKClipOperation.Difference, true);
                        using var dim = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(150 * fade)) };
                        c.DrawRect(inner, dim);
                        c.Restore();
                        break;
                    }
                    case MaskKind.Blur:
                    {
                        c.Save();
                        c.ClipRoundRect(new SKRoundRect(r, 6, 6), SKClipOperation.Intersect, true);
                        float sigma = Math.Max(6, Math.Min(r.Width, r.Height) * 0.18f);
                        var big = SKRect.Inflate(r, sigma * 3, sigma * 3);
                        var bigSrc = SKRect.Inflate(srcSub, sigma * 3 * sx, sigma * 3 * sx);
                        using var bp = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma), Color = SKColors.White.WithAlpha((byte)(255 * fade)) };
                        c.DrawImage(screen, bigSrc, big, Lin, bp);
                        c.Restore();
                        break;
                    }
                    case MaskKind.Pixelate:
                    {
                        int bw = Math.Max(2, (int)(r.Width / 14)), bh = Math.Max(2, (int)(r.Height / 14));
                        using var small = SKSurface.Create(new SKImageInfo(bw, bh, SKColorType.Bgra8888, SKAlphaType.Premul));
                        small.Canvas.DrawImage(screen, srcSub, SKRect.Create(0, 0, bw, bh), Lin);
                        using var si = small.Snapshot();
                        using var pp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * fade)) };
                        c.DrawImage(si, SKRect.Create(0, 0, bw, bh), r, new SKSamplingOptions(SKFilterMode.Nearest), pp);
                        break;
                    }
                    case MaskKind.Fill:
                    {
                        // mit der Umgebungsfarbe füllen, damit die Maske mit der Oberfläche verschmilzt
                        var col = BorderColor(screen, srcSub);
                        using var fp = new SKPaint { Color = col.WithAlpha((byte)(255 * fade)), IsAntialias = true };
                        c.DrawRoundRect(r, 4, 4, fp);
                        break;
                    }
                }
            }
        }

        static SKColor BorderColor(SKImage img, SKRect r)
        {
            using var pm = img.PeekPixels();
            if (pm == null) return SKColors.Gray;
            long R = 0, G = 0, B = 0;
            int n = 0;
            for (int i = 0; i <= 20; i++)
            {
                float f = i / 20f;
                foreach (var (x, y) in new[] { (r.Left + r.Width * f, r.Top - 2), (r.Left + r.Width * f, r.Bottom + 2), (r.Left - 2, r.Top + r.Height * f), (r.Right + 2, r.Top + r.Height * f) })
                {
                    int px = Math.Clamp((int)x, 0, img.Width - 1), py = Math.Clamp((int)y, 0, img.Height - 1);
                    var col = pm.GetPixelColor(px, py);
                    R += col.Red; G += col.Green; B += col.Blue; n++;
                }
            }
            return new SKColor((byte)(R / n), (byte)(G / n), (byte)(B / n));
        }

        // ================================================================== Lupe

        void DrawLoupe(SKCanvas c, SKRect inner, double t, SKImage screen, SKRect src)
        {
            var (amount, level) = Camera.LoupeAt(t);
            if (amount < 0.02 || !p.Input.HasCursor) return;
            var cur = Cursor.At(t);
            var center = ToScene(inner, cur.X, cur.Y);
            float radius = Math.Min(inner.Width, inner.Height) * 0.17f * (float)amount;
            float sx = src.Width / inner.Width;
            float lv = (float)Math.Max(1.2, level);
            var srcCenter = new SKPoint(src.Left + (center.X - inner.Left) * sx, src.Top + (center.Y - inner.Top) * sx);
            float sr = radius * sx / lv;
            var lsrc = new SKRect(srcCenter.X - sr, srcCenter.Y - sr, srcCenter.X + sr, srcCenter.Y + sr);
            var dst = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);

            using (var sh = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(110 * amount)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, radius * 0.15f), IsAntialias = true })
                c.DrawCircle(center.X, center.Y + radius * 0.06f, radius, sh);
            c.Save();
            using (var path = new SKPath())
            {
                path.AddCircle(center.X, center.Y, radius);
                c.ClipPath(path, SKClipOperation.Intersect, true);
            }
            c.DrawImage(screen, lsrc, dst, Hq);
            c.Restore();
            using var ring = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, radius * 0.035f), Color = SKColors.White.WithAlpha((byte)(230 * amount)), IsAntialias = true };
            c.DrawCircle(center.X, center.Y, radius, ring);
        }

        // ================================================================== Cursor

        static readonly Lazy<SKPath> ArrowPath = new(() => SKPath.ParseSvgPathData("M0,0 L0,17 L4.2,13.2 L7,19.6 L9.6,18.5 L6.9,12.3 L12.4,12.3 Z"));
        static readonly Lazy<SKPath> IBeamPath = new(() => SKPath.ParseSvgPathData(
            "M-3.2,-9 L-1.4,-9 Q0,-9 0,-7.6 Q0,-9 1.4,-9 L3.2,-9 L3.2,-7.6 L1.6,-7.6 Q0.7,-7.6 0.7,-6.6 L0.7,6.6 Q0.7,7.6 1.6,7.6 L3.2,7.6 L3.2,9 L1.4,9 Q0,9 0,7.6 Q0,9 -1.4,9 L-3.2,9 L-3.2,7.6 L-1.6,7.6 Q-0.7,7.6 -0.7,6.6 L-0.7,-6.6 Q-0.7,-7.6 -1.6,-7.6 L-3.2,-7.6 Z"));
        static readonly Lazy<SKPath> HandPath = new(BuildHand);

        static SKPath BuildHand()
        {
            SKPath R(float x, float y, float w, float h, float r)
            {
                var pth = new SKPath();
                pth.AddRoundRect(SKRect.Create(x, y, w, h), r, r);
                return pth;
            }
            var hand = R(4.6f, 0, 3.4f, 12, 1.7f);
            foreach (var part in new[] { R(7.6f, 5.4f, 3.2f, 7.5f, 1.6f), R(10.4f, 6.4f, 3.1f, 7, 1.55f), R(13.1f, 7.6f, 2.9f, 6.2f, 1.45f), R(3.4f, 9.6f, 12.6f, 10.4f, 3.6f) })
                hand = hand.Op(part, SKPathOp.Union) ?? hand;
            var thumb = SKPath.ParseSvgPathData("M4.8,11.2 L2.2,8.6 Q0.6,7.4 0.2,8.9 Q0,9.8 1,11.2 L4.6,16.6 Z");
            hand = hand.Op(thumb, SKPathOp.Union) ?? hand;
            return hand;
        }

        void DrawCursor(SKCanvas c, SKRect inner, double t, double outT, float layerAlpha)
        {
            if (!St.ShowCursor || !p.Input.HasCursor) return;
            var raw = p.Input.RawAt(t);
            if (raw.Shape < 0) return;

            var pos = Cursor.At(t);
            if (St.LoopCursor)
            {
                double total = p.OutputDuration;
                if (outT > total - 1.2)
                {
                    var start = Cursor.At(0.05);
                    double f = Smooth((outT - (total - 1.2)) / 1.2);
                    pos = (pos.X + (start.X - pos.X) * f, pos.Y + (start.Y - pos.Y) * f);
                }
            }

            // im Ruhezustand ausblenden (Klicks zählen als Aktivität)
            float alpha = layerAlpha;
            if (St.HideIdleCursor)
            {
                double last = p.Input.LastMoveBefore(t);
                foreach (var ct in p.Input.Clicks) if (ct <= t && ct > last) last = ct;
                double idle = t - last;
                if (idle > St.IdleSeconds) alpha *= (float)Math.Max(0, 1 - (idle - St.IdleSeconds) / 0.35);
            }
            if (alpha < 0.01) return;

            var crop = SourceCrop;
            float scale = inner.Width / crop.Width; // Szene-Pixel pro Aufnahme-Pixel
            float unit = (float)(scale * cursorBase * St.CursorSize);

            // Klick-Effekte
            foreach (var ct in p.Input.Clicks)
            {
                double d = t - ct;
                if (d < 0 || d > 0.6) continue;
                var cp = p.Input.RawAt(ct); // echter Klickpunkt (dort hat die Oberfläche reagiert)
                var at = ToScene(inner, cp.X, cp.Y);
                float prog = (float)(d / 0.6);
                switch (St.ClickEffect)
                {
                    case ClickEffect.Circle:
                    {
                        using var ring = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = 2.2f * unit,
                            IsAntialias = true,
                            Color = SKColors.White.WithAlpha((byte)(220 * (1 - prog) * alpha))
                        };
                        c.DrawCircle(at.X, at.Y, (5 + 16 * (float)Smooth(prog * 1.4)) * unit, ring);
                        break;
                    }
                    case ClickEffect.Ripple:
                    {
                        using var fill = new SKPaint
                        {
                            IsAntialias = true,
                            Color = SKColors.White.WithAlpha((byte)(50 * (1 - prog) * (1 - prog) * alpha))
                        };
                        c.DrawCircle(at.X, at.Y, (4 + 22 * (float)Smooth(prog * 1.2)) * unit, fill);
                        using var ring = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = 1.2f * unit,
                            IsAntialias = true,
                            Color = SKColors.White.WithAlpha((byte)(150 * (1 - prog) * alpha))
                        };
                        c.DrawCircle(at.X, at.Y, (4 + 22 * (float)Smooth(prog * 1.2)) * unit, ring);
                        break;
                    }
                }
            }

            // "Drücken": kurzes Verkleinern beim Klick
            float press = 1;
            foreach (var ct in p.Input.Clicks)
            {
                double d = t - ct;
                if (d < 0 || d > 0.35) continue;
                double k = d < 0.06 ? d / 0.06 : 1 - (d - 0.06) / 0.29;
                press = Math.Min(press, 1 - 0.2f * (float)Math.Clamp(k, 0, 1));
            }

            var shape = p.Input.Shapes.FirstOrDefault(s => s.Id == raw.Shape);
            var scenePos = ToScene(inner, pos.X, pos.Y);

            // Bewegungsunschärfe des Cursors: Nachzieh-Spur bei schnellen Bewegungen
            if (St.MotionBlur)
            {
                var prev = Cursor.At(t - 1.0 / 45);
                var pp = ToScene(inner, prev.X, prev.Y);
                float dist = SKPoint.Distance(pp, scenePos);
                if (dist > 18 * unit)
                {
                    for (int i = 1; i <= 4; i++)
                    {
                        float f = i / 5f;
                        var gp = new SKPoint(pp.X + (scenePos.X - pp.X) * f, pp.Y + (scenePos.Y - pp.Y) * f);
                        DrawCursorShape(c, shape, raw.Shape, gp, unit * press, alpha * 0.16f * f * (float)St.MotionBlurAmount * 2);
                    }
                }
            }
            DrawCursorShape(c, shape, raw.Shape, scenePos, unit * press, alpha);
        }

        void DrawCursorShape(SKCanvas c, CursorShape? shape, int id, SKPoint at, float unit, float alpha)
        {
            string kind = shape?.Kind ?? "arrow";
            if (St.HighResCursor && kind is "arrow" or "hand" or "ibeam" or "appstarting")
            {
                var path = kind switch { "hand" => HandPath.Value, "ibeam" => IBeamPath.Value, _ => ArrowPath.Value };
                var hot = kind switch { "hand" => new SKPoint(6.3f, 0.4f), "ibeam" => new SKPoint(0, 0), _ => new SKPoint(0, 0) };
                c.Save();
                c.Translate(at.X, at.Y);
                c.Scale(unit);
                c.Translate(-hot.X, -hot.Y);
                using (var sh = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(70 * alpha)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.9f), IsAntialias = true })
                {
                    c.Save();
                    c.Translate(0.4f, 0.9f);
                    c.DrawPath(path, sh);
                    c.Restore();
                }
                bool ibeam = kind == "ibeam";
                using var fill = new SKPaint { Color = (ibeam ? SKColors.Black : SKColors.White).WithAlpha((byte)(255 * alpha)), IsAntialias = true };
                using var stroke = new SKPaint
                {
                    Color = (ibeam ? SKColors.White : SKColors.Black).WithAlpha((byte)(255 * alpha)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = ibeam ? 0.7f : 1.05f,
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true
                };
                c.DrawPath(path, stroke);
                c.DrawPath(path, fill);
                c.Restore();
                return;
            }

            if (cursorImages.TryGetValue(id, out var img))
            {
                float s = (float)(unit / cursorBase);
                var dst = SKRect.Create(at.X - (shape?.HotX ?? 0) * s, at.Y - (shape?.HotY ?? 0) * s, img.Width * s, img.Height * s);
                using var pa = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * alpha)) };
                c.DrawImage(img, dst, Hq, pa);
            }
        }

        // ================================================================== Webcam

        void DrawWebcam(SKCanvas c, Layout layout, SKImage cam)
        {
            var r = layout.Cam;
            float alpha = layout.CamAlpha;
            float radius = layout.CamRadius;
            if (r.Width < 4 || r.Height < 4) return;

            using (var sh = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(120 * alpha)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, r.Width * 0.05f), IsAntialias = true })
                c.DrawRoundRect(SKRect.Inflate(r, -2, -2).Standardized, radius, radius, sh);

            c.Save();
            c.ClipRoundRect(new SKRoundRect(r, radius, radius), SKClipOperation.Intersect, true);

            // "cover": Kamera füllt die Fläche, mittig beschnitten
            float s = Math.Max(r.Width / cam.Width, r.Height / cam.Height);
            float sw = r.Width / s, shh = r.Height / s;
            var src = SKRect.Create((cam.Width - sw) / 2, (cam.Height - shh) / 2, sw, shh);
            using var pa = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * alpha)), ColorFilter = CamColorFilter() };
            if (St.CamSharpen)
                pa.ImageFilter = SKImageFilter.CreateMatrixConvolution(new SKSizeI(3, 3),
                    new float[] { 0, -0.5f, 0, -0.5f, 3, -0.5f, 0, -0.5f, 0 }, 1, 0, new SKPointI(1, 1), SKShaderTileMode.Clamp, false);
            if (St.CamMirror)
            {
                c.Translate(r.MidX, 0);
                c.Scale(-1, 1);
                c.Translate(-r.MidX, 0);
            }
            c.DrawImage(cam, src, r, Hq, pa);
            c.Restore();

            using var border = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, r.Width * 0.008f), Color = SKColors.White.WithAlpha((byte)(60 * alpha)), IsAntialias = true };
            c.DrawRoundRect(r, radius, radius, border);
        }

        SKColorFilter? CamColorFilter()
        {
            float b = (float)St.CamBrightness, ct = (float)St.CamContrast, sa = (float)St.CamSaturation, w = (float)St.CamWarmth;
            if (Math.Abs(b) + Math.Abs(ct) + Math.Abs(sa) + Math.Abs(w) < 0.001) return null;
            float con = 1 + ct, sat = 1 + sa;
            float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            float sr = (1 - sat) * lr, sg = (1 - sat) * lg, sb = (1 - sat) * lb;
            float off = (b * 0.4f + 0.5f * (1 - con)) * 255;
            return SKColorFilter.CreateColorMatrix(new[]
            {
                con * (sr + sat), con * sg, con * sb, 0, off + w * 18,
                con * sr, con * (sg + sat), con * sb, 0, off + w * 4,
                con * sr, con * sg, con * (sb + sat), 0, off - w * 18,
                0, 0, 0, 1, 0
            });
        }

        // ================================================================== Tasten

        void DrawKeys(SKCanvas c, int W, int H, double t)
        {
            (double T, string Text)? cur = null;
            foreach (var k in p.Input.Keys)
            {
                if (k.T > t) break;
                if (k.Text.Length > 0 && t - k.T < 1.4) cur = k;
            }
            if (cur == null) return;
            double age = t - cur.Value.T;
            float a = (float)Math.Min(Smooth(age / 0.12), Smooth((1.4 - age) / 0.3));
            float pop = 0.9f + 0.1f * (float)Smooth(age / 0.15);

            float m = Math.Min(W, H);
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), m * 0.032f);
            string text = cur.Value.Text;
            float tw = font.MeasureText(text);
            float ph = m * 0.07f, pw = tw + m * 0.06f;
            var rect = SKRect.Create(W / 2f - pw / 2, H - m * 0.06f - ph, pw, ph);
            c.Save();
            c.Translate(rect.MidX, rect.MidY);
            c.Scale(pop);
            c.Translate(-rect.MidX, -rect.MidY);
            using (var bg = new SKPaint { Color = new SKColor(18, 18, 22, (byte)(225 * a)), IsAntialias = true })
                c.DrawRoundRect(rect, ph / 2, ph / 2, bg);
            using (var bd = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(40 * a)), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true })
                c.DrawRoundRect(rect, ph / 2, ph / 2, bd);
            using var tp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a)), IsAntialias = true };
            c.DrawText(text, rect.MidX, rect.MidY + font.Size * 0.36f, SKTextAlign.Center, font, tp);
            c.Restore();
        }
    }
}
