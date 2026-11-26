using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SnipSnip
{
    /// <summary>
    /// Pegelverlauf einer Tonspur für die Wellenform in der Zeitleiste: höchster Ausschlag je 10 ms (0…1).
    /// Wird im Hintergrund mit FFmpeg dekodiert (8 kHz Mono reichen für die Darstellung) und funktioniert
    /// so für WAV-Aufnahmen ebenso wie für importierte Videos und Musik.
    /// </summary>
    internal static class AudioPeaks
    {
        /// <summary>Werte pro Sekunde.</summary>
        public const int Rate = 100;
        const int DecodeRate = 8000, Block = DecodeRate / Rate;

        /// <summary>Für die Doku-Bilder: feste Pegel statt FFmpeg (synchron).</summary>
        internal static Func<string, float[]>? DocsOverride;

        public static Task<float[]> LoadAsync(string file, CancellationToken ct = default) => Task.Run(() => Load(file, ct), ct);

        static float[] Load(string file, CancellationToken ct)
        {
            if (!File.Exists(file) || !FFmpeg.IsAvailable) return Array.Empty<float>();
            var args = new List<string> { "-v", "error" };
            // Aufnahmen, die nicht sauber beendet wurden, haben eine falsche Länge im WAV-Kopf
            if (file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-ignore_length", "1" });
            args.AddRange(new[] { "-i", file, "-vn", "-ac", "1", "-ar", DecodeRate.ToString(), "-f", "f32le", "-" });
            using var proc = FFmpeg.Start(args);
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            using var reg = ct.Register(() => { try { proc.Kill(); } catch { } });
            try
            {
                var peaks = new List<float>(1 << 16);
                var stream = proc.StandardOutput.BaseStream;
                var buf = new byte[Block * 4 * 64];
                int filled = 0, inBlock = 0;
                float max = 0;
                while (true)
                {
                    int n = stream.Read(buf, filled, buf.Length - filled);
                    if (n <= 0) break;
                    filled += n;
                    int whole = filled / 4 * 4;
                    for (int i = 0; i < whole; i += 4)
                    {
                        float v = Math.Abs(BitConverter.ToSingle(buf, i));
                        if (v > max) max = v;
                        if (++inBlock == Block) { peaks.Add(Math.Min(1, max)); max = 0; inBlock = 0; }
                    }
                    // angefangenes Sample für den nächsten Durchlauf behalten
                    Buffer.BlockCopy(buf, whole, buf, 0, filled - whole);
                    filled -= whole;
                }
                if (inBlock > 0) peaks.Add(Math.Min(1, max));
                proc.WaitForExit(2000);
                ct.ThrowIfCancellationRequested();
                return peaks.ToArray();
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
        }

        /// <summary>Höchster Wert im Zeitraum [t0, t1) – mindestens ein Wert.</summary>
        public static float Max(float[] peaks, double t0, double t1)
        {
            if (peaks.Length == 0) return 0;
            int i0 = Math.Clamp((int)(t0 * Rate), 0, peaks.Length - 1);
            int i1 = Math.Clamp((int)Math.Ceiling(t1 * Rate), i0 + 1, peaks.Length);
            float m = 0;
            for (int i = i0; i < i1; i++) if (peaks[i] > m) m = peaks[i];
            return m;
        }

        /// <summary>Ausschlag → Höhe 0…1 (logarithmisch, damit auch leise Sprache sichtbar ist).</summary>
        public static double Height(float peak, double gain)
        {
            double v = peak * gain;
            if (v < 1e-4) return 0;
            double db = 20 * Math.Log10(v);
            return Math.Clamp((db + 50) / 50, 0, 1);
        }
    }
}
