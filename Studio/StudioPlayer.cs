using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using SkiaSharp;

namespace SnipSnip
{
    /// <summary>
    /// Vorschau des Studios: rendert in einem Hintergrund-Thread (Wiedergabe oder Einzelbild beim Scrubben)
    /// und gibt den Ton synchron dazu aus. Die Zeit ist immer die Ausgabezeit (nach Schnitt/Tempo).
    /// </summary>
    public sealed class StudioPlayer : IDisposable
    {
        readonly StudioProject p;
        readonly Thread worker;
        readonly AutoResetEvent wake = new(false);
        readonly object gate = new();
        readonly Stopwatch clock = new();
        FrameDecoder? screen, cam;
        volatile bool running = true, playing;
        double requestT = 0, playStartOut;
        bool hasRequest = true;
        int pw, ph;
        byte[]? front;
        SeekableAudio? audio;
        IWavePlayer? waveOut;

        public StudioRenderer Renderer { get; }
        public WriteableBitmap? Bitmap { get; private set; }
        public double OutTime { get; private set; }
        public bool IsPlaying => playing;

        /// <summary>UI-Thread: neues Vorschaubild ist da (Bitmap wurde ggf. neu erzeugt).</summary>
        public event Action? FrameReady;
        public event Action<double>? TimeChanged;
        public event Action? PlaybackEnded;

        public StudioPlayer(StudioProject project)
        {
            p = project;
            Renderer = new StudioRenderer(p);
            worker = new Thread(Loop) { IsBackground = true, Name = "SnipSnip.Studio" };
            worker.Start();
            _ = PrepareAudioAsync();
        }

        /// <summary>Vorschaugröße abhängig vom Seitenverhältnis (max. ca. 1280 × 720).</summary>
        (int W, int H) PreviewSize()
        {
            var (w, h) = Renderer.OutputSize(720);
            double s = Math.Min(1, Math.Min(1280.0 / w, 1280.0 / h));
            return ((int)(w * s) & ~1, (int)(h * s) & ~1);
        }

        public void Seek(double outT)
        {
            outT = Math.Clamp(outT, 0, Math.Max(0, p.OutputDuration - 0.001));
            lock (gate)
            {
                requestT = outT;
                hasRequest = true;
                if (playing) { playStartOut = outT; clock.Restart(); }
            }
            OutTime = outT;
            wake.Set();
        }

        /// <summary>Aktuelles Bild neu zeichnen (nach Änderungen am Stil oder an der Bearbeitung).</summary>
        public void Refresh() => Seek(OutTime);

        public void Play()
        {
            if (playing) return;
            if (OutTime >= p.OutputDuration - 0.05) OutTime = 0;
            lock (gate)
            {
                playStartOut = OutTime;
                clock.Restart();
                playing = true;
            }
            audio?.SeekTo(p.SourceAt(OutTime));
            waveOut?.Play();
            wake.Set();
        }

        public void Pause()
        {
            playing = false;
            clock.Stop();
            waveOut?.Pause();
            Seek(OutTime);
        }

        public void Toggle()
        {
            if (playing) Pause(); else Play();
        }

        public bool Muted
        {
            get => audio?.Muted ?? false;
            set { if (audio != null) audio.Muted = value; }
        }

        // ------------------------------------------------------------------ Render-Schleife

        void Loop()
        {
            SKSurface? surface = null;
            try
            {
                while (running)
                {
                    double outT;
                    bool isPlaying;
                    lock (gate)
                    {
                        isPlaying = playing;
                        if (isPlaying) outT = playStartOut + clock.Elapsed.TotalSeconds;
                        else if (hasRequest) { outT = requestT; hasRequest = false; }
                        else outT = double.NaN;
                    }
                    if (double.IsNaN(outT)) { wake.WaitOne(250); continue; }

                    double dur = p.OutputDuration;
                    if (isPlaying && outT >= dur)
                    {
                        playing = false;
                        outT = Math.Max(0, dur - 0.001);
                        Application.Current?.Dispatcher.InvokeAsync(() => { waveOut?.Pause(); PlaybackEnded?.Invoke(); });
                    }

                    var frameWatch = Stopwatch.StartNew();
                    var (w, h) = PreviewSize();
                    if (surface == null || w != pw || h != ph)
                    {
                        surface?.Dispose();
                        surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                        pw = w;
                        ph = h;
                        front = new byte[w * h * 4];
                    }

                    double s = p.SourceAt(outT);
                    EnsureDecoders();
                    SKImage? img = null, camImg = null;
                    try { img = screen?.Get(s); } catch { }
                    if (cam != null)
                    {
                        try { camImg = cam.Get(p.Meta.WallAt(s) - p.Meta.WebcamStartEpoch); } catch { }
                    }

                    var c = surface.Canvas;
                    c.Clear(SKColors.Black);
                    try { Renderer.Render(c, w, h, s, outT, img, camImg, export: false, previewBlur: isPlaying); }
                    catch (Exception ex) { Debug.WriteLine("Render: " + ex); }
                    c.Flush();

                    byte[] buf;
                    lock (gate)
                    {
                        using var pm = surface.PeekPixels();
                        buf = front!;
                        var span = pm.GetPixelSpan();
                        for (int y = 0; y < h; y++) span.Slice(y * pm.RowBytes, w * 4).CopyTo(buf.AsSpan(y * w * 4, w * 4));
                    }
                    double shownT = outT;
                    Application.Current?.Dispatcher.Invoke(() => Present(buf, w, h, shownT));

                    if (isPlaying)
                    {
                        // Ton nachführen (bei Tempo ≠ 1 stumm)
                        if (audio != null)
                        {
                            audio.SpeedMute = Math.Abs(p.SpeedAt(s) - 1) > 0.01;
                            if (Math.Abs(audio.Position - s) > 0.15) audio.SeekTo(s);
                        }
                        int wait = 33 - (int)frameWatch.ElapsedMilliseconds;
                        if (wait > 0) wake.WaitOne(wait);
                    }
                }
            }
            finally
            {
                surface?.Dispose();
                screen?.Dispose();
                cam?.Dispose();
            }
        }

