using System;
using System.Collections.Generic;
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
        public void Refresh()
        {
            Seek(OutTime);
            _ = SyncMusicAsync();
        }

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

        /// <summary>
        /// Jede Tonspur einmal als 48-kHz-Stereo-WAV vorbereiten (zwischengespeichert im Projektordner);
        /// gemischt wird live, damit Lautstärke, Stumm und Blenden sofort hörbar sind.
        /// </summary>
        async Task PrepareAudioAsync()
        {
            try
            {
                var a = new SeekableAudio(p);
                string? sys = p.Meta.SourceFile ?? (p.SystemAudio != null && File.Exists(p.SystemAudio) ? p.SystemAudio : null);
                if (sys != null) await AddPreviewTrackAsync(a, AudioTrackId.System, sys, "preview_system.wav");
                if (p.MicAudio != null && File.Exists(p.MicAudio)) await AddPreviewTrackAsync(a, AudioTrackId.Mic, p.MicAudio, "preview_mic.wav");
                audio = a;
                await SyncMusicAsync();
                var wo = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 120);
                wo.Init(a.ToWaveProvider());
                waveOut = wo;
                if (playing) { a.SeekTo(p.SourceAt(OutTime)); wo.Play(); }
            }
            catch (Exception ex) { Debug.WriteLine("Vorschau-Ton: " + ex.Message); }
        }

        async Task AddPreviewTrackAsync(SeekableAudio a, AudioTrackId id, string source, string name)
        {
            try
            {
                string wav = await PreviewWavAsync(source, Path.Combine(p.Dir, name));
                a.Add(id, wav);
            }
            catch (Exception ex) { Debug.WriteLine($"Vorschau-Ton {id}: {ex.Message}"); }
        }

        async Task<string> PreviewWavAsync(string source, string wav)
        {
            if (File.Exists(wav)) return wav;
            string tmp = wav + ".tmp.wav";
            var args = new List<string>();
            if (source.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-ignore_length", "1" });
            args.AddRange(new[] { "-y", "-i", source, "-vn", "-ar", "48000", "-ac", "2", tmp });
            await FFmpeg.RunAsync(args);
            File.Move(tmp, wav, true);
            return wav;
        }

        string? loadingMusic;

        /// <summary>Hintergrundmusik der Vorschau an den Stil anpassen (gewählt, gewechselt, entfernt).</summary>
        async Task SyncMusicAsync()
        {
            var a = audio;
            if (a == null) return;
            string? path = p.Edits.Style.MusicPath;
            if (path != null && !File.Exists(path)) path = null;
            if (path == a.MusicSource || path == loadingMusic) return;
            if (path == null) { a.SetMusic(null, null); return; }
            loadingMusic = path;
            try
            {
                // pro Musikdatei eine eigene Vorschau (Name aus dem Pfad), alte aufräumen
                string name = $"preview_music_{(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path):x8}.wav";
                foreach (var old in Directory.GetFiles(p.Dir, "preview_music_*.wav"))
                    if (!Path.GetFileName(old).Equals(name, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
                string wav = await PreviewWavAsync(path, Path.Combine(p.Dir, name));
                if (p.Edits.Style.MusicPath == path) a.SetMusic(path, wav);
            }
            catch (Exception ex) { Debug.WriteLine("Vorschau-Musik: " + ex.Message); }
            finally { if (loadingMusic == path) loadingMusic = null; }
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

    /// <summary>
    /// Ton der Vorschau: mischt die Spuren live (Lautstärke, stumm, Ein-/Ausblenden, Musik mit einfachem Ducking,
    /// Klick-Geräusche). Die Position ist Quellzeit; Musik läuft wie im Export in Ausgabezeit und in Schleife.
    /// </summary>
    internal sealed class SeekableAudio : ISampleProvider, IDisposable
    {
        sealed class Source
        {
            public required AudioTrackId Id;
            public required WaveFileReader Reader;
            public required ISampleProvider Samples;
        }

        const int Rate = 48000, Channels = 2;
        readonly List<Source> sources = new();
        Source? music;
        readonly object gate = new();
        readonly StudioProject project;
        List<(double T, float[] Samples)>? clicks;
        float[] tmp = Array.Empty<float>();
        double pos, duck = 1;
        public bool Muted { get; set; }
        public bool SpeedMute { get; set; }
        /// <summary>Datei der geladenen Musik (null = keine).</summary>
        public string? MusicSource { get; private set; }

        public SeekableAudio(StudioProject project) => this.project = project;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);

        static Source Open(AudioTrackId id, string wav)
        {
            var r = new WaveFileReader(wav);
            ISampleProvider s = r.ToSampleProvider();
            if (s.WaveFormat.Channels == 1) s = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(s);
            return new Source { Id = id, Reader = r, Samples = s };
        }

        public void Add(AudioTrackId id, string wav)
        {
            var src = Open(id, wav);
            lock (gate)
            {
                src.Reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(pos, src.Reader.TotalTime.TotalSeconds));
                sources.Add(src);
            }
        }

        public void SetMusic(string? source, string? wav)
        {
            var src = wav != null ? Open(AudioTrackId.Music, wav) : null;
            lock (gate)
            {
                music?.Reader.Dispose();
                music = src;
                MusicSource = source;
            }
        }

        public double Position
        {
            get { lock (gate) return pos; }
        }

        public void SeekTo(double seconds)
        {
            lock (gate)
            {
                pos = Math.Max(0, seconds);
                foreach (var s in sources) s.Reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(pos, s.Reader.TotalTime.TotalSeconds));
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            count -= count % Channels;
            Array.Clear(buffer, offset, count);
            int frames = count / Channels;
            if (tmp.Length < count) tmp = new float[count];
            lock (gate)
            {
                double t0 = pos, t1 = pos + frames / (double)Rate;
                try
                {
                    var ed = project.Edits;
                    double outDur = project.OutputDuration;
                    double o0 = project.OutputAt(t0) ?? -1, o1 = project.OutputAt(t1) ?? o0;
                    foreach (var s in sources)
                    {
                        // immer lesen, damit die Spur ihre Position behält
                        int n = s.Samples.Read(tmp, 0, count);
                        if (Muted || SpeedMute || n <= 0) continue;
                        double g = ed.Gain(s.Id);
                        if (g < 0.001) continue;
                        Mix(buffer, offset, n, g * Fade(ed, s.Id, o0, outDur), g * Fade(ed, s.Id, o1, outDur));
                    }
                    if (music != null && o0 >= 0 && !Muted) MixMusic(buffer, offset, count, o0, o1, outDur, t0);
                    if (!Muted && !SpeedMute) MixClicks(buffer, offset, count, t0);
                }
                catch (Exception ex) { Debug.WriteLine("Vorschau-Mix: " + ex.Message); }
                pos = t1;
            }
            for (int i = offset; i < offset + count; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            return count;
        }

        static double Fade(ProjectEdits ed, AudioTrackId id, double outT, double outDur) => outT < 0 ? 1 : ed.FadeAt(id, outT, outDur);

        /// <summary>tmp mit linear übergehender Verstärkung g0 → g1 dazumischen.</summary>
        void Mix(float[] buffer, int offset, int n, double g0, double g1)
        {
            int frames = n / Channels;
            for (int f = 0; f < frames; f++)
            {
                float g = (float)(g0 + (g1 - g0) * f / Math.Max(1, frames));
                for (int c = 0; c < Channels; c++) buffer[offset + f * Channels + c] += tmp[f * Channels + c] * g;
            }
        }

        void MixMusic(float[] buffer, int offset, int count, double o0, double o1, double outDur, double srcT)
        {
            var ed = project.Edits;
            double g = ed.Gain(AudioTrackId.Music);
            double len = music!.Reader.TotalTime.TotalSeconds;
            if (g < 0.001 || len < 0.1 || o0 >= outDur) return;
            // Musik in Ausgabezeit; bei Sprüngen (Schnitt, Spulen) nachführen
            double want = o0 % len;
            if (Math.Abs(music.Reader.CurrentTime.TotalSeconds - want) > 0.08) music.Reader.CurrentTime = TimeSpan.FromSeconds(want);
            int n = music.Samples.Read(tmp, 0, count);
            if (n < count)
            {
                // Ende der Datei: von vorn weiter
                music.Reader.CurrentTime = TimeSpan.Zero;
                int m = music.Samples.Read(tmp, n, count - n);
                n += Math.Max(0, m);
            }
            // einfaches Ducking: bei Sprache im Mikrofon leiser (wie im Export, nur gröber)
            double target = 1;
            if (ed.Style.MusicDucking && ed.Gain(AudioTrackId.Mic) > 0.001)
            {
                var sp = project.Speech;
                int k = (int)(srcT * 10);
                if (k >= 0 && k < sp.Length && sp[k] > 0.5) target = 0.3;
            }
            double d0 = duck;
            duck += (target - duck) * (target < duck ? 0.5 : 0.15);
            Mix(buffer, offset, n, g * d0 * ed.FadeAt(AudioTrackId.Music, o0, outDur), g * duck * ed.FadeAt(AudioTrackId.Music, o1, outDur));
        }

        /// <summary>Klick-Geräusche wie im Export dazumischen (Zeit = Aufnahmezeit).</summary>
        void MixClicks(float[] buffer, int offset, int n, double t0)
        {
            var st = project.Edits.Style;
            if (!st.ClickSound || n <= 0) return;
            clicks ??= ClickSound.Events(project.Input);
            int frames = n / Channels;
            double t1 = t0 + frames / (double)Rate;
            float vol = (float)Math.Clamp(st.ClickVolume, 0, 1.5);
            foreach (var (t, snap) in clicks)
            {
                double len = snap.Length / (double)ClickSound.Rate;
                if (t + len < t0 || t >= t1) continue;
                for (int fIdx = 0; fIdx < frames; fIdx++)
                {
                    double k = ((t0 + fIdx / (double)Rate) - t) * ClickSound.Rate;
                    if (k < 0) continue;
                    int ki = (int)k;
                    if (ki >= snap.Length) break;
                    float v = snap[ki] * vol;
                    for (int c = 0; c < Channels; c++) buffer[offset + fIdx * Channels + c] += v;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                foreach (var s in sources) s.Reader.Dispose();
                music?.Reader.Dispose();
            }
        }
    }
}