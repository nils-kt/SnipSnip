using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SnipSnip
{
    public record MediaInfo(TimeSpan Duration, int Width, int Height, bool HasAudio);

    /// <summary>Findet, lädt und startet ffmpeg.exe (Video-Kodierung, Schnitt, GIF-Export).</summary>
    internal static class FFmpeg
    {
        const string BuildsUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";

        public static string ManagedDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipSnip", "ffmpeg");

        static string? cached;

        /// <summary>Nur für die Doku-Bilder: fester, neutraler Pfad statt der echten Installation.</summary>
        internal static string? DocsOverride;

        public static string? Find()
        {
            if (DocsOverride != null) return DocsOverride;
            if (cached != null && File.Exists(cached)) return cached;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(App.Settings.FfmpegPath)) candidates.Add(App.Settings.FfmpegPath.Trim('"', ' '));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
            candidates.Add(Path.Combine(ManagedDir, "ffmpeg.exe"));
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try { candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe")); } catch (ArgumentException) { }
            }

            cached = candidates.FirstOrDefault(File.Exists);
            return cached;
        }

        public static bool IsAvailable => Find() != null;

        public static void ResetCache() => cached = null;

        // ------------------------------------------------------------------ Download

        public static string DownloadName =>
            RuntimeInformation.OSArchitecture == Architecture.Arm64
                ? "ffmpeg-master-latest-winarm64-gpl.zip"
                : "ffmpeg-master-latest-win64-gpl.zip";

        /// <summary>Lädt den offiziellen BtbN-Build, prüft die SHA-256-Summe und entpackt nur ffmpeg.exe.</summary>
        public static async Task DownloadAsync(IProgress<(long Done, long? Total)> progress, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SnipSnip/1.0");

            string sums = await http.GetStringAsync(BuildsUrl + "checksums.sha256", ct);
            string? expected = sums.Split('\n')
                .Select(l => l.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2 && p[1].TrimStart('*') == DownloadName)
                .Select(p => p[0].ToLowerInvariant())
                .FirstOrDefault();
            if (expected == null) throw new Exception("Prüfsumme für den FFmpeg-Download nicht gefunden.");

            Directory.CreateDirectory(ManagedDir);
            string tmpZip = Path.Combine(ManagedDir, "download.tmp");
            try
            {
                using (var resp = await http.GetAsync(BuildsUrl + DownloadName, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long? total = resp.Content.Headers.ContentLength;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(tmpZip);
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    var sw = Stopwatch.StartNew();
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (sw.ElapsedMilliseconds > 100) { progress.Report((done, total)); sw.Restart(); }
                    }
                    progress.Report((done, total));
                }

                string actual;
                await using (var fs = File.OpenRead(tmpZip))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (actual != expected) throw new Exception("Die Prüfsumme des FFmpeg-Downloads stimmt nicht – Datei verworfen.");

                using var zip = ZipFile.OpenRead(tmpZip);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new Exception("ffmpeg.exe nicht im Archiv gefunden.");
                string target = Path.Combine(ManagedDir, "ffmpeg.exe");
                entry.ExtractToFile(target + ".new", true);
                File.Move(target + ".new", target, true);
                ResetCache();
            }
            finally
            {
                try { File.Delete(tmpZip); } catch { }
            }
        }

        // ------------------------------------------------------------------ Ausführen

        public static Process Start(IEnumerable<string> args, bool redirectStdin = false)
        {
            string exe = Find() ?? throw new InvalidOperationException("FFmpeg wurde nicht gefunden.");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = redirectStdin,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (var a in args) psi.ArgumentList.Add(a);
            return Process.Start(psi) ?? throw new Exception("FFmpeg konnte nicht gestartet werden.");
        }

        /// <summary>Startet ffmpeg, meldet den Fortschritt (Ausgabezeit) und wirft bei Fehlern mit den letzten Log-Zeilen.</summary>
        public static async Task RunAsync(IEnumerable<string> args, Action<TimeSpan>? onProgress = null, CancellationToken ct = default)
        {
            var all = new List<string> { "-y", "-nostats", "-progress", "pipe:1" };
            all.AddRange(args);
            using var p = Start(all);

            var errTail = new Queue<string>();
            var errTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync()) != null)
                {
                    lock (errTail) { errTail.Enqueue(line); if (errTail.Count > 12) errTail.Dequeue(); }
                }
            });
            var outTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    if (onProgress != null && line.StartsWith("out_time_us=") &&
                        long.TryParse(line.AsSpan(12), out long us) && us >= 0)
                        onProgress(TimeSpan.FromTicks(us * 10));
                }
            });

            using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            {
                await p.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(errTask, outTask);
            }
            ct.ThrowIfCancellationRequested();

            if (p.ExitCode != 0)
            {
                string tail;
                lock (errTail) tail = string.Join("\n", errTail.TakeLast(4));
                throw new Exception($"FFmpeg ist fehlgeschlagen (Code {p.ExitCode}).\n{tail}");
            }
        }

        static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
        static readonly Regex VideoRx = new(@"Stream #.*?Video:.*?,\s*(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);

        /// <summary>Liest Dauer, Auflösung und Audio-Spur aus (über "ffmpeg -i").</summary>
        public static async Task<MediaInfo> ProbeAsync(string file)
        {
            using var p = Start(new[] { "-i", file });
            var errTask = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            string err = await errTask;

            var d = DurationRx.Match(err);
            var v = VideoRx.Match(err);
            var dur = d.Success
                ? new TimeSpan(0, int.Parse(d.Groups[1].Value), int.Parse(d.Groups[2].Value), 0)
                  + TimeSpan.FromSeconds(double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture))
                : TimeSpan.Zero;
            return new MediaInfo(dur,
                v.Success ? int.Parse(v.Groups[1].Value) : 0,
                v.Success ? int.Parse(v.Groups[2].Value) : 0,
                Regex.IsMatch(err, @"Stream #.*?Audio:"));
        }

        public static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