        void EnsureDecoders()
        {
            if (screen == null && File.Exists(p.ScreenFile))
            {
                var (w, h) = PreviewSize();
                // Für Zooms etwas mehr Auflösung als die Vorschau dekodieren
                int maxW = Math.Min(p.Meta.Width, Math.Max(w, 1280) * 3 / 2);
                screen = new FrameDecoder(p.ScreenFile, p.Meta.Width, p.Meta.Height, Math.Max(1, Math.Min(p.Meta.Fps, 30)), maxW);
            }
            if (cam == null && p.HasWebcam)
                cam = new FrameDecoder(p.WebcamFile!, 1280, 720, 30, 480);
        }

        void Present(byte[] buf, int w, int h, double t)
        {
            bool created = false;
            if (Bitmap == null || Bitmap.PixelWidth != w || Bitmap.PixelHeight != h)
            {
                Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                created = true;
            }
            Bitmap.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
            OutTime = t;
            if (created) FrameReady?.Invoke();
            TimeChanged?.Invoke(t);
        }

        // ------------------------------------------------------------------ Ton

        async Task PrepareAudioAsync()
        {
            try
            {
                var inputs = new System.Collections.Generic.List<string>();
                if (p.Meta.SourceFile != null) inputs.Add(p.Meta.SourceFile);
                if (p.SystemAudio != null && File.Exists(p.SystemAudio)) inputs.Add(p.SystemAudio);
                if (p.MicAudio != null && File.Exists(p.MicAudio)) inputs.Add(p.MicAudio);
                if (inputs.Count == 0) return;

                string wav = Path.Combine(p.Dir, "preview.wav");
                if (!File.Exists(wav))
                {
                    var args = new System.Collections.Generic.List<string>();
                    foreach (var i in inputs) { args.Add("-i"); args.Add(i); }
                    if (inputs.Count > 1) args.AddRange(new[] { "-filter_complex", $"amix=inputs={inputs.Count}:duration=longest:normalize=0" });
                    else args.Add("-vn");
                    args.AddRange(new[] { "-ar", "48000", "-ac", "2", wav });
                    await FFmpeg.RunAsync(args);
                }
                var reader = new WaveFileReader(wav);
                audio = new SeekableAudio(reader);
                var wo = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 120);
                wo.Init(audio.ToWaveProvider());
                waveOut = wo;
                if (playing) { audio.SeekTo(p.SourceAt(OutTime)); wo.Play(); }
            }
            catch (Exception ex) { Debug.WriteLine("Vorschau-Ton: " + ex.Message); }
        }

        public void Dispose()
        {
            running = false;
            wake.Set();
            worker.Join(1000);
            waveOut?.Stop();
            waveOut?.Dispose();
            audio?.Dispose();
            Renderer.Dispose();
        }
    }

    /// <summary>Tonquelle mit Springen und Stummschaltung (für die Vorschau).</summary>
    internal sealed class SeekableAudio : ISampleProvider, IDisposable
    {
        readonly WaveFileReader reader;
        readonly ISampleProvider samples;
        readonly object gate = new();
        public bool Muted { get; set; }
        public bool SpeedMute { get; set; }

        public SeekableAudio(WaveFileReader r)
        {
            reader = r;
            samples = r.ToSampleProvider();
        }

        public WaveFormat WaveFormat => samples.WaveFormat;

        public double Position
        {
            get { lock (gate) return reader.CurrentTime.TotalSeconds; }
        }

        public void SeekTo(double seconds)
        {
            lock (gate) reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, reader.TotalTime.TotalSeconds));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int n;
            lock (gate) n = samples.Read(buffer, offset, count);
            if (Muted || SpeedMute) Array.Clear(buffer, offset, n);
            if (n < count) { Array.Clear(buffer, offset + n, count - n); n = count; } // Stille statt Ende
            return n;
        }

        public void Dispose() => reader.Dispose();
    }
}
