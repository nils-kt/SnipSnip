using System;
using System.IO;
using SkiaSharp;

namespace SnipSnip
{
    /// <summary>Eingebaute Hintergründe: weiche "Wallpaper" (prozedural erzeugt) und Farbverläufe.</summary>
    public static class Backgrounds
    {
        public sealed record Gradient(string Name, string[] Colors, float Angle);

        public sealed record Wallpaper(string Name, string Base, string[] Blobs);

        public static readonly Gradient[] Gradients =
        {
            new("Mitternacht", new[] { "#0F172A", "#312E81", "#7C3AED" }, 135),
            new("Sonnenuntergang", new[] { "#F97316", "#DB2777", "#7C3AED" }, 120),
            new("Ozean", new[] { "#0EA5E9", "#2563EB", "#1E1B4B" }, 160),
            new("Minze", new[] { "#34D399", "#0D9488", "#134E4A" }, 140),
            new("Pfirsich", new[] { "#FDE68A", "#FCA5A5", "#F472B6" }, 125),
            new("Grafit", new[] { "#3F3F46", "#18181B", "#09090B" }, 160),
            new("Polarlicht", new[] { "#22D3EE", "#A78BFA", "#F0ABFC" }, 110),
            new("Wald", new[] { "#A3E635", "#16A34A", "#14532D" }, 150),
            new("Glut", new[] { "#FACC15", "#EF4444", "#7F1D1D" }, 135),
            new("Nebel", new[] { "#E2E8F0", "#CBD5E1", "#94A3B8" }, 135)
        };

        public static readonly Wallpaper[] Wallpapers =
        {
            new("Dämmerung", "#14123A", new[] { "#4F8CFF", "#FF7A59", "#33D6C4", "#8C5CFF" }),
            new("Lagune", "#062A3A", new[] { "#00C2A8", "#3D7BFF", "#7CF3D6", "#0B5FA5" }),
            new("Flamingo", "#3A0F2E", new[] { "#FF5C8A", "#FFB86B", "#B34DFF", "#FF7AD1" }),
            new("Gletscher", "#DDE7F5", new[] { "#A6C8FF", "#FFFFFF", "#C9B8FF", "#8EE3F5" }),
            new("Sahara", "#3A1E0C", new[] { "#FFB547", "#FF6B3D", "#FFD89B", "#C2410C" }),
            new("Aurora", "#06121F", new[] { "#2DD4BF", "#A3E635", "#6366F1", "#22D3EE" }),
            new("Lavendel", "#2A2240", new[] { "#C4B5FD", "#F9A8D4", "#818CF8", "#E9D5FF" }),
            new("Tinte", "#08080C", new[] { "#3B82F6", "#1E3A8A", "#64748B", "#0EA5E9" })
        };

        public static SKColor Parse(string hex) => SKColor.TryParse(hex, out var c) ? c : SKColors.Black;

        /// <summary>Zeichnet den Hintergrund des Stils in die angegebene Größe.</summary>
        public static SKImage Render(StudioStyle st, int w, int h)
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var c = surface.Canvas;
            var rect = new SKRect(0, 0, w, h);

            switch (st.Background)
            {
                case BackgroundKind.Solid:
                    c.Clear(Parse(st.SolidColor));
                    break;

                case BackgroundKind.Image when st.ImagePath != null && File.Exists(st.ImagePath):
                {
                    c.Clear(SKColors.Black);
                    using var img = SKImage.FromEncodedData(st.ImagePath);
                    if (img != null)
                    {
                        float s = Math.Max((float)w / img.Width, (float)h / img.Height);
                        float iw = img.Width * s, ih = img.Height * s;
                        var dst = SKRect.Create((w - iw) / 2, (h - ih) / 2, iw, ih);
                        using var p = new SKPaint();
                        if (st.BackgroundBlur > 0)
                            p.ImageFilter = SKImageFilter.CreateBlur((float)st.BackgroundBlur * w / 1000f, (float)st.BackgroundBlur * w / 1000f);
                        c.DrawImage(img, dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
                    }
                    break;
                }

                case BackgroundKind.Gradient:
                {
                    var g = Gradients[Math.Clamp(st.Preset, 0, Gradients.Length - 1)];
                    DrawLinear(c, rect, g.Colors, g.Angle);
                    break;
                }

                default:
                    DrawWallpaper(c, w, h, Wallpapers[Math.Clamp(st.Preset, 0, Wallpapers.Length - 1)]);
                    break;
            }
            return surface.Snapshot();
        }

        static void DrawLinear(SKCanvas c, SKRect r, string[] colors, float angle)
        {
            double a = angle * Math.PI / 180;
            float dx = (float)Math.Cos(a) * r.Width / 2, dy = (float)Math.Sin(a) * r.Height / 2;
            var center = new SKPoint(r.MidX, r.MidY);
            using var p = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(center.X - dx, center.Y - dy), new SKPoint(center.X + dx, center.Y + dy),
                    Array.ConvertAll(colors, Parse), null, SKShaderTileMode.Clamp)
            };
            c.DrawRect(r, p);
        }

        /// <summary>Weiche Farbflächen auf dunklem/hellem Grund – deterministisch je Vorlage.</summary>
        static void DrawWallpaper(SKCanvas c, int w, int h, Wallpaper wp)
        {
            c.Clear(Parse(wp.Base));
            int seed = 17;
            foreach (char ch in wp.Name) seed = unchecked(seed * 31 + ch); // stabil (string.GetHashCode ist pro Prozess zufällig)
            var rng = new Random(seed & 0x7FFFFFFF);
            float m = Math.Max(w, h);
            using var p = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Screen };
            for (int i = 0; i < 7; i++)
            {
                var col = Parse(wp.Blobs[i % wp.Blobs.Length]);
                float cx = (float)(rng.NextDouble() * 1.2 - 0.1) * w;
                float cy = (float)(rng.NextDouble() * 1.2 - 0.1) * h;
                float r = (float)(0.35 + rng.NextDouble() * 0.45) * m;
                p.Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), r,
                    new[] { col.WithAlpha(170), col.WithAlpha(60), col.WithAlpha(0) }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp);
                c.DrawCircle(cx, cy, r, p);
            }
            // feines Rauschen gegen Farbstreifen (Banding)
            using var noise = new SKPaint
            {
                Shader = SKShader.CreatePerlinNoiseFractalNoise(0.9f, 0.9f, 2, 7),
                BlendMode = SKBlendMode.Overlay,
                Color = SKColors.White.WithAlpha(22)
            };
            c.DrawRect(0, 0, w, h, noise);
        }
    }
}
