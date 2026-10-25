using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SnipSnip
{
    /// <summary>
    /// Verfolgt den Bildinhalt unter einer Maske (z. B. ein verschobenes Fenster): Blockvergleich auf verkleinerten
    /// Graustufenbildern, grob auf halber Auflösung, dann pixelgenau. Bildschirminhalte verschieben sich starr,
    /// daher reicht reine Verschiebung; bei Stillstand oder Unklarheit bleibt die Maske stehen.
    /// </summary>
    internal static class MaskTracker
    {
        /// <summary>Ziel-Bildrate der Verfolgung (höhere Aufnahmeraten werden ausgedünnt).</summary>
        const double TrackFps = 30;
        /// <summary>Suchradius auf halber Auflösung, in Pixeln pro Bild.</summary>
        const int Range = 40;
        /// <summary>Mittlere Abweichung (Graustufen), ab der ein Treffer als unsicher gilt.</summary>
        const double MaxMeanDiff = 28;

        sealed class Gray
        {
            public int W, H;
            public byte[] Px = Array.Empty<byte>();
            public double T;
            Gray? half;
            public Gray Half => half ??= Downsample(this);
        }

        /// <summary>
        /// Verfolgt das Rechteck <paramref name="start"/> (relativ 0..1), das zur Aufnahmezeit <paramref name="t0"/> gilt,
        /// vorwärts bis <paramref name="to"/> und rückwärts bis <paramref name="from"/>.
        /// </summary>
        public static async Task<List<MaskKey>> TrackAsync(StudioProject p, (double X, double Y, double W, double H) start,
            double t0, double from, double to, IProgress<double>? progress, CancellationToken ct)
        {
            int tw = Math.Min(960, p.Meta.Width);
            int th = Math.Max(2, (int)Math.Round(p.Meta.Height * (double)tw / p.Meta.Width / 2) * 2);
            tw -= tw % 2;
            double total = Math.Max(0.01, to - from);
            double done = 0;
            var keys = new List<MaskKey>();
            double rw = start.W * tw, rh = start.H * th;
            // Eigene Aufnahmen haben eine feste Bildrate: Einzelbilder exakt nach Index lesen (kein Runden durch einen fps-Filter)
            var src = new Source(p.ScreenFile, tw, th, p.Meta.SourceFile == null && p.Meta.Fps > 0 ? p.Meta.Fps : 0);
            double half = 0.5 / src.Fps;

            MaskKey Key(double t, double x, double y) => new(t, x / tw, y / th, start.W, start.H);

            // vorwärts
            double x = start.X * tw, y = start.Y * th;
            Gray? prev = null;
            await foreach (var f in Decode(src, t0, to, ct))
            {
                if (prev != null)
                {
                    var (dx, dy) = Step(prev, f, (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(rw), (int)Math.Round(rh));
                    x += dx; y += dy;
                }
                keys.Add(Key(f.T, x, y));
                prev = f;
                done = f.T - t0;
                progress?.Report(Math.Clamp(done / total, 0, 1));
            }
            if (keys.Count == 0) keys.Add(Key(t0, start.X * tw, start.Y * th));

            // rückwärts in Stücken (jedes Stück vorwärts dekodieren, dann umgekehrt abarbeiten)
            if (from < t0 - half)
            {
                x = start.X * tw; y = start.Y * th;
                prev = null;
                double end = t0 + half;
                var back = new List<MaskKey>();
                while (end > from + half)
                {
                    ct.ThrowIfCancellationRequested();
                    double cs = Math.Max(from, end - 3);
                    var chunk = new List<Gray>();
                    await foreach (var f in Decode(src, cs, end, ct)) chunk.Add(f);
                    if (chunk.Count == 0) break;
                    for (int i = chunk.Count - 1; i >= 0; i--)
                    {
                        var f = chunk[i];
                        if (f.T > t0 + half) continue;
                        if (prev != null)
                        {
                            if (f.T >= prev.T - half) continue;
                            var (dx, dy) = Step(prev, f, (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(rw), (int)Math.Round(rh));
                            x += dx; y += dy;
                            back.Add(Key(f.T, x, y));
                        }
                        prev = f;
                    }
                    done += end - cs;
                    progress?.Report(Math.Clamp(done / total, 0, 1));
                    if (cs <= from) break;
                    end = cs;
                }
                keys.AddRange(back);
            }

            keys.Sort((a, b) => a.T.CompareTo(b.T));
            return Compress(keys);
        }

        /// <summary>Entfernt Positionen mitten in einem Stillstand – die lineare Interpolation ergibt dort ohnehin dasselbe.</summary>
        static List<MaskKey> Compress(List<MaskKey> keys)
        {
            if (keys.Count < 3) return keys;
            var r = new List<MaskKey> { keys[0] };
            for (int i = 1; i < keys.Count - 1; i++)
            {
                var a = r[^1];
                var b = keys[i];
                var c = keys[i + 1];
                bool same = Same(a, b) && Same(b, c);
                if (!same) r.Add(b);
            }
            r.Add(keys[^1]);
            return r;

            static bool Same(MaskKey a, MaskKey b) => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;
        }

        // ---------------------------------------------------------------- Vergleich

        /// <summary>Verschiebung des Inhalts von prev (Rechteck x,y,w,h) nach cur, in Pixeln.</summary>
        static (int Dx, int Dy) Step(Gray prev, Gray cur, int x, int y, int w, int h)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
            int x1 = Math.Min(prev.W, x + w), y1 = Math.Min(prev.H, y + h);
            if (x1 - x0 < 8 || y1 - y0 < 8) return (0, 0);
            if (IsFlat(prev, x0, y0, x1, y1)) return (0, 0);

            // grob: halbe Auflösung, großer Suchbereich
            var (cx, cy, cOk) = Search(prev.Half, cur.Half, x0 / 2, y0 / 2, x1 / 2, y1 / 2, 0, 0, Range);
            if (!cOk) return (0, 0);
            // fein: volle Auflösung um den groben Treffer
            var (fx, fy, fOk) = Search(prev, cur, x0, y0, x1, y1, cx * 2, cy * 2, 2);
            return fOk ? (fx, fy) : (0, 0);
        }

        static (int Dx, int Dy, bool Ok) Search(Gray a, Gray b, int x0, int y0, int x1, int y1, int cx, int cy, int r)
        {
            int area = (x1 - x0) * (y1 - y0);
            int step = Math.Max(1, (int)Math.Sqrt(area / 3000.0));
            int samples = ((x1 - x0 + step - 1) / step) * ((y1 - y0 + step - 1) / step);

            bool Valid(int dx, int dy) => x0 + dx >= 0 && y0 + dy >= 0 && x1 + dx <= b.W && y1 + dy <= b.H;

            long Sad(int dx, int dy, long limit)
            {
                long s = 0;
                for (int yy = y0; yy < y1; yy += step)
                {
                    int ra = yy * a.W, rb = (yy + dy) * b.W + dx;
                    for (int xx = x0; xx < x1; xx += step)
                        s += Math.Abs(a.Px[ra + xx] - b.Px[rb + xx]);
                    if (s >= limit) return s;
                }
                return s;
            }

            // Bevorzugt den Mittelpunkt (keine Bewegung): andere Kandidaten müssen deutlich besser sein
            long best = long.MaxValue;
            int bx = cx, by = cy;
            if (Valid(cx, cy)) best = (long)(Sad(cx, cy, long.MaxValue) * 0.97);
            var gate = new object();
            Parallel.For(-r, r + 1, dyo =>
            {
                long localBest;
                lock (gate) localBest = best;
                int lx = 0, ly = 0;
                bool found = false;
                int dy = cy + dyo;
                for (int dxo = -r; dxo <= r; dxo++)
                {
                    int dx = cx + dxo;
                    if ((dxo == 0 && dyo == 0) || !Valid(dx, dy)) continue;
                    long s = Sad(dx, dy, localBest);
                    if (s < localBest) { localBest = s; lx = dx; ly = dy; found = true; }
                }
                if (!found) return;
                lock (gate)
                {
                    if (localBest < best || (localBest == best && Math.Abs(lx - cx) + Math.Abs(ly - cy) < Math.Abs(bx - cx) + Math.Abs(by - cy)))
                    {
                        best = localBest; bx = lx; by = ly;
                    }
                }
            });
            if (best == long.MaxValue) return (0, 0, false);
            // kein passender Inhalt mehr (Fenster geschlossen, Szenenwechsel) → stehen bleiben
            if (best / (double)samples > MaxMeanDiff) return (0, 0, false);
            return (bx, by, true);
        }

        static bool IsFlat(Gray g, int x0, int y0, int x1, int y1)
        {
            int step = Math.Max(1, (int)Math.Sqrt((x1 - x0) * (y1 - y0) / 2000.0));
            double sum = 0, sq = 0;
            int n = 0;
            for (int y = y0; y < y1; y += step)
                for (int x = x0; x < x1; x += step)
                {
                    int v = g.Px[y * g.W + x];
                    sum += v; sq += v * v; n++;
                }
            double mean = sum / n;
            return sq / n - mean * mean < 4; // Standardabweichung < 2 Graustufen
        }

        static Gray Downsample(Gray g)
        {
            int w = g.W / 2, h = g.H / 2;
            var px = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int r0 = 2 * y * g.W, r1 = r0 + g.W;
                for (int x = 0; x < w; x++)
                    px[y * w + x] = (byte)((g.Px[r0 + 2 * x] + g.Px[r0 + 2 * x + 1] + g.Px[r1 + 2 * x] + g.Px[r1 + 2 * x + 1] + 2) >> 2);
            }
            return new Gray { W = w, H = h, Px = px, T = g.T };
        }

        // ---------------------------------------------------------------- Dekodieren

        sealed record Source(string File, int W, int H, int NativeFps)
        {
            /// <summary>Nur jedes n-te Bild der Aufnahme verwenden.</summary>
            public int Step => NativeFps > 0 ? Math.Max(1, (int)Math.Round(NativeFps / TrackFps)) : 1;
            public double Fps => NativeFps > 0 ? NativeFps / (double)Step : TrackFps;
        }

        static async IAsyncEnumerable<Gray> Decode(Source s, double from, double to,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            from = Math.Max(0, from);
            var args = new List<string> { "-v", "error" };
            long k0 = 0;
            if (s.NativeFps > 0)
            {
                // Bild k liegt bei k/fps; ab einem halben Bild davor suchen, damit genau Bild k das erste ist
                int fps = s.NativeFps;
                k0 = (long)Math.Ceiling(from * fps / s.Step - 1e-6) * s.Step;
                long k1 = (long)Math.Floor(to * fps + 1e-6);
                if (k1 < k0) yield break;
                if (k0 > 0) args.AddRange(new[] { "-ss", N((k0 - 0.5) / fps) });
                args.AddRange(new[] { "-i", s.File, "-frames:v", (k1 - k0 + 1).ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough" });
                args.AddRange(new[] { "-an", "-vf", $"scale={s.W}:{s.H}:flags=area,format=gray" });
            }
            else
            {
                args.AddRange(new[] { "-ss", N(from), "-i", s.File, "-t", N(Math.Max(0.05, to - from)) });
                args.AddRange(new[] { "-an", "-vf", $"fps={N(TrackFps)},scale={s.W}:{s.H}:flags=area,format=gray" });
            }
            args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "gray", "-" });

            using var proc = FFmpeg.Start(args);
            _ = proc.StandardError.ReadToEndAsync();
            var stdout = proc.StandardOutput.BaseStream;
            int index = 0;
            try
            {
                while (true)
                {
                    var buf = new byte[s.W * s.H];
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int n = await stdout.ReadAsync(buf.AsMemory(off), ct);
                        if (n <= 0) yield break;
                        off += n;
                    }
                    int i = index++;
                    if (s.NativeFps > 0)
                    {
                        if (i % s.Step != 0) continue;
                        yield return new Gray { W = s.W, H = s.H, Px = buf, T = (k0 + i) / (double)s.NativeFps };
                    }
                    else yield return new Gray { W = s.W, H = s.H, Px = buf, T = from + i / TrackFps };
                }
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
        }    }
}
