using System;
using System.Collections.Generic;
using System.Linq;

namespace SnipSnip
{
    /// <summary>Erzeugt automatische Zooms aus Klicks und Tipp-Phasen.</summary>
    public static class AutoZoom
    {
        public static List<ZoomSegment> Generate(StudioProject p, StudioStyle style)
        {
            var activity = p.Input.Clicks
                .Concat(p.Input.Keys.Select(k => k.T))
                .OrderBy(t => t)
                .ToList();
            var result = new List<ZoomSegment>();
            if (activity.Count == 0) return result;

            double dur = p.Meta.Duration;
            int i = 0;
            while (i < activity.Count)
            {
                int j = i;
                while (j + 1 < activity.Count && activity[j + 1] - activity[j] < 2.8) j++;
                double s = Math.Max(0, activity[i] - 0.7);
                double e = Math.Min(dur, activity[j] + 2.0);
                if (e - s < 1.6) e = Math.Min(dur, s + 1.6);
                if (result.Count > 0 && s - result[^1].End < 1.2) result[^1].End = e;
                else result.Add(new ZoomSegment { Start = s, End = e, Level = style.ZoomLevel, Auto = true });
                i = j + 1;
            }
            return result;
        }
    }

    /// <summary>
    /// Kamerafahrt über die Quellzeit: Zoomstufe und Blickpunkt werden über eine gedämpfte Feder
    /// geglättet (wie in Screen Studio) – vorab mit 120 Hz berechnet, damit Vorschau und Export identisch sind.
    /// </summary>
    public sealed class CameraPath
    {
        const double Dt = 1.0 / 120;
        readonly float[] zoom, fx, fy, loupe, loupeLevel;
        readonly int w, h;

        public CameraPath(StudioProject p, CursorPath cursor)
        {
            var st = p.Edits.Style;
            w = p.Meta.Width;
            h = p.Meta.Height;
            int n = (int)(p.Meta.Duration / Dt) + 2;
            zoom = new float[n];
            fx = new float[n];
            fy = new float[n];
            loupe = new float[n];
            loupeLevel = new float[n];

            double speed = Math.Clamp(st.ZoomSpeed, 0.3, 3);
            double kz = 26 * speed * speed, cz = 2 * Math.Sqrt(kz);   // Zoom-Feder
            double kf = 14 * speed * speed, cf = 2 * Math.Sqrt(kf);   // Blickpunkt-Feder (träger)

            var zooms = p.Edits.Zooms.OrderBy(z => z.Start).ToList();
            double z = 1, vz = 0, x = w / 2.0, y = h / 2.0, vx = 0, vy = 0, l = 0, vl = 0, lastLevel = 2;
            int zi = 0;

            for (int i = 0; i < n; i++)
            {
                double t = i * Dt;
                while (zi < zooms.Count && zooms[zi].End < t) zi++;
                ZoomSegment? seg = zi < zooms.Count && zooms[zi].Start <= t ? zooms[zi] : null;

                double tz = 1, tl = 0, tx = w / 2.0, ty = h / 2.0;
                if (seg != null)
                {
                    if (seg.Type == ZoomType.Loupe) { tl = 1; lastLevel = seg.Level; }
                    else tz = Math.Max(1, seg.Level);
                    if (seg.FollowCursor && cursor != null && p.Input.HasCursor)
                    {
                        var c = cursor.At(t);
                        tx = c.X;
                        ty = c.Y;
                    }
                    else
                    {
                        tx = seg.FocusX * w;
                        ty = seg.FocusY * h;
                    }
                }
                else
                {
                    // beim Herauszoomen den letzten Blickpunkt halten, statt zur Mitte zu schwenken
                    tx = x;
                    ty = y;
                    if (z < 1.02) { tx = w / 2.0; ty = h / 2.0; }
                }

                vz += (kz * (tz - z) - cz * vz) * Dt;
                z += vz * Dt;
                vx += (kf * (tx - x) - cf * vx) * Dt;
                vy += (kf * (ty - y) - cf * vy) * Dt;
                x += vx * Dt;
                y += vy * Dt;
                vl += (kz * (tl - l) - cz * vl) * Dt;
                l += vl * Dt;

                zoom[i] = (float)Math.Max(1, z);
                fx[i] = (float)x;
                fy[i] = (float)y;
                loupe[i] = (float)Math.Clamp(l, 0, 1);
                loupeLevel[i] = (float)lastLevel;
            }
        }

        static float Lerp(float[] a, double t)
        {
            double f = Math.Clamp(t / Dt, 0, a.Length - 1.001);
            int i = (int)f;
            return (float)(a[i] + (a[i + 1] - a[i]) * (f - i));
        }

        /// <summary>Zoomstufe und Blickpunkt (Pixel der Aufnahme) zur Quellzeit t.</summary>
        public (double Zoom, double X, double Y) At(double t) => (Lerp(zoom, t), Lerp(fx, t), Lerp(fy, t));

        /// <summary>Lupen-Stärke (0..1) und -Vergrößerung.</summary>
        public (double Amount, double Level) LoupeAt(double t) => (Lerp(loupe, t), Lerp(loupeLevel, t));

        /// <summary>Wie stark sich die Kamera gerade bewegt (für die Bewegungsunschärfe).</summary>
        public double MotionAt(double t)
        {
            var a = At(t - 1.0 / 60);
            var b = At(t + 1.0 / 60);
            double dz = Math.Abs(b.Zoom - a.Zoom) * 30;
            double dp = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / Math.Max(1, w) * 30 * a.Zoom;
            return dz + dp;
        }
    }
}
