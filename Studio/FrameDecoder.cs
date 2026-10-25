using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;

namespace SnipSnip
{
    /// <summary>
    /// Liest Videobilder über FFmpeg (rawvideo/BGRA über eine Pipe). Sequenziell vorwärts ist billig;
    /// Rücksprünge oder große Sprünge starten den Decoder an der gewünschten Stelle neu.
    /// </summary>
    public sealed class FrameDecoder : IDisposable
    {
        readonly string file;
        readonly int maxWidth;
        readonly double fps;
        readonly double timeOffset;
        Process? proc;
        Stream? stdout;
        int w, h;
        byte[]? buf, readBuf;
        bool pending;
        double startT, curT = double.NaN;
        long frameIndex;
        SKImage? current;
        readonly Queue<SKImage> retired = new();
        readonly object gate = new();

        /// <param name="maxWidth">0 = Originalgröße, sonst auf diese Breite verkleinern (Vorschau).</param>
        /// <param name="timeOffset">wird auf jede angefragte Zeit addiert (z. B. Webcam-Versatz)</param>
        public FrameDecoder(string file, int srcWidth, int srcHeight, double fps, int maxWidth = 0, double timeOffset = 0)
        {
            this.file = file;
            this.fps = Math.Clamp(fps, 1, 120);
            this.maxWidth = maxWidth;
            this.timeOffset = timeOffset;
            w = srcWidth;
            h = srcHeight;
            if (maxWidth > 0 && w > maxWidth)
            {
                h = (int)Math.Round(h * (double)maxWidth / w) & ~1;
                w = maxWidth & ~1;
            }
            w = Math.Max(2, w & ~1);
            h = Math.Max(2, h & ~1);
        }

        public int Width => w;
        public int Height => h;

        void Restart(double t)
        {
            Close();
            startT = Math.Max(0, t);
            var args = new List<string>
            {
                "-v", "error", "-ss", startT.ToString("0.###", CultureInfo.InvariantCulture), "-i", file,
                "-an", "-vf", $"fps={fps.ToString("0.###", CultureInfo.InvariantCulture)},scale={w}:{h}:flags=bicubic",
                "-f", "rawvideo", "-pix_fmt", "bgra", "-"
            };
            proc = FFmpeg.Start(args);
            _ = proc.StandardError.ReadToEndAsync();
            stdout = proc.StandardOutput.BaseStream;
            buf ??= new byte[w * h * 4];
            readBuf ??= new byte[w * h * 4];
            frameIndex = 0;
            curT = double.NaN;
        }

        /// <summary>Liest ein Bild nur als Rohdaten – übersprungene Bilder (z. B. an Schnitten) kosten so kein SKImage.</summary>
        bool ReadFrame()
        {
            if (stdout == null || buf == null || readBuf == null) return false;
            int off = 0;
            while (off < readBuf.Length)
            {
                int n = stdout.Read(readBuf, off, readBuf.Length - off);
                if (n <= 0) return false;
                off += n;
            }
            (buf, readBuf) = (readBuf, buf);
            curT = startT + frameIndex / fps;
            frameIndex++;
            pending = true;
            return true;
        }

        /// <summary>
        /// Macht das zuletzt gelesene Bild zum aktuellen. Ältere Bilder werden erst später freigegeben, weil parallele
        /// Export-Renderer sie noch benutzen – pro Abruf entsteht höchstens ein neues Bild, die Warteschlange reicht also sicher.
        /// </summary>
        void Publish()
        {
            if (!pending || buf == null) return;
            pending = false;
            if (current != null)
            {
                retired.Enqueue(current);
                while (retired.Count > 48) retired.Dequeue().Dispose();
            }
            current = SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque), buf, w * 4);
        }

        /// <summary>Bild zur Zeit t (Sekunden). Gibt das Bild zurück, das Eigentum bleibt beim Decoder.</summary>
        public SKImage? Get(double t)
        {
            lock (gate)
            {
                t += timeOffset;
                if (t < 0) t = 0;
                double half = 0.5 / fps;
                bool needRestart = proc == null
                                   || (double.IsNaN(curT) && (t < startT - half || t > startT + 3))
                                   || (!double.IsNaN(curT) && (t < curT - half || t > curT + 3));
                if (needRestart) Restart(t);
                // genau zwischen zwei Bildern immer das frühere nehmen (Toleranz gegen Rundungsrauschen) –
                // verfolgte Masken rechnen mit derselben Regel
                while (double.IsNaN(curT) || curT + half < t - 1e-6)
                {
                    if (!ReadFrame()) break;
                }
                Publish();
                return current;
            }
        }

        void Close()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            proc?.Dispose();
            proc = null;
            stdout = null;
        }

        public void Dispose()
        {
            lock (gate)
            {
                Close();
                current?.Dispose();
                current = null;
                while (retired.Count > 0) retired.Dequeue().Dispose();
            }
        }

        /// <summary>Einzelbild für das Scrubben (eigener, kurzlebiger Prozess).</summary>
        public static async Task<SKImage?> GrabAsync(string file, double t, int width, int height)
        {
            var args = new List<string>
            {
                "-v", "error", "-ss", Math.Max(0, t).ToString("0.###", CultureInfo.InvariantCulture), "-i", file,
                "-an", "-frames:v", "1", "-vf", $"scale={width}:{height}", "-f", "rawvideo", "-pix_fmt", "bgra", "-"
            };
            using var p = FFmpeg.Start(args);
            _ = p.StandardError.ReadToEndAsync();
            var data = new byte[width * height * 4];
            int off = 0;
            var s = p.StandardOutput.BaseStream;
            while (off < data.Length)
            {
                int n = await s.ReadAsync(data.AsMemory(off));
                if (n <= 0) break;
                off += n;
            }
            await p.WaitForExitAsync();
            if (off < data.Length) return null;
            return SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), data, width * 4);
        }
    }
}
