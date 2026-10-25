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
    /// Nimmt einen Bildschirmbereich als Studio-Projekt auf (wie Screen Studio):
    /// Bild ohne Cursor per GDI → FFmpeg (screen.mkv, absturzsicher), Cursor/Klicks/Tasten getrennt (events.jsonl),
    /// System-Ton und Mikrofon als eigene WAV-Spuren, optional Webcam. Alles wird alle 2 s auf die Platte geschrieben.
    /// </summary>
    internal sealed class ScreenRecorder
    {
        readonly int x, y, w, h, fps;
        readonly string dir;
        readonly RecordingClock clock = new();
        readonly List<AudioTrack> audio = new();
        readonly Queue<string> ffmpegLog = new();
        readonly RecordingMeta meta;
        readonly WebcamCapture? webcam;
        InputTracker? input;
        Process? ffmpeg;
        Thread? thread;
        volatile bool running;
        Exception? failure;
        readonly Stopwatch pauseWatch = new();
        System.Threading.Timer? flushTimer;

        public TimeSpan Elapsed => clock.Elapsed;
        public bool IsPaused => clock.IsPaused;
        public int Width => w;
        public int Height => h;
        public string ProjectDir => dir;

        /// <summary>Wird (auf einem Hintergrund-Thread) ausgelöst, wenn die Aufnahme abbricht.</summary>
        public event Action<Exception>? Failed;

        public ScreenRecorder(Rect physicalRegion, string projectDir, WebcamCapture? webcam)
        {
            x = (int)Math.Round(physicalRegion.X);
            y = (int)Math.Round(physicalRegion.Y);
            // H.264 / yuv420p braucht gerade Kantenlängen
            w = Math.Max(2, (int)Math.Round(physicalRegion.Width) & ~1);
            h = Math.Max(2, (int)Math.Round(physicalRegion.Height) & ~1);
            fps = Math.Clamp(App.Settings.VideoFps, 5, 60);
            dir = projectDir;
            this.webcam = webcam;
            meta = new RecordingMeta { Width = w, Height = h, Fps = fps, RegionX = x, RegionY = y, HasCursor = true };
        }

        public void Start()
        {
            var s = App.Settings;
            encoder = VideoEncoder.Best();
            StartFfmpeg(encoder);

            if (s.RecordSystemAudio) meta.HasSystemAudio = TryAddAudio(true);
            if (s.RecordMicrophone) meta.HasMic = TryAddAudio(false);
            if (webcam != null && webcam.StartEpoch > 0)
            {
                meta.Webcam = Path.GetFileName(webcam.Path);
                meta.WebcamStartEpoch = webcam.StartEpoch;
            }

            input = new InputTracker(clock, x, y, dir, s.RecordAllKeys);
            running = true;
            meta.StartEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            clock.Start();
            foreach (var a in audio) a.Start();
            input.Start();
            StudioProject.SaveMeta(dir, meta);

            thread = new Thread(CaptureLoop) { IsBackground = true, Name = "SnipSnip.Recorder", Priority = ThreadPriority.AboveNormal };
            thread.Start();
            flushTimer = new System.Threading.Timer(_ => Flush(), null, 2000, 2000);
        }

        EncoderKind encoder;
        bool switchedToCpu;

        /// <summary>Encoder-Prozess starten (screen.mkv wird dabei neu angelegt).</summary>
        void StartFfmpeg(EncoderKind kind)
        {
            var args = new List<string>
            {
                "-y", "-f", "rawvideo", "-pix_fmt", "bgr0", "-video_size", $"{w}x{h}", "-framerate", fps.ToString(), "-i", "-"
            };
            // hohe Qualität (wird später gezoomt) und häufige Keyframes für schnelles Springen im Editor
            args.AddRange(VideoEncoder.Args(kind, Math.Min(App.Settings.VideoCrf, 18), realtime: true));
            args.AddRange(new[] { "-g", fps.ToString(), Path.Combine(dir, "screen.mkv") });
            var proc = FFmpeg.Start(args, redirectStdin: true);
            ffmpeg = proc;
            VideoEncoder.LastUsed = kind;
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    lock (ffmpegLog) { ffmpegLog.Enqueue(line); if (ffmpegLog.Count > 10) ffmpegLog.Dequeue(); }
            });
            _ = proc.StandardOutput.ReadToEndAsync();
        }

        /// <summary>
        /// Hardware-Encoder ist ausgefallen: einmalig auf die CPU umschalten. Die Aufnahme läuft weiter;
        /// die fehlenden Bilder werden über die Aufholschleife ergänzt, damit Ton und Cursor synchron bleiben.
        /// </summary>
        bool TrySwitchToCpu()
        {
            if (encoder == EncoderKind.Cpu || switchedToCpu || !running) return false;
            switchedToCpu = true;
            var failedKind = encoder;
            VideoEncoder.MarkFailed(failedKind);
            try { ffmpeg?.Kill(true); } catch { }
            try { ffmpeg?.WaitForExit(2000); } catch { }
            encoder = EncoderKind.Cpu;
            StartFfmpeg(encoder);
            Application.Current?.Dispatcher.InvokeAsync(() =>
                Toast.Notify("Hardware-Kodierung nicht verfügbar", $"{VideoEncoder.Name(failedKind)} ist ausgefallen – die Aufnahme läuft per CPU weiter.", null, null, 6));
            return true;
        }

        bool TryAddAudio(bool loopback)
        {
            try
            {
                audio.Add(new AudioTrack(loopback, Path.Combine(dir, loopback ? "system.wav" : "mic.wav"), clock));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Audio ({(loopback ? "System" : "Mikrofon")}) nicht verfügbar: {ex.Message}");
                return false;
            }
        }

        /// <summary>Alle 2 s: Ton-Header und Metadaten aktualisieren (Absturzsicherung).</summary>
        void Flush()
        {
            try
            {
                foreach (var a in audio) a.Flush();
                lock (meta)
                {
                    meta.Duration = clock.Elapsed.TotalSeconds;
                    StudioProject.SaveMeta(dir, meta);
                }
            }
            catch { }
        }

        public void Pause()
        {
            if (clock.IsPaused) return;
            clock.Pause();
            pauseWatch.Restart();
        }

        public void Resume()
        {
            if (!clock.IsPaused) return;
            lock (meta) meta.Pauses.Add(new PauseInfo { At = clock.Elapsed.TotalSeconds, Duration = pauseWatch.Elapsed.TotalSeconds });
            clock.Resume();
        }

        // ------------------------------------------------------------------ Bild-Schleife (ohne Cursor)

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
                        Thread.Sleep(Math.Max(1, (int)((due - now) * 1000)));
                        continue;
                    }

                    Gdi.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Gdi.SRCCOPY);
                    Win.GdiFlush();
                    Marshal.Copy(bits, buf, 0, buf.Length);
                    try
                    {
                        stdin.Write(buf, 0, buf.Length);
                        n++;

                        // Hinkt die Aufnahme hinterher, Bilder doppeln, damit Bild, Ton und Cursor-Zeit exakt passen.
                        int dup = 0;
                        while (running && !clock.IsPaused && clock.Elapsed.TotalSeconds >= (n + 1) * interval && dup++ < fps)
                        {
                            stdin.Write(buf, 0, buf.Length);
                            n++;
                        }
                    }
                    catch (IOException) when (TrySwitchToCpu())
                    {
                        // neuer Encoder-Prozess: von vorn schreiben – die Aufholschleife ergänzt die fehlende Zeit
                        stdin = ffmpeg!.StandardInput.BaseStream;
                        n = 0;
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
                Gdi.DeleteDC(memDc);
                Gdi.ReleaseDC(IntPtr.Zero, screenDc);
                Win.timeEndPeriod(1);
            }
        }

        string FfmpegTail()
        {
            lock (ffmpegLog) return string.Join("\n", ffmpegLog.TakeLast(3));
        }

        // ------------------------------------------------------------------ Stoppen

        /// <summary>Beendet die Aufnahme und liefert den Projektordner.</summary>
        public async Task<string> StopAsync()
        {
            if (clock.IsPaused) Resume();
            running = false;
            flushTimer?.Dispose();
            await Task.Run(() => thread?.Join());
            var duration = clock.Elapsed;
            input?.Stop();
            foreach (var a in audio) await a.StopAsync(duration);
            if (webcam != null) await webcam.StopAsync();

            if (ffmpeg != null)
            {
                try { ffmpeg.StandardInput.Close(); } catch { }
                var exited = ffmpeg.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMinutes(2))) != exited)
                {
                    try { ffmpeg.Kill(true); } catch { }
                    throw new Exception("FFmpeg reagiert nicht mehr.");
                }
                if (ffmpeg.ExitCode != 0 || !File.Exists(Path.Combine(dir, "screen.mkv")))
                    throw failure ?? new Exception("Die Videodatei konnte nicht erstellt werden.\n" + FfmpegTail());
            }
            if (failure != null) throw failure;

            lock (meta)
            {
                meta.Duration = duration.TotalSeconds;
                meta.HasSystemAudio &= audio.Any(a => a.Path.EndsWith("system.wav") && a.HasData);
                meta.HasMic &= audio.Any(a => a.Path.EndsWith("mic.wav") && a.HasData);
                if (meta.Webcam != null && !File.Exists(Path.Combine(dir, meta.Webcam))) meta.Webcam = null;
                meta.Complete = true;
                StudioProject.SaveMeta(dir, meta);
            }
            return dir;
        }

        /// <summary>Bricht ab und löscht das Projekt.</summary>
        public async Task DiscardAsync()
        {
            running = false;
            flushTimer?.Dispose();
            await Task.Run(() => thread?.Join());
            try { input?.Stop(); } catch { }
            foreach (var a in audio) await a.StopAsync(clock.Elapsed);
            if (webcam != null) await webcam.StopAsync();
            try { ffmpeg?.Kill(true); } catch { }
            await Task.Delay(300);
            try { Directory.Delete(dir, true); } catch { }
        }

        /// <summary>Löscht alte Export-Zwischendateien aus dem Temp-Ordner (älter als 2 Tage).</summary>
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
            capture = loopback ? new WasapiLoopbackCapture() : new WasapiCapture(AudioDevices.Microphone(App.Settings.MicrophoneDevice), true, 50);
            fmt = capture.WaveFormat;
            writer = new WaveFileWriter(path, fmt);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, _) => stopped.TrySetResult();
        }

        public void Start() => capture.StartRecording();

        /// <summary>WAV-Header aktualisieren, damit die Datei nach einem Absturz lesbar bleibt.</summary>
        public void Flush()
        {
            lock (gate) { if (!closed) writer.Flush(); }
        }

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
