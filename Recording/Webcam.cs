using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SnipSnip
{
    /// <summary>Webcam-Aufnahme über FFmpeg (DirectShow) in eine eigene Datei.</summary>
    internal sealed class WebcamCapture
    {
        static readonly Regex DeviceRx = new("\"([^\"]+)\"\\s*\\(video\\)", RegexOptions.Compiled);
        static readonly Regex StartRx = new(@"start:\s*(\d+\.\d+)", RegexOptions.Compiled);

        Process? process;
        readonly TaskCompletionSource<double> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Path { get; }

        /// <summary>Wanduhrzeit (Unix-Sekunden) des ersten Kamerabilds = Zeitpunkt 0 der Webcam-Datei.</summary>
        public double StartEpoch { get; private set; }

        public WebcamCapture(string path) => Path = path;

        public static async Task<List<string>> ListDevicesAsync()
        {
            var list = new List<string>();
            if (!FFmpeg.IsAvailable) return list;
            try
            {
                using var p = FFmpeg.Start(new[] { "-list_devices", "true", "-f", "dshow", "-i", "dummy" });
                _ = p.StandardOutput.ReadToEndAsync();
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                foreach (Match m in DeviceRx.Matches(err))
                    if (!list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
            }
            catch { }
            return list;
        }

        public void Start(string device, int fps)
        {
            var args = new List<string>
            {
                "-y", "-f", "dshow", "-rtbufsize", "256M", "-use_wallclock_as_timestamps", "1",
                "-i", $"video={device}",
                "-vf", $"fps={fps}", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-pix_fmt", "yuv420p", Path
            };
            process = FFmpeg.Start(args, redirectStdin: true);
            _ = process.StandardOutput.ReadToEndAsync();
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                {
                    if (started.Task.IsCompleted) continue;
                    var m = StartRx.Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 1e9)
                    {
                        StartEpoch = s;
                        started.TrySetResult(s);
                    }
                }
                started.TrySetResult(0);
            });
        }

        /// <summary>Wartet, bis die Kamera läuft (max. timeout). false = Kamera nicht verfügbar.</summary>
        public async Task<bool> WaitStartedAsync(TimeSpan timeout)
        {
            var done = await Task.WhenAny(started.Task, Task.Delay(timeout));
            return done == started.Task && started.Task.Result > 0;
        }

        public async Task StopAsync()
        {
            if (process == null || process.HasExited) return;
            try
            {
                await process.StandardInput.WriteAsync('q');
                await process.StandardInput.FlushAsync();
            }
            catch { }
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(5000)) != exited)
                try { process.Kill(true); } catch { }
        }
    }

    /// <summary>Blendet die Desktop-Symbole während der Aufnahme aus.</summary>
    internal static class DesktopIcons
    {
        static IntPtr hidden;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? title);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        static IntPtr FindListView()
        {
            IntPtr defView = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr worker = IntPtr.Zero;
            while (defView == IntPtr.Zero && (worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }

        public static void Hide()
        {
            try
            {
                var lv = FindListView();
                if (lv != IntPtr.Zero && IsWindowVisible(lv))
                {
                    ShowWindow(lv, 0);
                    hidden = lv;
                }
            }
            catch { }
        }

        public static void Restore()
        {
            if (hidden == IntPtr.Zero) return;
            try { ShowWindow(hidden, 5); } catch { }
            hidden = IntPtr.Zero;
        }
    }
}
