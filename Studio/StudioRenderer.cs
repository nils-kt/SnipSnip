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
    public sealed partial class StudioRenderer : IDisposable
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
            DisposeEffects();
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

        void LoadMicEnvelope() => micEnv = p.Speech;

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
            _ => ContentAspect
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
                    return new Layout(Fit(screenArea, ContentAspect), camArea, 1, 1, radius);
                }
                case CamLayout.CameraOnly:
                    return new Layout(Fit(inner, ContentAspect), inner, 0, 1, radius);
                case CamLayout.Overlay:
                {
                    var content = Fit(inner, ContentAspect);
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
                    return new Layout(Fit(inner, ContentAspect), SKRect.Empty, 1, 0, radius);
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
            float bar = ChromeBar(content);
            var area = new SKRect(content.Left, content.Top + bar, content.Right, content.Bottom);
            float inset = (float)(St.Inset / 100 * Math.Min(area.Width, area.Height));
            return SKRect.Inflate(area, -inset, -inset);
        }

        SKRect SourceCrop => SKRect.Create((float)(p.Edits.CropX * p.Meta.Width), (float)(p.Edits.CropY * p.Meta.Height),
            (float)(p.Edits.CropW * p.Meta.Width), (float)(p.Edits.CropH * p.Meta.Height));

        SKPoint ToScene(SKRect inner, double x, double y)
        {
            var crop = SourceCrop;
            return new SKPoint(inner.Left + (float)((x - crop.Left) / crop.Width * inner.Width),
                               inner.Top + (float)((y - crop.Top) / crop.Height * inner.Height));
        }

        /// <summary>
        /// Begrenzt den Kamera-Mittelpunkt stetig: Ist die Ansicht schmaler als die Aufnahme, bleibt sie innerhalb der
        /// Aufnahme; wird sie breiter, wandert sie stufenlos zur Aufnahme-Mitte (früher ein harter Regelwechsel → Ruck).
        /// Zum Schluss immer innerhalb der Ausgabe bleiben (bei Zoom 1 exakt mittig).
        /// </summary>
        static float ClampAxis(float f, float half, float cMin, float cMax, float canvasMax)
        {
            float center = (cMin + cMax) / 2;
            float lo = Math.Min(cMin + half, center), hi = Math.Max(cMax - half, center);
            float v = Math.Clamp(f, lo, hi);
            return Math.Clamp(v, half, Math.Max(half, canvasMax - half));
        }

        /// <summary>Kamera-Transformation (Szene → Ausgabe) zur Quellzeit t.</summary>
        SKMatrix CameraMatrix(int W, int H, SKRect inner, double t)
        {
            var (z, cx, cy) = CameraView(W, H, inner, t);
            return z <= 1.0005f ? SKMatrix.Identity : ViewMatrix(W, H, z, cx, cy);
        }

        static SKMatrix ViewMatrix(int W, int H, float z, float cx, float cy) =>
            SKMatrix.CreateTranslation(-cx, -cy).PostConcat(SKMatrix.CreateScale(z, z)).PostConcat(SKMatrix.CreateTranslation(W / 2f, H / 2f));

        /// <summary>Hintergrund: folgt der Kamera abgeschwächt (Parallaxe) – wirkt räumlicher.</summary>
        SKMatrix BackgroundMatrix(int W, int H, SKRect inner, double t)
        {
            var (z, cx, cy) = CameraView(W, H, inner, t);
            if (z <= 1.0005f) return SKMatrix.Identity;
            float k = 1 - (float)Math.Clamp(St.Parallax, 0, 1);
            float zb = 1 + (z - 1) * k;
            float hw = W / (2 * zb), hh = H / (2 * zb);
            float bx = Math.Clamp(W / 2f + (cx - W / 2f) * k, hw, W - hw);
            float by = Math.Clamp(H / 2f + (cy - H / 2f) * k, hh, H - hh);
            return ViewMatrix(W, H, zb, bx, by);
        }

        /// <summary>Zoomstufe und Mittelpunkt der Ansicht (Szenen-Koordinaten) zur Quellzeit t.</summary>
        (float Z, float Cx, float Cy) CameraView(int W, int H, SKRect inner, double t)
        {
            var (z, fx, fy) = Camera.At(t);
            if (z <= 1.0005) return (1, W / 2f, H / 2f);
            var f = ToScene(inner, fx, fy);
            float hw = (float)(W / (2 * z)), hh = (float)(H / (2 * z));
            // etwas innerhalb der Aufnahme bleiben, damit gezoomt keine abgerundeten Ecken/Hintergrund sichtbar werden
            float rad = (float)(St.Radius / 1000 * Math.Min(W, H) * 2) * 0.6f;
            float cx = ClampAxis(f.X, hw, inner.Left + rad, inner.Right - rad, W);
            float cy = ClampAxis(f.Y, hh, inner.Top + rad, inner.Bottom - rad, H);
            return ((float)z, cx, cy);
        }

        // ================================================================== Abbildungen für die Oberfläche

        SKMatrix SceneMatrix(int W, int H, double t, bool hasCam, out SKRect inner)
        {
            var (z, _, _) = Camera.At(t);
            var layout = CurrentLayout(W, H, t, z, hasCam);
            inner = InnerOf(layout.Content);
            return CameraMatrix(W, H, inner, t).PreConcat(TiltMatrix(layout.Content));
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

        /// <summary>Diagnose: Kamerafahrt Bild für Bild (Zoom, Mittelpunkt, Teilbilder der Unschärfe).</summary>
        public string CameraTrace(int W, int H, double from, double to, double fps)
        {
            var sb = new System.Text.StringBuilder("t;zoom;cx;cy;motion;samples;blur\n");
            for (double t = from; t <= to; t += 1 / fps)
            {
                var (z, _, _) = Camera.At(t);
                var l = CurrentLayout(W, H, t, z, p.HasWebcam);
                var m = CameraMatrix(W, H, InnerOf(l.Content), t);
                // Mittelpunkt der Ansicht in Szenen-Koordinaten
                m.TryInvert(out var inv);
                var ctr = inv.MapPoint(new SKPoint(W / 2f, H / 2f));
                double motion = SmoothedMotion(t);
                int samples = St.MotionBlur && BlurWeight(motion) > 0.001 ? 6 : 1;
                sb.AppendLine(FormattableString.Invariant($"{t:0.000};{z:0.0000};{ctr.X:0.0};{ctr.Y:0.0};{motion:0.000};{samples};{BlurWeight(motion):0.000}"));
            }
            return sb.ToString();
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

            // Unschärfe wächst stetig mit der Bewegung – kein hartes An/Aus (das wirkte wie ein Neu-Rendern)
            int samples = 1;
            double blurWeight = 0;
            if (St.MotionBlur && St.MotionBlurAmount > 0.01 && (export || previewBlur))
            {
                blurWeight = BlurWeight(SmoothedMotion(t));
                if (blurWeight > 0.001) samples = export ? 6 : 3;
            }

            // Schnitt-Übergang „Unschärfe“: alles in eine weichgezeichnete Ebene
            float cutK = CutStrength(outT);
            bool blurLayer = St.CutTransition == CutTransition.Blur && cutK > 0.01f;
            if (blurLayer)
            {
                float sigma = cutK * Math.Min(W, H) * 0.025f;
                using var lp = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) };
                c.SaveLayer(lp);
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
                double span = St.MotionBlurAmount * 0.03 * blurWeight; // Belichtungszeit in Sekunden (dezent, wie eine 180°-Blende)
                // schnelle Abtastung nur, wenn die Bewegung den Schärfeunterschied ohnehin verdeckt
                bool fast = blurWeight > 0.5;
                for (int i = 0; i < samples; i++)
                {
                    double ts = t + span * (i / (double)(samples - 1) - 0.5);
                    tmp.Clear(SKColors.Transparent);
                    DrawScene(tmp, W, H, layout, ts, outT, screen, fast);
                    using var snap = tmpSurface.Snapshot();
                    using var pa = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 / (i + 1))) };
                    if (i == 0) acc.Clear(SKColors.Transparent);
                    acc.DrawImage(snap, 0, 0, Lin, i == 0 ? null : pa);
                }
                using var result = accSurface.Snapshot();
                c.DrawImage(result, 0, 0);
            }

            DrawVignette(c, W, H);
            DrawGrain(c, W, H, outT);
            if (hasCam && layout.CamAlpha > 0.01 && !layout.Cam.IsEmpty) DrawWebcam(c, layout, cam!);
            if (St.ShowKeys) DrawKeys(c, W, H, t);
            DrawTexts(c, W, H, t);
            DrawWatermark(c, W, H);
            DrawProgress(c, W, H, outT);
            if (blurLayer) c.Restore();
            DrawFades(c, W, H, outT, cutK);
        }

        /// <summary>0 bei Stillstand, weich ansteigend bis 1 bei schneller Kamerafahrt.</summary>
        static double BlurWeight(double motion) => Smooth((motion - 0.03) / 0.45);

        /// <summary>Bewegung über ±0,12 s gemittelt (Dreiecksfenster), damit die Unschärfe weich ein- und ausblendet.</summary>
        double SmoothedMotion(double t)
        {
            double sum = 0, wsum = 0;
            for (int i = -4; i <= 4; i++)
            {
                double w = 5 - Math.Abs(i);
                sum += Camera.MotionAt(t + i * 0.03) * w;
                wsum += w;
            }
            return sum / wsum;
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
            c.Concat(BackgroundMatrix(W, H, inner, t));
            c.DrawImage(Background(W, H), 0, 0);
            c.Restore();

            c.Save();
            c.Concat(CameraMatrix(W, H, inner, t));
            c.Concat(TiltMatrix(layout.Content));

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
                DrawChrome(c, content, layout.ContentAlpha);
                if (screen != null)
                {
                    float s = screen.Width / (float)Math.Max(1, p.Meta.Width);
                    var crop = SourceCrop;
                    var src = SKRect.Create(crop.Left * s, crop.Top * s, crop.Width * s, crop.Height * s);
                    using var ipa = new SKPaint { Color = SKColors.White.WithAlpha(alpha), ColorFilter = ScreenColorFilter() };
                    c.DrawImage(screen, src, inner, fast ? Lin : Hq, ipa);
                    DrawMasks(c, inner, t, screen, src);
                    DrawLoupe(c, inner, t, screen, src);
                    DrawSpotlight(c, inner, t);
                    cursorBgLum = BackgroundLuminance(screen, t, outT);
                }
                else
                {
                    using var ph = new SKPaint { Color = new SKColor(20, 20, 24) };
                    c.DrawRect(inner, ph);
                }
                DrawCursor(c, inner, t, outT, layout.ContentAlpha);
                c.Restore();
                DrawBorder(c, content, radius, layout.ContentAlpha);
            }
            c.Restore();
        }

        // ================================================================== Masken & Hervorheben

        SKRect MaskRect(SKRect inner, MaskSegment m, double t)
        {
            var (x, y, w, h) = m.RectAt(t);
            var a = ToScene(inner, x * p.Meta.Width, y * p.Meta.Height);
            var b = ToScene(inner, (x + w) * p.Meta.Width, (y + h) * p.Meta.Height);
            return new SKRect(a.X, a.Y, b.X, b.Y);
        }

        void DrawMasks(SKCanvas c, SKRect inner, double t, SKImage screen, SKRect src)
        {
            foreach (var m in p.Edits.Masks)
            {
                if (t < m.Start || t >= m.End) continue;
                // Verfolgte Masken gehören zu einem Einzelbild der Aufnahme – auf dessen Zeit runden,
                // sonst läuft die Maske bei 60 fps aus einer 30-fps-Aufnahme jedes zweite Bild ein halbes Bild voraus
                // (bei Gleichstand das frühere Bild – so wählt es auch der FrameDecoder)
                double tm = m.Motion == MaskMotion.Track && p.Meta.Fps > 0 ? Math.Ceiling(t * p.Meta.Fps - 0.5 - 1e-6) / p.Meta.Fps : t;
                var r = MaskRect(inner, m, tm);
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
                        // mitbewegt: am inneren Rand abtasten – der gehört zum verfolgten Inhalt, die Farbe bleibt stabil
                        var col = BorderColor(screen, srcSub, m.Motion == MaskMotion.Track ? -6 : 2);
                        using var fp = new SKPaint { Color = col.WithAlpha((byte)(255 * fade)), IsAntialias = true };
                        c.DrawRoundRect(r, 4, 4, fp);
                        break;
                    }
                }
            }
        }

        /// <param name="d">Abstand vom Rand: positiv = außerhalb, negativ = innerhalb des Rechtecks.</param>
        static SKColor BorderColor(SKImage img, SKRect r, float d)
        {
            using var pm = img.PeekPixels();
            if (pm == null) return SKColors.Gray;
            long R = 0, G = 0, B = 0;
            int n = 0;
            for (int i = 0; i <= 20; i++)
            {
                float f = i / 20f;
                foreach (var (x, y) in new[] { (r.Left + r.Width * f, r.Top - d), (r.Left + r.Width * f, r.Bottom + d), (r.Left - d, r.Top + r.Height * f), (r.Right + d, r.Top + r.Height * f) })
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
        static readonly Lazy<SKPath> IBeamPath = new(BuildIBeam);

        /// <summary>Textcursor aus einem Stück: kräftiger Strich mit geraden Querbalken.</summary>
        static SKPath BuildIBeam()
        {
            var stem = new SKPath();
            stem.AddRect(SKRect.Create(-0.85f, -8.6f, 1.7f, 17.2f));
            var top = new SKPath();
            top.AddRoundRect(SKRect.Create(-3.6f, -9.5f, 7.2f, 1.7f), 0.6f, 0.6f);
            var bottom = new SKPath();
            bottom.AddRoundRect(SKRect.Create(-3.6f, 7.8f, 7.2f, 1.7f), 0.6f, 0.6f);
            var p = stem.Op(top, SKPathOp.Union) ?? stem;
            return p.Op(bottom, SKPathOp.Union) ?? p;
        }
        static readonly Lazy<SKPath> HandPath = new(BuildHand);
        static readonly Lazy<SKPath> ModernArrowPath = new(() => SKPath.ParseSvgPathData(
            "M1,1.6 L1,18.6 Q1,19.9 2.1,19.2 L6.4,15.5 L9.1,21.6 Q9.5,22.5 10.4,22.1 L12.4,21.2 Q13.3,20.8 12.9,19.9 L10.3,14 L15.9,13.8 Q17.2,13.7 16.3,12.8 L2.6,0.5 Q1,-0.6 1,1.6 Z"));

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

        /// <summary>Sichtbarkeit des Cursors (Ausblenden im Ruhezustand; Klicks zählen als Aktivität).</summary>
        float CursorVisibility(double t)
        {
            if (!St.HideIdleCursor) return 1;
            double last = p.Input.LastMoveBefore(t);
            foreach (var ct in p.Input.Clicks) if (ct <= t && ct > last) last = ct;
            double idle = t - last;
            return idle > St.IdleSeconds ? (float)Math.Max(0, 1 - (idle - St.IdleSeconds) / 0.35) : 1;
        }

        (double X, double Y) CursorPos(double t, double outT)
        {
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
            return pos;
        }

        /// <summary>Helligkeit (0..1) der Aufnahme an der Cursorposition – der Textcursor passt seine Farbe daran an.</summary>
        float cursorBgLum = 0.15f;

        float BackgroundLuminance(SKImage screen, double t, double outT)
        {
            if (!p.Input.HasCursor) return cursorBgLum;
            using var pm = screen.PeekPixels();
            if (pm == null) return cursorBgLum;
            var pos = CursorPos(t, outT);
            float s = screen.Width / (float)Math.Max(1, p.Meta.Width);
            double sum = 0;
            int n = 0;
            for (int dy = -6; dy <= 6; dy += 3)
                for (int dx = -6; dx <= 6; dx += 3)
                {
                    int x = (int)((pos.X + dx) * s), y = (int)((pos.Y + dy) * s);
                    if (x < 0 || y < 0 || x >= pm.Width || y >= pm.Height) continue;
                    var col = pm.GetPixelColor(x, y);
                    sum += (0.2126 * col.Red + 0.7152 * col.Green + 0.0722 * col.Blue) / 255;
                    n++;
                }
            return n > 0 ? (float)(sum / n) : cursorBgLum;
        }

        void DrawCursor(SKCanvas c, SKRect inner, double t, double outT, float layerAlpha)
        {
            if (!St.ShowCursor || !p.Input.HasCursor) return;
            var raw = p.Input.RawAt(t);
            if (raw.Shape < 0) return;

            var pos = CursorPos(t, outT);
            float alpha = layerAlpha * CursorVisibility(t);

            var crop = SourceCrop;
            float scale = inner.Width / crop.Width; // Szene-Pixel pro Aufnahme-Pixel
            float unit = (float)(scale * cursorBase * St.CursorSize);
            var clickCol = Backgrounds.Parse(St.ClickColor);

            // Klick-Effekte (auch wenn der Cursor gerade ausgeblendet ist)
            double life = St.ClickEffect == ClickEffect.Focus ? 0.9 : 0.6;
            foreach (var ct in p.Input.Clicks)
            {
                double d = t - ct;
                if (d < 0 || d > life) continue;
                var cp = p.Input.RawAt(ct); // echter Klickpunkt (dort hat die Oberfläche reagiert)
                var at = ToScene(inner, cp.X, cp.Y);
                float prog = (float)(d / life);
                DrawClickEffect(c, inner, at, prog, unit, layerAlpha, clickCol);
            }
            if (alpha < 0.01) return;

            // "Drücken": kurzes Verkleinern beim Klick
            float press = 1;
            foreach (var ct in p.Input.Clicks)
            {
                double d = t - ct;
                if (d < 0 || d > 0.35) continue;
                double k = d < 0.06 ? d / 0.06 : 1 - (d - 0.06) / 0.29;
                press = Math.Min(press, 1 - (St.CursorStyle == CursorStyle.Touch ? 0.3f : 0.2f) * (float)Math.Clamp(k, 0, 1));
            }

            var shape = p.Input.Shapes.FirstOrDefault(s => s.Id == raw.Shape);
            var scenePos = ToScene(inner, pos.X, pos.Y);

            // Neigung in Bewegungsrichtung (wie ein mitgezogener Zeiger)
            float angle = 0;
            if (St.CursorTilt)
            {
                var before = Cursor.At(t - 1.0 / 30);
                double vx = (pos.X - before.X) * 30; // Aufnahme-Pixel pro Sekunde
                angle = (float)Math.Clamp(vx / 1600 * 16, -16, 16);
            }

            // Leuchtkreis – um die optische Mitte der Form, nicht um die Spitze (sonst hängt er beim Pfeil daneben)
            if (St.CursorHighlight)
            {
                var hc = Backgrounds.Parse(St.CursorColor);
                float hr = 24 * unit;
                string kind = shape?.Kind ?? "arrow";
                var off = St.CursorStyle is CursorStyle.Dot or CursorStyle.Ring or CursorStyle.Touch || kind == "ibeam"
                    ? SKPoint.Empty
                    : kind == "hand" ? new SKPoint(2f * unit, 9f * unit) : new SKPoint(5f * unit, 8.5f * unit);
                if (Math.Abs(angle) > 0.05f)
                {
                    double ra = angle * Math.PI / 180;
                    off = new SKPoint((float)(off.X * Math.Cos(ra) - off.Y * Math.Sin(ra)), (float)(off.X * Math.Sin(ra) + off.Y * Math.Cos(ra)));
                }
                var center = scenePos + off;
                using var hp = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateRadialGradient(center, hr,
                        new[] { hc.WithAlpha((byte)(120 * alpha)), hc.WithAlpha((byte)(60 * alpha)), hc.WithAlpha(0) },
                        new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp)
                };
                c.DrawCircle(center, hr, hp);
            }

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
                        DrawCursorShape(c, shape, raw.Shape, gp, unit * press, alpha * 0.16f * f * (float)St.MotionBlurAmount * 2, angle);
                    }
                }
            }
            DrawCursorShape(c, shape, raw.Shape, scenePos, unit * press, alpha, angle);
        }

        void DrawClickEffect(SKCanvas c, SKRect inner, SKPoint at, float prog, float unit, float alpha, SKColor col)
        {
            switch (St.ClickEffect)
            {
                case ClickEffect.Circle:
                {
                    using var ring = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 2.2f * unit,
                        IsAntialias = true,
                        Color = col.WithAlpha((byte)(220 * (1 - prog) * alpha))
                    };
                    c.DrawCircle(at.X, at.Y, (5 + 16 * (float)Smooth(prog * 1.4)) * unit, ring);
                    break;
                }
                case ClickEffect.Ripple:
                {
                    float r = (4 + 22 * (float)Smooth(prog * 1.2)) * unit;
                    using var fill = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(50 * (1 - prog) * (1 - prog) * alpha)) };
                    c.DrawCircle(at.X, at.Y, r, fill);
                    using var ring = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1.2f * unit,
                        IsAntialias = true,
                        Color = col.WithAlpha((byte)(150 * (1 - prog) * alpha))
                    };
                    c.DrawCircle(at.X, at.Y, r, ring);
                    break;
                }
                case ClickEffect.Pulse:
                {
                    // gefüllter Punkt, der kurz aufpoppt und verblasst
                    float grow = (float)Smooth(prog * 2.2);
                    using var fill = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(150 * (1 - prog) * alpha)) };
                    c.DrawCircle(at.X, at.Y, (3 + 11 * grow) * unit, fill);
                    using var core = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(230 * Math.Max(0, 1 - prog * 2.5f) * alpha)) };
                    c.DrawCircle(at.X, at.Y, 3.2f * unit, core);
                    break;
                }
                case ClickEffect.Burst:
                {
                    using var ray = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 2f * unit,
                        StrokeCap = SKStrokeCap.Round,
                        IsAntialias = true,
                        Color = col.WithAlpha((byte)(235 * (1 - prog) * alpha))
                    };
                    float r1 = (7 + 12 * (float)Smooth(prog * 1.3)) * unit, r2 = (11 + 20 * (float)Smooth(prog)) * unit;
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4 + Math.PI / 8;
                        float cx = (float)Math.Cos(a), sy = (float)Math.Sin(a);
                        c.DrawLine(at.X + cx * r1, at.Y + sy * r1, at.X + cx * r2, at.Y + sy * r2, ray);
                    }
                    break;
                }
                case ClickEffect.Focus:
                {
                    // kurz alles außer der Klickstelle abdunkeln
                    float env = (float)Math.Min(Smooth(prog / 0.15), Smooth((1 - prog) / 0.45));
                    float r = 70 * unit;
                    using var dim = new SKPaint
                    {
                        Shader = SKShader.CreateRadialGradient(at, r * 1.3f,
                            new[] { SKColors.Transparent, SKColors.Transparent, SKColors.Black.WithAlpha((byte)(130 * env * alpha)) },
                            new[] { 0f, 0.62f, 1f }, SKShaderTileMode.Clamp)
                    };
                    c.DrawRect(inner, dim);
                    using var ring = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1.6f * unit,
                        IsAntialias = true,
                        Color = col.WithAlpha((byte)(170 * env * alpha))
                    };
                    c.DrawCircle(at.X, at.Y, r * 0.78f, ring);
                    break;
                }
            }
        }

        void DrawCursorShape(SKCanvas c, CursorShape? shape, int id, SKPoint at, float unit, float alpha, float angle = 0)
        {
            string kind = shape?.Kind ?? "arrow";
            var style = St.CursorStyle;

            if (style is CursorStyle.Dot or CursorStyle.Ring or CursorStyle.Touch)
            {
                DrawRoundCursor(c, at, unit, alpha, style);
                return;
            }

            bool vectorKind = kind is "arrow" or "hand" or "ibeam" or "appstarting";
            bool vector = style == CursorStyle.Original ? St.HighResCursor && vectorKind : vectorKind || !cursorImages.ContainsKey(id);
            if (vector)
            {
                bool ibeam = kind == "ibeam";
                bool hand = kind == "hand";
                var path = hand ? HandPath.Value : ibeam ? IBeamPath.Value : style == CursorStyle.Modern ? ModernArrowPath.Value : ArrowPath.Value;
                var hot = hand ? new SKPoint(6.3f, 0.4f) : style == CursorStyle.Modern && !ibeam ? new SKPoint(1, 1) : new SKPoint(0, 0);
                bool darkBg = cursorBgLum < 0.5f;
                var (fillC, strokeC, strokeW) = ibeam
                    ? (darkBg ? SKColors.White : new SKColor(12, 12, 14), darkBg ? new SKColor(12, 12, 14) : SKColors.White, 0.9f)
                    : style switch
                {
                    CursorStyle.MacOS or CursorStyle.Modern => (new SKColor(18, 18, 20), SKColors.White, ibeam ? 0.8f : style == CursorStyle.Modern ? 1.5f : 1.15f),
                    CursorStyle.Colored => (ibeam ? new SKColor(18, 18, 20) : Backgrounds.Parse(St.CursorColor).WithAlpha(255), SKColors.White, ibeam ? 0.8f : 1.3f),
                    // Original (hochauflösend) und Windows: weiß mit schwarzem Rand, Textcursor umgekehrt
                    _ => (ibeam ? SKColors.Black : SKColors.White, ibeam ? SKColors.White : SKColors.Black, ibeam ? 0.7f : 1.05f)
                };
                c.Save();
                c.Translate(at.X, at.Y);
                if (Math.Abs(angle) > 0.05f && !ibeam) c.RotateDegrees(angle);
                c.Scale(unit);
                c.Translate(-hot.X, -hot.Y);
                float blur = style == CursorStyle.Modern ? 1.6f : 0.9f;
                using (var sh = new SKPaint { Color = SKColors.Black.WithAlpha((byte)((style == CursorStyle.Modern ? 95 : 70) * alpha)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur), IsAntialias = true })
                {
                    c.Save();
                    c.Translate(0.4f, style == CursorStyle.Modern ? 1.4f : 0.9f);
                    c.DrawPath(path, sh);
                    c.Restore();
                }
                using var fill = new SKPaint { Color = fillC.WithAlpha((byte)(255 * alpha)), IsAntialias = true };
                using var stroke = new SKPaint
                {
                    Color = strokeC.WithAlpha((byte)(255 * alpha)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = strokeW,
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

        /// <summary>Punkt, Ring oder Touch-Kreis – gleiche Form für alle Cursor-Arten.</summary>
        void DrawRoundCursor(SKCanvas c, SKPoint at, float unit, float alpha, CursorStyle style)
        {
            var col = Backgrounds.Parse(St.CursorColor);
            using var shadow = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(60 * alpha)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.4f * unit), IsAntialias = true };
            switch (style)
            {
                case CursorStyle.Dot:
                {
                    float r = 5.5f * unit;
                    c.DrawCircle(at.X, at.Y + 0.8f * unit, r, shadow);
                    using var fill = new SKPaint { Color = col.WithAlpha((byte)(255 * alpha)), IsAntialias = true };
                    c.DrawCircle(at, r, fill);
                    using var rim = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * alpha)), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f * unit, IsAntialias = true };
                    c.DrawCircle(at, r, rim);
                    break;
                }
                case CursorStyle.Ring:
                {
                    float r = 10 * unit;
                    using var ringShadow = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(60 * alpha)), Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f * unit, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f * unit), IsAntialias = true };
                    c.DrawCircle(at.X, at.Y + 0.6f * unit, r, ringShadow);
                    using var ring = new SKPaint { Color = col.WithAlpha((byte)(255 * alpha)), Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f * unit, IsAntialias = true };
                    c.DrawCircle(at, r, ring);
                    using var dot = new SKPaint { Color = col.WithAlpha((byte)(255 * alpha)), IsAntialias = true };
                    c.DrawCircle(at, 2.2f * unit, dot);
                    break;
                }
                default: // Touch: halbtransparenter Fingerabdruck wie bei Handy-Aufnahmen
                {
                    float r = 13 * unit;
                    using var fill = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(115 * alpha)), IsAntialias = true };
                    c.DrawCircle(at, r, fill);
                    using var rim = new SKPaint { Color = new SKColor(40, 40, 46, (byte)(110 * alpha)), Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f * unit, IsAntialias = true };
                    c.DrawCircle(at, r, rim);
                    break;
                }
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

        SKColorFilter? CamColorFilter() => ColorAdjust(St.CamBrightness, St.CamContrast, St.CamSaturation, St.CamWarmth);

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

            float m = Math.Min(W, H) * (float)Math.Clamp(St.KeysSize, 0.5, 2.5);
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), m * 0.032f);
            string text = cur.Value.Text;
            float tw = font.MeasureText(text);
            float ph = m * 0.07f, pw = tw + m * 0.06f;
            float margin = Math.Min(W, H) * 0.06f;
            float x = St.KeysPosition switch
            {
                KeysPosition.BottomLeft => margin,
                KeysPosition.BottomRight => W - margin - pw,
                _ => W / 2f - pw / 2
            };
            float y = St.KeysPosition == KeysPosition.Top ? margin : H - margin - ph;
            var rect = SKRect.Create(x, y, pw, ph);
            bool light = St.KeysLight;
            c.Save();
            c.Translate(rect.MidX, rect.MidY);
            c.Scale(pop);
            c.Translate(-rect.MidX, -rect.MidY);
            using (var bg = new SKPaint { Color = (light ? new SKColor(250, 250, 252) : new SKColor(18, 18, 22)).WithAlpha((byte)(232 * a)), IsAntialias = true })
                c.DrawRoundRect(rect, ph / 2, ph / 2, bg);
            using (var bd = new SKPaint { Color = (light ? new SKColor(0, 0, 0) : new SKColor(255, 255, 255)).WithAlpha((byte)(40 * a)), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true })
                c.DrawRoundRect(rect, ph / 2, ph / 2, bd);
            using var tp = new SKPaint { Color = (light ? new SKColor(24, 24, 28) : SKColors.White).WithAlpha((byte)(255 * a)), IsAntialias = true };
            c.DrawText(text, rect.MidX, rect.MidY + font.Size * 0.36f, SKTextAlign.Center, font, tp);
            c.Restore();
        }
    }
}
