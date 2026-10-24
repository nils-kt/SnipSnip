using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SnipSnip
{
    /// <summary>Stoppuhr, die pausiert werden kann – gemeinsamer Takt für Bild und Ton.</summary>
    internal sealed class RecordingClock
    {
        readonly Stopwatch sw = new();
        public TimeSpan Elapsed => sw.Elapsed;
        public bool IsPaused { get; private set; }
        public void Start() => sw.Start();
        public void Pause() { sw.Stop(); IsPaused = true; }
        public void Resume() { IsPaused = false; sw.Start(); }
    }

    /// <summary>
    /// Nimmt einen Bildschirmbereich auf: Bilder per GDI → ffmpeg (stdin, H.264),
    /// System-Ton und Mikrofon per WASAPI → WAV; am Ende wird alles zu einer MP4 zusammengeführt.
    /// </summary>
    internal sealed class ScreenRecorder
    {
        readonly int x, y, w, h, fps;
        readonly string dir, videoPath;
        readonly RecordingClock clock = new();
        readonly List<AudioTrack> audio = new();
        readonly Queue<string> ffmpegLog = new();
        Process? ffmpeg;
        Thread? thread;
        volatile bool running;
        Exception? failure;

        public TimeSpan Elapsed => clock.Elapsed;
        public bool IsPaused => clock.IsPaused;
        public bool HasFailed => failure != null;
        public int Width => w;
        public int Height => h;

        /// <summary>Wird (auf einem Hintergrund-Thread) ausgelöst, wenn die Aufnahme abbricht.</summary>
        public event Action<Exception>? Failed;

        public ScreenRecorder(Rect physicalRegion)
        {
            x = (int)Math.Round(physicalRegion.X);
            y = (int)Math.Round(physicalRegion.Y);
            // H.264 / yuv420p braucht gerade Kantenlängen
            w = Math.Max(2, (int)Math.Round(physicalRegion.Width) & ~1);
            h = Math.Max(2, (int)Math.Round(physicalRegion.Height) & ~1);
            fps = Math.Clamp(App.Settings.VideoFps, 5, 60);
            dir = Path.Combine(Path.GetTempPath(), "SnipSnip", $"rec_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
            Directory.CreateDirectory(dir);
            videoPath = Path.Combine(dir, "video.mp4");
        }

        public void Start()
        {
            var s = App.Settings;
            var args = new List<string>
            {
                "-y", "-f", "rawvideo", "-pix_fmt", "bgr0", "-video_size", $"{w}x{h}", "-framerate", fps.ToString(), "-i", "-"
            };
            args.AddRange(VideoEncoder.Args(s.VideoCrf, realtime: true));
            args.AddRange(new[] { "-pix_fmt", "yuv420p", videoPath });
            ffmpeg = FFmpeg.Start(args, redirectStdin: true);
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await ffmpeg.StandardError.ReadLineAsync()) != null)
                    lock (ffmpegLog) { ffmpegLog.Enqueue(line); if (ffmpegLog.Count > 10) ffmpegLog.Dequeue(); }
            });
            _ = ffmpeg.StandardOutput.ReadToEndAsync();

            if (s.RecordSystemAudio) TryAddAudio(true);
            if (s.RecordMicrophone) TryAddAudio(false);

            running = true;
            clock.Start();
            foreach (var a in audio) a.Start();
            thread = new Thread(CaptureLoop) { IsBackground = true, Name = "SnipSnip.Recorder", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        void TryAddAudio(bool loopback)
        {
            try { audio.Add(new AudioTrack(loopback, Path.Combine(dir, loopback ? "system.wav" : "mic.wav"), clock)); }
            catch (Exception ex) { Debug.WriteLine($"Audio ({(loopback ? "System" : "Mikrofon")}) nicht verfügbar: {ex.Message}"); }
        }

        public void Pause() { if (!clock.IsPaused) clock.Pause(); }

        public void Resume() { if (clock.IsPaused) clock.Resume(); }

        // ------------------------------------------------------------------ Bild-Schleife

        void CaptureLoop()
        {
            Win.timeBeginPeriod(1);
            IntPtr screenDc = Gdi.GetDC(IntPtr.Zero);
            IntPtr memDc = Gdi.CreateCompatibleDC(screenDc);
            var bi = new Win.BITMAPINFO
            {
                biSize = Marshal.SizeOf<Win.BITMAPINFO>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32
            };
            IntPtr dib = Win.CreateDIBSection(memDc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = Gdi.SelectObject(memDc, dib);
            var buf = new byte[w * h * 4];
            var cursorCache = new Dictionary<IntPtr, (int X, int Y)>();
            bool showCursor = App.Settings.VideoShowCursor, clicks = App.Settings.VideoHighlightClicks;
            IntPtr clickPen = Win.CreatePen(0, Math.Max(3, h / 300), 0x0030C8FF); // BGR: Gelb-Orange
            IntPtr nullBrush = Win.GetStockObject(5);

            try
            {
                var stdin = ffmpeg!.StandardInput.BaseStream;
                double interval = 1.0 / fps;
                long n = 0;
                while (running)
                {
                    if (clock.IsPaused) { Thread.Sleep(10); continue; }
                    double due = n * interval, now = clock.Elapsed.TotalSeconds;
                    if (now < due)
                    {
                        int ms = (int)((due - now) * 1000);
                        Thread.Sleep(Math.Max(1, ms));
                        continue;
                    }

                    Gdi.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Gdi.SRCCOPY);
                    if (showCursor || clicks) DrawCursor(memDc, cursorCache, showCursor, clicks, clickPen, nullBrush);
                    Win.GdiFlush();
                    Marshal.Copy(bits, buf, 0, buf.Length);
                    stdin.Write(buf, 0, buf.Length);
                    n++;

                    // Hinkt die Aufnahme hinterher (z. B. weil der Encoder bremst), Bilder doppeln,
                    // damit Video- und Tonlänge exakt zusammenpassen.
                    int dup = 0;
                    while (running && !clock.IsPaused && clock.Elapsed.TotalSeconds >= (n + 1) * interval && dup++ < fps)
                    {
                        stdin.Write(buf, 0, buf.Length);
                        n++;
                    }
                }
                stdin.Flush();
            }
            catch (Exception ex)
            {
                if (running)
                {
                    failure = new Exception("Die Videoaufnahme wurde abgebrochen: " + ex.Message + "\n" + FfmpegTail(), ex);
                    running = false;
                    Failed?.Invoke(failure);
                }
            }
            finally
            {
                Gdi.SelectObject(memDc, old);
                Gdi.DeleteObject(dib);
                Gdi.DeleteObject(clickPen);
                Gdi.DeleteDC(memDc);
                Gdi.ReleaseDC(IntPtr.Zero, screenDc);
                Win.timeEndPeriod(1);
            }
        }

        void DrawCursor(IntPtr dc, Dictionary<IntPtr, (int X, int Y)> cache, bool cursor, bool clicks, IntPtr pen, IntPtr brush)
        {
            var ci = new Win.CURSORINFO { cbSize = Marshal.SizeOf<Win.CURSORINFO>() };
            if (!Win.GetCursorInfo(ref ci)) return;
            int cx = ci.ptScreenPos.X - x, cy = ci.ptScreenPos.Y - y;

            if (clicks && ((Win.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Win.GetAsyncKeyState(0x02) & 0x8000) != 0))
            {
                int r = Math.Max(14, h / 45);
                IntPtr op = Gdi.SelectObject(dc, pen), ob = Gdi.SelectObject(dc, brush);
                Win.Ellipse(dc, cx - r, cy - r, cx + r, cy + r);
                Gdi.SelectObject(dc, op);
                Gdi.SelectObject(dc, ob);
            }

            if (!cursor || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
            if (!cache.TryGetValue(ci.hCursor, out var hot))
            {
                if (Win.GetIconInfo(ci.hCursor, out var ii))
                {
                    hot = (ii.xHotspot, ii.yHotspot);
                    if (ii.hbmMask != IntPtr.Zero) Gdi.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) Gdi.DeleteObject(ii.hbmColor);
                }
                cache[ci.hCursor] = hot;
            }
            Win.DrawIconEx(dc, cx - hot.X, cy - hot.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
        }

        string FfmpegTail()
        {
            lock (ffmpegLog) return string.Join("\n", ffmpegLog.TakeLast(3));
        }

        // ------------------------------------------------------------------ Stoppen

        /// <summary>Beendet die Aufnahme und liefert den Pfad der fertigen MP4.</summary>
        public async Task<string> StopAsync()
        {
            running = false;
            await Task.Run(() => thread?.Join());
            var duration = clock.Elapsed;
            foreach (var a in audio) await a.StopAsync(duration);

            if (ffmpeg != null)
            {
                try { ffmpeg.StandardInput.Close(); } catch { }
                var exited = ffmpeg.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMinutes(2))) != exited)
                {
                    try { ffmpeg.Kill(true); } catch { }
                    throw new Exception("FFmpeg reagiert nicht mehr.");
                }
                if (ffmpeg.ExitCode != 0 || !File.Exists(videoPath))
                    throw failure ?? new Exception("Die Videodatei konnte nicht erstellt werden.\n" + FfmpegTail());
            }
            if (failure != null) throw failure;

            string final = Path.Combine(dir, $"SnipSnip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
            var tracks = audio.Where(a => a.HasData).ToList();
            if (tracks.Count == 0)
            {
                // nur noch "faststart" setzen (Stream-Copy, sehr schnell)
                await FFmpeg.RunAsync(new[] { "-i", videoPath, "-c", "copy", "-movflags", "+faststart", final });
            }
            else
            {
                var args = new List<string> { "-i", videoPath };
                foreach (var t in tracks) { args.Add("-i"); args.Add(t.Path); }
                if (tracks.Count == 1)
                    args.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
                else
                    args.AddRange(new[] { "-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[a]", "-map", "0:v", "-map", "[a]" });
                args.AddRange(new[] { "-c:v", "copy", "-c:a", "aac", "-b:a", "160k", "-shortest", "-movflags", "+faststart", final });
                await FFmpeg.RunAsync(args);
            }

            TryDelete(videoPath);
            foreach (var t in audio) TryDelete(t.Path);
            return final;
        }

        /// <summary>Bricht ab und löscht alle Zwischendateien.</summary>
        public async Task DiscardAsync()
        {
            running = false;
            await Task.Run(() => thread?.Join());
            foreach (var a in audio) await a.StopAsync(clock.Elapsed);
            try { ffmpeg?.Kill(true); } catch { }
            await Task.Delay(200);
            try { Directory.Delete(dir, true); } catch { }
        }

        static void TryDelete(string p)
        {
            try { File.Delete(p); } catch { }
        }

        /// <summary>Löscht alte Aufnahmen aus dem Temp-Ordner (älter als 2 Tage).</summary>
        public static void CleanupOldTemp()
        {
            try
            {
                var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "SnipSnip"));
                if (!root.Exists) return;
                foreach (var d in root.GetDirectories("rec_*").Concat(root.GetDirectories("export_*")))
                    if (d.CreationTime < DateTime.Now.AddDays(-2))
                        try { d.Delete(true); } catch { }
            }
            catch { }
        }
    }

    /// <summary>
    /// WASAPI-Aufnahme in eine WAV-Datei. Lücken (WASAPI-Loopback liefert bei Stille keine Daten)
    /// werden anhand der Aufnahme-Uhr mit Stille aufgefüllt, damit der Ton synchron bleibt.
    /// </summary>
    internal sealed class AudioTrack
    {
        readonly IWaveIn capture;
        readonly WaveFileWriter writer;
        readonly RecordingClock clock;
        readonly WaveFormat fmt;
        readonly object gate = new();
        readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long written;
        bool closed;

        public string Path { get; }
        public bool HasData { get; private set; }

        public AudioTrack(bool loopback, string path, RecordingClock clock)
        {
            Path = path;
            this.clock = clock;
            capture = loopback ? new WasapiLoopbackCapture() : new WasapiCapture(DefaultMic(), true, 50);
            fmt = capture.WaveFormat;
            writer = new WaveFileWriter(path, fmt);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, _) => stopped.TrySetResult();
        }

        static MMDevice DefaultMic()
        {
            using var en = new MMDeviceEnumerator();
            return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }

        public void Start() => capture.StartRecording();

        long BytesFor(TimeSpan t)
        {
            long b = (long)(t.TotalSeconds * fmt.AverageBytesPerSecond);
            return b - b % fmt.BlockAlign;
        }

        void WriteSilence(long bytes)
        {
            var zero = new byte[Math.Min(bytes, 1 << 16)];
            while (bytes > 0)
            {
                int n = (int)Math.Min(bytes, zero.Length);
                writer.Write(zero, 0, n);
                written += n;
                bytes -= n;
            }
        }

        void OnData(object? sender, WaveInEventArgs e)
        {
            lock (gate)
            {
                if (closed || clock.IsPaused || e.BytesRecorded == 0) return;
                long expectedEnd = BytesFor(clock.Elapsed);
                long gap = expectedEnd - (written + e.BytesRecorded);
                if (gap > fmt.AverageBytesPerSecond / 20) WriteSilence(gap - gap % fmt.BlockAlign);
                writer.Write(e.Buffer, 0, e.BytesRecorded);
                written += e.BytesRecorded;
                HasData = true;
            }
        }

        public async Task StopAsync(TimeSpan duration)
        {
            try { capture.StopRecording(); } catch { }
            await Task.WhenAny(stopped.Task, Task.Delay(2000));
            lock (gate)
            {
                if (closed) return;
                closed = true;
                long target = BytesFor(duration);
                if (HasData && target > written) WriteSilence(target - written);
                writer.Dispose();
            }
            capture.Dispose();
        }
    }

    /// <summary>Wählt den besten verfügbaren H.264-Encoder.</summary>
    internal static class VideoEncoder
    {
        static string? encoder;

        static string Detect()
        {
            if (encoder != null) return encoder;
            try
            {
                using var p = FFmpeg.Start(new[] { "-encoders" });
                _ = p.StandardError.ReadToEndAsync();
                string list = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                encoder = list.Contains(" libx264 ") ? "libx264"
                        : list.Contains(" h264_mf ") ? "h264_mf"
                        : "mpeg4";
            }
            catch { encoder = "libx264"; }
            return encoder;
        }

        public static IEnumerable<string> Args(int crf, bool realtime) => Detect() switch
        {
            "libx264" => new[] { "-c:v", "libx264", "-preset", realtime ? "veryfast" : "medium", "-crf", crf.ToString() },
            "h264_mf" => new[] { "-c:v", "h264_mf", "-b:v", crf <= 20 ? "12M" : crf <= 24 ? "8M" : "4M" },
            _ => new[] { "-c:v", "mpeg4", "-q:v", "3" }
        };
    }

    internal static class Win
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            public int bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize, flags;
            public IntPtr hCursor;
            public Native.POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot, yHotspot;
            public IntPtr hbmMask, hbmColor;
        }

        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("gdi32.dll")] public static extern IntPtr CreatePen(int style, int width, uint color);
        [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int obj);
        [DllImport("gdi32.dll")] public static extern bool Ellipse(IntPtr hdc, int l, int t, int r, int b);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO ii);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const int WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>Fenster aus Bildschirmaufnahmen ausblenden (ab Windows 10 2004).</summary>
        public static void ExcludeFromCapture(IntPtr hwnd)
        {
            try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); } catch { }
        }

        public static void AddExStyle(IntPtr hwnd, int style) =>
            SetWindowLong(hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) | style);
    }
}
