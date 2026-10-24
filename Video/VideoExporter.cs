using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SnipSnip
{
    public sealed class ExportOptions
    {
        public bool Gif { get; set; }
        /// <summary>1 = Originalgröße, 0.5 = halbe Breite/Höhe.</summary>
        public double Scale { get; set; } = 1;
        public bool Audio { get; set; } = true;
        public int GifFps { get; set; } = 15;
    }

    internal static class VideoExporter
    {
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// Exportiert die behaltenen Abschnitte als MP4 oder GIF.
        /// progress: 0..1 bezogen auf die Gesamtlänge der behaltenen Abschnitte.
        /// </summary>
        public static async Task ExportAsync(string input, MediaInfo info, IReadOnlyList<(TimeSpan Start, TimeSpan End)> keep,
            string output, ExportOptions o, Action<double>? progress, CancellationToken ct)
        {
            if (keep.Count == 0) throw new InvalidOperationException("Es ist kein Abschnitt mehr übrig.");
            double total = keep.Sum(k => (k.End - k.Start).TotalSeconds);
            bool audio = o.Audio && info.HasAudio && !o.Gif;
            bool untouched = keep.Count == 1 && keep[0].Start <= TimeSpan.FromMilliseconds(20)
                             && keep[0].End >= info.Duration - TimeSpan.FromMilliseconds(50);

            // Schnellster Weg: nichts geschnitten/skaliert -> nur kopieren
            if (!o.Gif && untouched && o.Scale >= 0.999 && (audio || !info.HasAudio))
            {
                await Task.Run(() => File.Copy(input, output, true), ct);
                progress?.Invoke(1);
                return;
            }

            var f = new StringBuilder();
            var parts = new StringBuilder();
            for (int i = 0; i < keep.Count; i++)
            {
                string a = N(keep[i].Start.TotalSeconds), b = N(keep[i].End.TotalSeconds);
                f.Append($"[0:v]trim=start={a}:end={b},setpts=PTS-STARTPTS[v{i}];");
                parts.Append($"[v{i}]");
                if (audio)
                {
                    f.Append($"[0:a]atrim=start={a}:end={b},asetpts=PTS-STARTPTS[a{i}];");
                    parts.Append($"[a{i}]");
                }
            }
            f.Append(parts).Append($"concat=n={keep.Count}:v=1:a={(audio ? 1 : 0)}[vc]").Append(audio ? "[ac]" : "").Append(';');

            string scale = o.Scale < 0.999
                ? $"scale=trunc(iw*{N(o.Scale)}/2)*2:trunc(ih*{N(o.Scale)}/2)*2:flags=lanczos"
                : "null";

            var args = new List<string> { "-i", input };
            if (o.Gif)
            {
                f.Append($"[vc]{scale},fps={o.GifFps},split[g0][g1];[g0]palettegen=stats_mode=diff[p];[g1][p]paletteuse=dither=bayer:bayer_scale=5[out]");
                args.AddRange(new[] { "-filter_complex", f.ToString(), "-map", "[out]", "-loop", "0", output });
            }
            else
            {
                f.Append($"[vc]{scale},format=yuv420p[out]");
                args.AddRange(new[] { "-filter_complex", f.ToString(), "-map", "[out]" });
                if (audio) args.AddRange(new[] { "-map", "[ac]", "-c:a", "aac", "-b:a", "160k" });
                args.AddRange(VideoEncoder.Args(App.Settings.VideoCrf, realtime: false));
                args.AddRange(new[] { "-movflags", "+faststart", output });
            }

            await FFmpeg.RunAsync(args, t => progress?.Invoke(Math.Clamp(t.TotalSeconds / Math.Max(0.01, total), 0, 1)), ct);
            progress?.Invoke(1);
        }

        /// <summary>Erzeugt gleichmäßig verteilte Vorschaubilder für die Zeitleiste.</summary>
        public static async Task<List<BitmapSource>> ThumbnailsAsync(string input, TimeSpan duration, int count, int height)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SnipSnip", $"export_thumbs_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                double rate = count / Math.Max(0.1, duration.TotalSeconds);
                await FFmpeg.RunAsync(new[]
                {
                    "-i", input, "-vf", $"fps={N(rate)},scale=-2:{height}", "-frames:v", count.ToString(),
                    "-q:v", "4", Path.Combine(dir, "t%03d.jpg")
                });
                return Directory.GetFiles(dir, "t*.jpg").OrderBy(p => p).Select(LoadFrozen).ToList();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Ein einzelnes Vorschaubild (für Toasts und den Upload-Verlauf).</summary>
        public static async Task<BitmapSource?> ThumbnailAsync(string input)
        {
            string file = Path.Combine(Path.GetTempPath(), "SnipSnip", $"thumb_{Guid.NewGuid():N}.jpg");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await FFmpeg.RunAsync(new[] { "-ss", "0.3", "-i", input, "-frames:v", "1", "-vf", "scale=320:-2", file });
                return File.Exists(file) ? LoadFrozen(file) : null;
            }
            catch { return null; }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        static BitmapSource LoadFrozen(string path)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
    }
}
