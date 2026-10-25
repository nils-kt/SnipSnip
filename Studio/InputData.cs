using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SnipSnip
{
    /// <summary>Gelesene Eingabedaten einer Aufnahme (Maus, Klicks, Tasten, Cursorformen).</summary>
    public sealed class InputData
    {
        public static readonly InputData Empty = new();

        public double[] T { get; private set; } = Array.Empty<double>();
        public float[] X { get; private set; } = Array.Empty<float>();
        public float[] Y { get; private set; } = Array.Empty<float>();
        double[] lastMove = Array.Empty<double>();
        public int[] Shape { get; private set; } = Array.Empty<int>();
        public List<(double T, int Button, bool Down)> Buttons { get; } = new();
        public List<(double T, string Text)> Keys { get; } = new();
        public List<CursorShape> Shapes { get; private set; } = new();
        public string Dir { get; private set; } = "";

        public bool HasCursor => T.Length > 0;

        public IEnumerable<double> Clicks => Buttons.Where(b => b.Down).Select(b => b.T);

        public static InputData Load(string dir, RecordingMeta meta)
        {
            var d = new InputData { Dir = dir };
            string ev = Path.Combine(dir, "events.jsonl");
            if (!File.Exists(ev)) return d;

            var t = new List<double>();
            var x = new List<float>();
            var y = new List<float>();
            var s = new List<int>();
            foreach (var line in File.ReadLines(ev))
            {
                if (line.Length < 5) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    double tt = r.GetProperty("t").GetDouble();
                    if (r.TryGetProperty("x", out var px))
                    {
                        t.Add(tt);
                        x.Add(px.GetInt32());
                        y.Add(r.GetProperty("y").GetInt32());
                        s.Add(r.TryGetProperty("c", out var c) ? c.GetInt32() : 0);
                    }
                    else if (r.TryGetProperty("d", out var dn)) d.Buttons.Add((tt, dn.GetInt32(), true));
                    else if (r.TryGetProperty("u", out var up)) d.Buttons.Add((tt, up.GetInt32(), false));
                    else if (r.TryGetProperty("k", out var k)) d.Keys.Add((tt, k.GetString() ?? ""));
                }
                catch (JsonException) { /* letzte Zeile nach Absturz evtl. unvollständig */ }
            }
            d.T = t.ToArray();
            d.X = x.ToArray();
            d.Y = y.ToArray();
            d.Shape = s.ToArray();
            d.lastMove = new double[t.Count];
            for (int k = 0; k < t.Count; k++)
                d.lastMove[k] = k > 0 && Math.Abs(x[k] - x[k - 1]) + Math.Abs(y[k] - y[k - 1]) <= 1.5 ? d.lastMove[k - 1] : t[k];

            string shapes = Path.Combine(dir, "cursors.json");
            if (File.Exists(shapes))
            {
                try { d.Shapes = JsonSerializer.Deserialize<List<CursorShape>>(File.ReadAllText(shapes), AppSettings.Json) ?? new(); }
                catch { }
            }
            return d;
        }

        int IndexAt(double t)
        {
            int i = Array.BinarySearch(T, t);
            if (i < 0) i = ~i - 1;
            return Math.Clamp(i, 0, T.Length - 1);
        }

        /// <summary>Rohe Cursorposition (Pixel der Aufnahme) und Form zur Zeit t.</summary>
        public (double X, double Y, int Shape) RawAt(double t)
        {
            if (T.Length == 0) return (0, 0, -1);
            int i = IndexAt(t);
            if (i + 1 < T.Length && T[i + 1] > T[i] && t > T[i])
            {
                double f = Math.Clamp((t - T[i]) / (T[i + 1] - T[i]), 0, 1);
                // Lange Lücken (Pausen, Stillstand) nicht interpolieren
                if (T[i + 1] - T[i] > 0.3) f = 0;
                return (X[i] + (X[i + 1] - X[i]) * f, Y[i] + (Y[i + 1] - Y[i]) * f, Shape[i]);
            }
            return (X[i], Y[i], Shape[i]);
        }

        /// <summary>Zeitpunkt der letzten Bewegung vor t (für das Ausblenden im Ruhezustand).</summary>
        public double LastMoveBefore(double t)
        {
            return T.Length == 0 ? 0 : lastMove[IndexAt(t)];
        }

        /// <summary>Zusammenhängende Tipp-Phasen (Tastenanschläge ohne Kürzel).</summary>
        public IEnumerable<(double Start, double End)> TypingSpans(double minDuration, double maxGap)
        {
            var typing = Keys.Where(k => k.Text.Length == 0 || k.Text.Length == 1).Select(k => k.T).OrderBy(v => v).ToList();
            int i = 0;
            while (i < typing.Count)
            {
                int j = i;
                while (j + 1 < typing.Count && typing[j + 1] - typing[j] <= maxGap) j++;
                if (typing[j] - typing[i] >= minDuration) yield return (typing[i] + 0.2, typing[j] - 0.1);
                i = j + 1;
            }
        }
    }

    /// <summary>Geglättete Cursorbahn (kritisch gedämpfte Feder, vorab berechnet mit 240 Hz).</summary>
    public sealed class CursorPath
    {
        const double Dt = 1.0 / 240;
        readonly float[] sx = Array.Empty<float>(), sy = Array.Empty<float>();
        readonly InputData input;
        public double Smoothing { get; }

        public CursorPath(InputData input, double smoothing, double duration)
        {
            this.input = input;
            Smoothing = smoothing;
            if (!input.HasCursor || smoothing <= 0.01) return;

            // Federhärte: 0 = sehr direkt, 1 = sehr weich
            double k = 1400 * Math.Pow(1 - Math.Clamp(smoothing, 0, 0.98), 2.2) + 25;
            double c = 2 * Math.Sqrt(k);
            int n = (int)(duration / Dt) + 2;
            sx = new float[n];
            sy = new float[n];
            var (x, y, _) = input.RawAt(0);
            double vx = 0, vy = 0;
            for (int i = 0; i < n; i++)
            {
                var (tx, ty, _) = input.RawAt(i * Dt);
                // Sprünge (z. B. Monitorwechsel) nicht durch den Raum gleiten lassen
                if (Math.Abs(tx - x) + Math.Abs(ty - y) > 900) { x = tx; y = ty; vx = vy = 0; }
                vx += (k * (tx - x) - c * vx) * Dt;
                vy += (k * (ty - y) - c * vy) * Dt;
                x += vx * Dt;
                y += vy * Dt;
                sx[i] = (float)x;
                sy[i] = (float)y;
            }
        }

        public (double X, double Y) At(double t)
        {
            if (sx.Length == 0)
            {
                var r = input.RawAt(t);
                return (r.X, r.Y);
            }
            double f = Math.Clamp(t / Dt, 0, sx.Length - 1.001);
            int i = (int)f;
            double a = f - i;
            return (sx[i] + (sx[i + 1] - sx[i]) * a, sy[i] + (sy[i + 1] - sy[i]) * a);
        }
    }
}
