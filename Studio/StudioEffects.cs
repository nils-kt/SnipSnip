using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace SnipSnip
{
    /// <summary>
    /// Zusatz-Effekte des Renderers: Fensterrahmen, Randlinie, 3D-Neigung, Farbkorrektur, Scheinwerfer,
    /// Übergänge, Vignette, Filmkorn, Wasserzeichen, Fortschrittsbalken und Text-Einblendungen.
    /// </summary>
    public sealed partial class StudioRenderer
    {
        SKImage? watermark;
        string? watermarkPath;
        SKImage[]? grainTiles;

        void DisposeEffects()
        {
            watermark?.Dispose();
            if (grainTiles != null) foreach (var g in grainTiles) g.Dispose();
        }

        // ================================================================== Fensterrahmen & Rand

        const float ChromeK = 0.032f;

        /// <summary>Seitenverhältnis des Rahmens (Aufnahme plus Titelleiste).</summary>
        public double ContentAspect => St.Chrome == WindowChrome.None ? CropAspect : 1 / (1 / CropAspect + ChromeK);

        float ChromeBar(SKRect content) => St.Chrome == WindowChrome.None ? 0 : content.Width * ChromeK;

        void DrawChrome(SKCanvas c, SKRect content, float alpha)
        {
            if (St.Chrome == WindowChrome.None) return;
            float bar = ChromeBar(content);
            var r = new SKRect(content.Left, content.Top, content.Right, content.Top + bar);
            bool dark = St.Chrome is WindowChrome.MacDark or WindowChrome.Browser;
            var bg = St.Chrome switch
            {
                WindowChrome.MacLight => new SKColor(236, 236, 238),
                WindowChrome.Windows => new SKColor(243, 243, 245),
                _ => new SKColor(40, 40, 46)
            };
            byte a = (byte)(255 * alpha);
            using (var bp = new SKPaint { Color = bg.WithAlpha(a) }) c.DrawRect(r, bp);
            using (var line = new SKPaint { Color = (dark ? SKColors.Black : new SKColor(0, 0, 0, 40)).WithAlpha((byte)(60 * alpha)), StrokeWidth = Math.Max(1, bar * 0.03f) })
                c.DrawLine(r.Left, r.Bottom - 0.5f, r.Right, r.Bottom - 0.5f, line);

            var textCol = (dark ? new SKColor(200, 200, 208) : new SKColor(70, 70, 78)).WithAlpha(a);
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), bar * 0.4f);
            using var tp = new SKPaint { Color = textCol, IsAntialias = true };
            string title = St.ChromeTitle;

            if (St.Chrome == WindowChrome.Windows)
            {
                if (title.Length > 0) c.DrawText(title, r.Left + bar * 0.5f, r.MidY + font.Size * 0.36f, SKTextAlign.Left, font, tp);
                using var ip = new SKPaint { Color = textCol, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, bar * 0.04f), IsAntialias = true };
                float s = bar * 0.17f, step = bar * 1.25f, x = r.Right - step * 0.5f, y = r.MidY;
                c.DrawLine(x - s, y - s, x + s, y + s, ip); // ✕
                c.DrawLine(x - s, y + s, x + s, y - s, ip);
                x -= step;
                c.DrawRect(SKRect.Create(x - s, y - s, 2 * s, 2 * s), ip); // ☐
                x -= step;
                c.DrawLine(x - s, y, x + s, y, ip); // –
                return;
            }

            // macOS / Browser: Ampel-Knöpfe
            float rad = bar * 0.17f, gap = bar * 0.56f, cx = r.Left + bar * 0.62f;
            foreach (var col in new[] { new SKColor(255, 95, 87), new SKColor(254, 188, 46), new SKColor(40, 200, 64) })
            {
                using var dp = new SKPaint { Color = col.WithAlpha(a), IsAntialias = true };
                c.DrawCircle(cx, r.MidY, rad, dp);
                cx += gap;
            }
            if (St.Chrome == WindowChrome.Browser)
            {
                float w = Math.Min(r.Width * 0.5f, r.Width - bar * 6);
                var pill = SKRect.Create(r.MidX - w / 2, r.Top + bar * 0.2f, w, bar * 0.6f);
                using (var pp = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * alpha)), IsAntialias = true })
                    c.DrawRoundRect(pill, pill.Height / 2, pill.Height / 2, pp);
                c.DrawText(title.Length > 0 ? title : "example.com", pill.MidX, pill.MidY + font.Size * 0.36f, SKTextAlign.Center, font, tp);
            }
            else if (title.Length > 0)
            {
                c.DrawText(title, r.MidX, r.MidY + font.Size * 0.36f, SKTextAlign.Center, font, tp);
            }
        }

        void DrawBorder(SKCanvas c, SKRect content, float radius, float alpha)
        {
            if (St.BorderWidth <= 0.01) return;
            float w = (float)St.BorderWidth * Math.Max(1, Math.Min(content.Width, content.Height) / 600f);
            var col = Backgrounds.Parse(St.BorderColor);
            using var pa = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = w,
                Color = col.WithAlpha((byte)(col.Alpha * alpha)),
                IsAntialias = true
            };
            var r = SKRect.Inflate(content, -w / 2, -w / 2);
            float rr = Math.Max(0, radius - w / 2);
            c.DrawRoundRect(r, rr, rr, pa);
        }

        // ================================================================== 3D-Neigung

        /// <summary>Perspektivische Neigung des Rahmens um seine Mitte (SceneMatrix nutzt sie ebenfalls).</summary>
        SKMatrix TiltMatrix(SKRect r)
        {
            double ay = St.TiltY * Math.PI / 180, ax = St.TiltX * Math.PI / 180;
            if (Math.Abs(ay) + Math.Abs(ax) < 1e-4 || r.Width < 4 || r.Height < 4) return SKMatrix.Identity;
            double cx = r.MidX, cy = r.MidY, d = Math.Max(r.Width, r.Height) * 2.2;
            var src = new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Right, r.Bottom), (r.Left, r.Bottom) };
            var dst = new (double X, double Y)[4];
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                double x = src[i].Item1 - cx, y = src[i].Item2 - cy;
                double x1 = x * Math.Cos(ay), z1 = -x * Math.Sin(ay);
                double y1 = y * Math.Cos(ax) - z1 * Math.Sin(ax), z2 = y * Math.Sin(ax) + z1 * Math.Cos(ax);
                double s = d / (d + z2);
                dst[i] = (x1 * s, y1 * s);
                minX = Math.Min(minX, dst[i].X); maxX = Math.Max(maxX, dst[i].X);
                minY = Math.Min(minY, dst[i].Y); maxY = Math.Max(maxY, dst[i].Y);
            }
            // so verkleinern, dass das geneigte Bild in den ursprünglichen Rahmen passt
            double k = Math.Min(1, Math.Min(r.Width / (maxX - minX), r.Height / (maxY - minY)));
            double ox = (minX + maxX) / 2, oy = (minY + maxY) / 2;
            for (int i = 0; i < 4; i++) dst[i] = (cx + (dst[i].X - ox) * k, cy + (dst[i].Y - oy) * k);
            return Homography(src.Select(s => ((double)s.Item1, (double)s.Item2)).ToArray(), dst);
        }

        /// <summary>Projektive Abbildung, die vier Punkte auf vier Punkte abbildet (Gauß-Elimination, 8×8).</summary>
        static SKMatrix Homography((double X, double Y)[] s, (double X, double Y)[] d)
        {
            var a = new double[8, 9];
            for (int i = 0; i < 4; i++)
            {
                var (x, y) = s[i];
                var (u, v) = d[i];
                int r0 = 2 * i, r1 = r0 + 1;
                a[r0, 0] = x; a[r0, 1] = y; a[r0, 2] = 1; a[r0, 6] = -x * u; a[r0, 7] = -y * u; a[r0, 8] = u;
                a[r1, 3] = x; a[r1, 4] = y; a[r1, 5] = 1; a[r1, 6] = -x * v; a[r1, 7] = -y * v; a[r1, 8] = v;
            }
            for (int col = 0; col < 8; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 8; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
                if (Math.Abs(a[piv, col]) < 1e-12) return SKMatrix.Identity;
                if (piv != col) for (int k = 0; k < 9; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
                for (int r = 0; r < 8; r++)
                {
                    if (r == col) continue;
                    double f = a[r, col] / a[col, col];
                    if (f == 0) continue;
                    for (int k = col; k < 9; k++) a[r, k] -= f * a[col, k];
                }
            }
            double H(int i) => a[i, 8] / a[i, i];
            return new SKMatrix
            {
                ScaleX = (float)H(0), SkewX = (float)H(1), TransX = (float)H(2),
                SkewY = (float)H(3), ScaleY = (float)H(4), TransY = (float)H(5),
                Persp0 = (float)H(6), Persp1 = (float)H(7), Persp2 = 1
            };
        }

        // ================================================================== Farbe

        static SKColorFilter? ColorAdjust(double brightness, double contrast, double saturation, double warmth)
        {
            float b = (float)brightness, ct = (float)contrast, sa = (float)saturation, w = (float)warmth;
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

        SKColorFilter? ScreenColorFilter() => ColorAdjust(St.Brightness, St.Contrast, St.Saturation, St.Warmth);

        // ================================================================== Scheinwerfer

        void DrawSpotlight(SKCanvas c, SKRect inner, double t)
        {
            if (!St.Spotlight || !p.Input.HasCursor || !St.ShowCursor) return;
            float vis = CursorVisibility(t);
            if (vis < 0.01f) return;
            var pos = Cursor.At(t);
            var at = ToScene(inner, pos.X, pos.Y);
            float r = (float)(St.SpotlightSize / 100 * Math.Min(inner.Width, inner.Height));
            byte dim = (byte)(255 * Math.Clamp(St.SpotlightDim, 0, 0.95) * vis);
            using var pa = new SKPaint
            {
                Shader = SKShader.CreateRadialGradient(at, r * 1.35f,
                    new[] { SKColors.Transparent, SKColors.Transparent, SKColors.Black.WithAlpha(dim) },
                    new[] { 0f, 0.7f, 1f }, SKShaderTileMode.Clamp)
            };
            c.DrawRect(inner, pa);
        }

        // ================================================================== Übergänge & Blenden

        /// <summary>0..1: wie nah die Ausgabezeit an einem Schnitt liegt (für Schnitt-Übergänge).</summary>
        float CutStrength(double outT)
        {
            if (St.CutTransition == CutTransition.None) return 0;
            double window = St.CutTransition == CutTransition.Flash ? 0.18 : 0.28;
            double acc = 0, best = double.MaxValue;
            ClipSegment? prev = null;
            foreach (var c in p.Kept)
            {
                if (prev != null && Math.Abs(prev.End - c.Start) > 0.02) best = Math.Min(best, Math.Abs(outT - acc));
                acc += (c.End - c.Start) / Math.Max(0.1, c.Speed);
                prev = c;
            }
            return best >= window ? 0 : (float)Smooth(1 - best / window);
        }

        void DrawFades(SKCanvas c, int W, int H, double outT, float cutK)
        {
            double dur = p.OutputDuration;
            double black = 0;
            if (St.FadeIn > 0.01) black = Math.Max(black, 1 - Smooth(outT / St.FadeIn));
            if (St.FadeOut > 0.01) black = Math.Max(black, 1 - Smooth((dur - outT) / St.FadeOut));
            if (St.CutTransition == CutTransition.Fade) black = Math.Max(black, cutK);
            if (black > 0.004)
            {
                using var pa = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(255 * Math.Clamp(black, 0, 1))) };
                c.DrawRect(0, 0, W, H, pa);
            }
            if (St.CutTransition == CutTransition.Flash && cutK > 0.004)
            {
                using var pa = new SKPaint { Color = SKColors.White.WithAlpha((byte)(220 * cutK * cutK)) };
                c.DrawRect(0, 0, W, H, pa);
            }
        }

        void DrawVignette(SKCanvas c, int W, int H)
        {
            if (St.Vignette <= 0.01) return;
            float r = (float)Math.Sqrt(W * W + H * H) / 2;
            using var pa = new SKPaint
            {
                Shader = SKShader.CreateRadialGradient(new SKPoint(W / 2f, H / 2f), r,
                    new[] { SKColors.Transparent, SKColors.Transparent, SKColors.Black.WithAlpha((byte)(220 * Math.Clamp(St.Vignette, 0, 1))) },
                    new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp)
            };
            c.DrawRect(0, 0, W, H, pa);
        }

        /// <summary>Filmkorn: vorberechnete Rausch-Kacheln, je Bild versetzt (schnell und gleichmäßig).</summary>
        void DrawGrain(SKCanvas c, int W, int H, double outT)
        {
            if (St.Grain <= 0.01) return;
            if (grainTiles == null)
            {
                grainTiles = new SKImage[4];
                for (int i = 0; i < 4; i++)
                {
                    using var s = SKSurface.Create(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
                    using var np = new SKPaint
                    {
                        Shader = SKShader.CreatePerlinNoiseTurbulence(0.9f, 0.9f, 1, 17 + i * 31, new SKPointI(256, 256)),
                        // Graustufen
                        ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                        {
                            0.33f, 0.33f, 0.33f, 0, 0,
                            0.33f, 0.33f, 0.33f, 0, 0,
                            0.33f, 0.33f, 0.33f, 0, 0,
                            0, 0, 0, 0, 255
                        })
                    };
                    s.Canvas.DrawRect(0, 0, 256, 256, np);
                    grainTiles[i] = s.Snapshot();
                }
            }
            int frame = (int)(outT * 24);
            var tile = grainTiles[frame & 3];
            var rnd = new Random(frame * 7919);
            float scale = Math.Max(1, Math.Min(W, H) / 900f);
            var lm = SKMatrix.CreateScale(scale, scale).PostConcat(SKMatrix.CreateTranslation(rnd.Next(256), rnd.Next(256)));
            using var pa = new SKPaint
            {
                Shader = tile.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, lm),
                BlendMode = SKBlendMode.Overlay,
                Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(St.Grain, 0, 1) * 0.6))
            };
            c.DrawRect(0, 0, W, H, pa);
        }

        void DrawWatermark(SKCanvas c, int W, int H)
        {
            if (string.IsNullOrEmpty(St.WatermarkPath)) return;
            if (watermarkPath != St.WatermarkPath)
            {
                watermark?.Dispose();
                watermark = File.Exists(St.WatermarkPath) ? SKImage.FromEncodedData(St.WatermarkPath) : null;
                watermarkPath = St.WatermarkPath;
            }
            if (watermark == null) return;
            float m = Math.Min(W, H);
            float w = (float)(St.WatermarkSize / 100 * W), h = w * watermark.Height / Math.Max(1, watermark.Width);
            if (h > m * 0.3f) { h = m * 0.3f; w = h * watermark.Width / Math.Max(1, watermark.Height); }
            float margin = m * 0.035f;
            bool right = St.WatermarkCorner is CamCorner.TopRight or CamCorner.BottomRight;
            bool bottom = St.WatermarkCorner is CamCorner.BottomLeft or CamCorner.BottomRight;
            var dst = SKRect.Create(right ? W - margin - w : margin, bottom ? H - margin - h : margin, w, h);
            using var pa = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(St.WatermarkOpacity, 0, 1))) };
            c.DrawImage(watermark, dst, Hq, pa);
        }

        void DrawProgress(SKCanvas c, int W, int H, double outT)
        {
            if (!St.ProgressBar) return;
            double dur = Math.Max(0.01, p.OutputDuration);
            float h = Math.Max(3, Math.Min(W, H) * 0.007f);
            using (var track = new SKPaint { Color = new SKColor(255, 255, 255, 40) })
                c.DrawRect(0, H - h, W, h, track);
            using var bar = new SKPaint { Color = Backgrounds.Parse(St.AccentColor) };
            c.DrawRect(0, H - h, (float)(W * Math.Clamp(outT / dur, 0, 1)), h, bar);
        }

        // ================================================================== Text-Einblendungen

        static SKTypeface Face(bool bold) => SKTypeface.FromFamilyName("Segoe UI", bold ? SKFontStyle.Bold : SKFontStyle.Normal);

        static List<string> Wrap(string text, SKFont font, float maxW)
        {
            var lines = new List<string>();
            foreach (var para in text.Replace("\r", "").Split('\n'))
            {
                string line = "";
                foreach (var word in para.Split(' '))
                {
                    string test = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && font.MeasureText(test) > maxW)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else line = test;
                }
                lines.Add(line);
            }
            return lines;
        }

        void DrawTexts(SKCanvas c, int W, int H, double t)
        {
            foreach (var s in p.Edits.Texts)
            {
                if (t < s.Start || t >= s.End || string.IsNullOrWhiteSpace(s.Text)) continue;
                float a = (float)Math.Min(Smooth((t - s.Start) / 0.3), Smooth((s.End - t) / 0.3));
                if (a < 0.01f) continue;
                DrawText(c, W, H, s, a);
            }
        }

        void DrawText(SKCanvas c, int W, int H, TextSegment s, float a)
        {
            float m = Math.Min(W, H) * (float)Math.Clamp(s.Size, 0.4, 3);
            float slide = (1 - a) * Math.Min(W, H) * 0.02f;
            float margin = Math.Min(W, H) * 0.08f;
            var accent = Backgrounds.Parse(St.AccentColor);

            float AnchorY(float blockH) => s.Position switch
            {
                TextPosition.Top => margin,
                TextPosition.Center => (H - blockH) / 2,
                _ => H - margin - blockH
            } + slide;

            switch (s.Style)
            {
                case TextStyle.Title:
                {
                    using var font = new SKFont(Face(true), m * 0.075f);
                    var lines = Wrap(s.Text, font, W * 0.86f);
                    float lh = font.Size * 1.18f, y = AnchorY(lh * lines.Count);
                    using var sh = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(150 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, font.Size * 0.08f), IsAntialias = true };
                    using var tp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a)), IsAntialias = true };
                    foreach (var line in lines)
                    {
                        float by = y + lh * 0.8f;
                        c.DrawText(line, W / 2f, by + font.Size * 0.04f, SKTextAlign.Center, font, sh);
                        c.DrawText(line, W / 2f, by, SKTextAlign.Center, font, tp);
                        y += lh;
                    }
                    break;
                }
                case TextStyle.Subtitle:
                case TextStyle.Callout:
                {
                    bool callout = s.Style == TextStyle.Callout;
                    using var font = new SKFont(Face(callout), m * (callout ? 0.034f : 0.038f));
                    var lines = Wrap(s.Text, font, W * 0.72f);
                    float lh = font.Size * 1.3f, padX = font.Size * 0.8f, padY = font.Size * 0.45f;
                    float tw = lines.Max(l => font.MeasureText(l));
                    float bw = tw + 2 * padX, bh = lh * lines.Count + 2 * padY - (lh - font.Size * 1.1f);
                    var box = SKRect.Create(W / 2f - bw / 2, AnchorY(bh), bw, bh);
                    float rad = callout ? Math.Min(bh / 2, font.Size * 1.1f) : font.Size * 0.35f;
                    using (var sh = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(90 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, font.Size * 0.5f), IsAntialias = true })
                        c.DrawRoundRect(SKRect.Inflate(box, 0, 0).Standardized, rad, rad, sh);
                    using (var bg = new SKPaint { Color = (callout ? accent : new SKColor(14, 14, 18)).WithAlpha((byte)((callout ? 245 : 215) * a)), IsAntialias = true })
                        c.DrawRoundRect(box, rad, rad, bg);
                    using var tp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a)), IsAntialias = true };
                    float y = box.Top + padY + font.Size * 0.95f;
                    foreach (var line in lines)
                    {
                        c.DrawText(line, box.MidX, y, SKTextAlign.Center, font, tp);
                        y += lh;
                    }
                    break;
                }
                case TextStyle.LowerThird:
                {
                    // Bauchbinde: erste Zeile fett (Name/Titel), weitere Zeilen normal
                    var all = s.Text.Replace("\r", "").Split('\n');
                    using var f1 = new SKFont(Face(true), m * 0.04f);
                    using var f2 = new SKFont(Face(false), m * 0.028f);
                    string head = all[0];
                    var rest = all.Skip(1).Where(l => l.Length > 0).ToList();
                    float padX = f1.Size * 0.7f, padY = f1.Size * 0.5f, stripe = Math.Max(3, m * 0.008f);
                    float tw = Math.Max(f1.MeasureText(head), rest.Count > 0 ? rest.Max(l => f2.MeasureText(l)) : 0);
                    float bh = f1.Size * 1.2f + rest.Count * f2.Size * 1.35f + 2 * padY;
                    float bw = tw + 2 * padX + stripe;
                    float x = margin * 0.8f - (1 - a) * Math.Min(W, H) * 0.04f;
                    float y = s.Position == TextPosition.Top ? margin : s.Position == TextPosition.Center ? (H - bh) / 2 : H - margin - bh;
                    var box = SKRect.Create(x, y, bw, bh);
                    using (var bg = new SKPaint { Color = new SKColor(14, 14, 18, (byte)(225 * a)), IsAntialias = true })
                        c.DrawRoundRect(box, f1.Size * 0.25f, f1.Size * 0.25f, bg);
                    using (var st = new SKPaint { Color = accent.WithAlpha((byte)(255 * a)), IsAntialias = true })
                        c.DrawRect(SKRect.Create(box.Left, box.Top, stripe, box.Height), st);
                    using var t1 = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a)), IsAntialias = true };
                    using var t2 = new SKPaint { Color = new SKColor(200, 200, 210, (byte)(255 * a)), IsAntialias = true };
                    float tx = box.Left + stripe + padX, ty = box.Top + padY + f1.Size * 0.95f;
                    c.DrawText(head, tx, ty, SKTextAlign.Left, f1, t1);
                    ty += f1.Size * 0.25f;
                    foreach (var l in rest)
                    {
                        ty += f2.Size * 1.35f;
                        c.DrawText(l, tx, ty, SKTextAlign.Left, f2, t2);
                    }
                    break;
                }
            }
        }
    }
}
