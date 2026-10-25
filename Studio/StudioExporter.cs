using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace SnipSnip
{
    public sealed class StudioExportOptions
    {
        public bool Gif { get; set; }
        /// <summary>Kürzere Kante in Pixeln (720, 1080, 1440, 2160).</summary>
        public int ShortSide { get; set; } = 1080;
        public int Fps { get; set; } = 60;
        public int Crf { get; set; } = 20;
        public int GifFps { get; set; } = 15;
    }

    /// <summary>Rendert ein Studio-Projekt Bild für Bild und kodiert es als MP4 oder GIF.</summary>
    public static class StudioExporter
    {
        static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        public static async Task ExportAsync(StudioProject p, StudioExportOptions o, string output,
            Action<double, string> progress, CancellationToken ct)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "SnipSnip", $"export_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmp);
            try
            {
                double dur = p.OutputDuration;
                if (dur < 0.05) throw new InvalidOperationException("Das Video ist nach dem Schnitt leer.");

                string? audio = null;
                if (!o.Gif)
                {
                    progress(0, "Ton wird vorbereitet …");
                    audio = await BuildAudioAsync(p, tmp, dur, ct);
                }

                using var renderer = new StudioRenderer(p);
                var (W, H) = renderer.OutputSize(o.Gif ? Math.Min(o.ShortSide, 1080) : o.ShortSide);
                int fps = o.Gif ? Math.Max(o.GifFps, 10) : o.Fps;
                string videoOut = o.Gif ? Path.Combine(tmp, "gif_source.mp4") : output;

                List<string> BuildArgs(EncoderKind kind)
                {
                    var args = new List<string> { "-y", "-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", $"{W}x{H}", "-framerate", fps.ToString(), "-i", "-" };
                    if (audio != null) { args.Add("-i"); args.Add(audio); }
                    args.AddRange(new[] { "-map", "0:v" });
                    // kein -shortest: endet der Ton früher, liest FFmpeg sonst keine Bilder mehr und der Export hängt
                    if (audio != null) args.AddRange(new[] { "-map", "1:a", "-c:a", "aac", "-b:a", "192k" });
                    args.AddRange(VideoEncoder.Args(kind, o.Gif ? 14 : o.Crf, realtime: false));
                    args.AddRange(new[] { "-movflags", "+faststart", videoOut });
                    return args;
                }

                // Erst Hardware (falls vorhanden), bei einem Fehler automatisch noch einmal per CPU
                var encoder = await Task.Run(VideoEncoder.Best, ct);
                while (true)
                {
                    try
                    {
                        VideoEncoder.LastUsed = encoder;
                        await Task.Run(() => RenderFrames(p, renderer, W, H, fps, dur, BuildArgs(encoder), progress, ct), ct);
                        break;
                    }
                    catch (Exception) when (encoder != EncoderKind.Cpu && !ct.IsCancellationRequested)
                    {
                        VideoEncoder.MarkFailed(encoder);
                        progress(0, $"{VideoEncoder.Name(encoder)} nicht nutzbar – exportiere per CPU …");
                        encoder = EncoderKind.Cpu;
                        try { if (File.Exists(videoOut)) File.Delete(videoOut); } catch { }
                    }
                }

                if (o.Gif)
                {
                    progress(0.97, "GIF wird optimiert …");
                    string palette = Path.Combine(tmp, "palette.png");
                    await FFmpeg.RunAsync(new[] { "-i", videoOut, "-vf", $"fps={o.GifFps},palettegen=stats_mode=diff:max_colors=256", palette }, null, ct);
                    await FFmpeg.RunAsync(new[]
                    {
                        "-i", videoOut, "-i", palette, "-lavfi", $"fps={o.GifFps}[x];[x][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle",
                        "-loop", "0", output
                    }, null, ct);
                }
                progress(1, "Fertig");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        static void RenderFrames(StudioProject p, StudioRenderer renderer, int W, int H, int fps, double dur,
            List<string> args, Action<double, string> progress, CancellationToken ct)
        {
            using var ff = FFmpeg.Start(args, redirectStdin: true);
            var errTail = new Queue<string>();
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await ff.StandardError.ReadLineAsync()) != null)
                    lock (errTail) { errTail.Enqueue(line); if (errTail.Count > 6) errTail.Dequeue(); }
            });
            _ = ff.StandardOutput.ReadToEndAsync();

            using var screen = new FrameDecoder(p.ScreenFile, p.Meta.Width, p.Meta.Height, Math.Max(p.Meta.Fps, 1));
            using var cam = p.HasWebcam ? new FrameDecoder(p.WebcamFile!, 1280, 720, 30, 960) : null;

            var info = new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            // Jeder Worker bekommt einen eigenen Renderer (Caches sind nicht threadsicher) und eine eigene Fläche.
            var renderers = new StudioRenderer[workers];
            var surfaces = new SKSurface[workers];
            var buffers = new byte[workers][];
            for (int i = 0; i < workers; i++)
            {
                renderers[i] = i == 0 ? renderer : new StudioRenderer(p);
                surfaces[i] = SKSurface.Create(info);
                buffers[i] = new byte[W * H * 4];
            }
            var stdin = ff.StandardInput.BaseStream;
            int frames = Math.Max(1, (int)Math.Ceiling(dur * fps));
            var imgs = new SKImage?[workers];
            var cams = new SKImage?[workers];
            var srcT = new double[workers];

            try
            {
                for (int k0 = 0; k0 < frames; k0 += workers)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(workers, frames - k0);
                    // Dekodieren bleibt sequenziell (vorwärts lesen ist am schnellsten)
                    for (int i = 0; i < n; i++)
                    {
                        double outT = (k0 + i) / (double)fps;
                        srcT[i] = p.SourceAt(outT);
                        imgs[i] = screen.Get(srcT[i]);
                        cams[i] = cam?.Get(p.Meta.WallAt(srcT[i]) - p.Meta.WebcamStartEpoch);
                    }
                    System.Threading.Tasks.Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
                    {
                        var c = surfaces[i].Canvas;
                        c.Clear(SKColors.Black);
                        renderers[i].Render(c, W, H, srcT[i], (k0 + i) / (double)fps, imgs[i], cams[i], export: true);
                        c.Flush();
                        using var pm = surfaces[i].PeekPixels();
                        var span = pm.GetPixelSpan();
                        for (int y = 0; y < H; y++) span.Slice(y * pm.RowBytes, W * 4).CopyTo(buffers[i].AsSpan(y * W * 4, W * 4));
                    });
                    for (int i = 0; i < n; i++) stdin.Write(buffers[i], 0, buffers[i].Length);
                    progress(Math.Min(0.96, (k0 + n) / (double)frames * 0.96), $"Bild {k0 + n} von {frames}");
                }
                stdin.Close();
            }            catch (Exception) when (ct.IsCancellationRequested)
            {
                try { ff.Kill(true); } catch { }
                throw new OperationCanceledException(ct);
            }
            catch (IOException)
            {
                // FFmpeg hat aufgegeben – Fehlermeldung unten
            }

            for (int i = 0; i < workers; i++)
            {
                surfaces[i].Dispose();
                if (i > 0) renderers[i].Dispose();
            }
            ff.WaitForExit();
            if (ff.ExitCode != 0)
            {
                string tail;
                lock (errTail) tail = string.Join("\n", errTail);
                throw new Exception("Export fehlgeschlagen.\n" + tail);
            }
        }

        // ------------------------------------------------------------------ Ton

        /// <summary>Baut die fertige Tonspur (Schnitte, Tempo, Rauschunterdrückung, Musik, Klick-Sounds, Lautheit).</summary>
        static async Task<string?> BuildAudioAsync(StudioProject p, string tmp, double dur, CancellationToken ct)
        {
            var st = p.Edits.Style;
            var args = new List<string>();
            var f = new StringBuilder();
            var srcLabels = new List<string>();
            int idx = 0;

            void AddWav(string path)
            {
                args.AddRange(new[] { "-ignore_length", "1", "-i", path });
            }

            if (p.Meta.SourceFile != null)
            {
                var info = await FFmpeg.ProbeAsync(p.Meta.SourceFile);
                if (info.HasAudio)
                {
                    args.AddRange(new[] { "-i", p.Meta.SourceFile });
                    f.Append($"[{idx}:a]aresample=48000,volume={N(st.SystemVolume)}[a{idx}];");
                    srcLabels.Add($"[a{idx}]");
                    idx++;
                }
            }
            if (p.SystemAudio != null && File.Exists(p.SystemAudio) && st.SystemVolume > 0.001)
            {
                AddWav(p.SystemAudio);
                f.Append($"[{idx}:a]aresample=48000,volume={N(st.SystemVolume)}[a{idx}];");
                srcLabels.Add($"[a{idx}]");
                idx++;
            }
            if (p.MicAudio != null && File.Exists(p.MicAudio) && st.MicVolume > 0.001)
            {
                AddWav(p.MicAudio);
                string noise = st.Noise switch
                {
                    NoiseMode.Standard => "highpass=f=80,afftdn=nr=12:nf=-30,",
                    NoiseMode.Podcast => "highpass=f=90,afftdn=nr=18:nf=-28,acompressor=threshold=-20dB:ratio=3:attack=10:release=200,equalizer=f=3000:t=q:w=1:g=2,",
                    _ => ""
                };
                f.Append($"[{idx}:a]aresample=48000,{noise}volume={N(st.MicVolume)}[a{idx}];");
                srcLabels.Add($"[a{idx}]");
                idx++;
            }

            string? clicks = st.ClickSound && p.Input.HasCursor ? WriteClickTrack(p, tmp, dur) : null;
            bool music = st.MusicPath != null && File.Exists(st.MusicPath) && st.MusicVolume > 0.001;
            if (srcLabels.Count == 0 && clicks == null && !music) return null;

            string edited;
            if (srcLabels.Count == 0)
            {
                f.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={N(dur)}[ed];");
                edited = "[ed]";
            }
            else
            {
                if (srcLabels.Count > 1) f.Append(string.Concat(srcLabels) + $"amix=inputs={srcLabels.Count}:duration=longest:normalize=0[src];");
                else f.Append($"{srcLabels[0]}anull[src];");

                var kept = p.Kept.ToList();
                f.Append($"[src]asplit={kept.Count}" + string.Concat(kept.Select((_, i) => $"[s{i}]")) + ";");
                for (int i = 0; i < kept.Count; i++)
                {
                    var c = kept[i];
                    string tempo = Math.Abs(c.Speed - 1) > 0.01 ? $",atempo={N(Math.Clamp(c.Speed, 0.5, 100))}" : "";
                    f.Append($"[s{i}]atrim=start={N(c.Start)}:end={N(c.End)},asetpts=PTS-STARTPTS{tempo}[e{i}];");
                }
                f.Append(string.Concat(kept.Select((_, i) => $"[e{i}]")) + $"concat=n={kept.Count}:v=0:a=1[ed];");
                edited = "[ed]";
            }

            // Ducking: Musik weicht der Stimme bzw. dem Ton aus – dazu die bearbeitete Spur als Steuersignal abzweigen
            bool duck = music && st.MusicDucking && srcLabels.Count > 0;
            if (duck)
            {
                f.Append($"{edited}asplit=2[edm][eds];");
                edited = "[edm]";
            }
            var mixLabels = new List<string> { edited };
            if (clicks != null)
            {
                args.AddRange(new[] { "-i", clicks });
                f.Append($"[{idx}:a]aresample=48000,volume=0.8[clk];");
                mixLabels.Add("[clk]");
                idx++;
            }
            if (music)
            {
                args.AddRange(new[] { "-stream_loop", "-1", "-i", st.MusicPath! });
                double fade = Math.Min(2, dur / 3);
                f.Append($"[{idx}:a]aresample=48000,atrim=duration={N(dur)},volume={N(st.MusicVolume)},afade=t=out:st={N(Math.Max(0, dur - fade))}:d={N(fade)}" + (duck ? "[mraw];[mraw][eds]sidechaincompress=threshold=0.02:ratio=8:attack=40:release=600:makeup=1[mus];" : "[mus];"));
                mixLabels.Add("[mus]");
                idx++;
            }

            string final = mixLabels.Count > 1
                ? string.Concat(mixLabels) + $"amix=inputs={mixLabels.Count}:duration=first:normalize=0"
                : $"{edited}anull";
            string fades = (st.FadeIn > 0.01 ? $",afade=t=in:d={N(Math.Min(st.FadeIn, dur / 2))}" : "")
                         + (st.FadeOut > 0.01 ? $",afade=t=out:st={N(Math.Max(0, dur - st.FadeOut))}:d={N(Math.Min(st.FadeOut, dur / 2))}" : "");
            // loudnorm verschiebt die Zeitstempel → neu zählen; apad + atrim: Ton immer exakt so lang wie das Video
            f.Append(final + (st.Normalize ? ",loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000,asetpts=N/SR/TB" : "") + $",apad,atrim=duration={N(dur)}{fades}[out]");

            string outPath = Path.Combine(tmp, "audio.wav");
            args.AddRange(new[] { "-filter_complex", f.ToString(), "-map", "[out]", "-ar", "48000", "-ac", "2", outPath });
            await FFmpeg.RunAsync(args, null, ct);
            return outPath;
        }

        /// <summary>Erzeugt eine Spur mit dezenten Klick-Geräuschen an den Klick-Zeitpunkten des fertigen Videos.</summary>
        static string WriteClickTrack(StudioProject p, string tmp, double dur)
        {
            const int Rate = 48000;
            var samples = new float[(int)(dur * Rate) + Rate];
            var click = SynthClick(Rate);
            foreach (var t in p.Input.Clicks)
            {
                var o = p.OutputAt(t);
                if (o == null) continue;
                int start = (int)(o.Value * Rate);
                for (int i = 0; i < click.Length && start + i < samples.Length; i++) samples[start + i] += click[i];
            }
            string path = Path.Combine(tmp, "clicks.wav");
            using var w = new NAudio.Wave.WaveFileWriter(path, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
            w.WriteSamples(samples, 0, samples.Length);
            return path;
        }

        static float[] SynthClick(int rate)
        {
            int n = rate * 35 / 1000;
            var s = new float[n];
            var rng = new Random(7);
            double lp = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                double env = Math.Exp(-t * 180);
                double noise = rng.NextDouble() * 2 - 1;
                lp += (noise - lp) * 0.35; // weicher Tiefpass
                double tone = Math.Sin(2 * Math.PI * 1900 * t) * Math.Exp(-t * 260);
                s[i] = (float)((lp * 0.55 + tone * 0.45) * env * 0.5);
            }
            return s;
        }
    }
}
