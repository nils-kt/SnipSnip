using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SnipSnip
{
    public enum EncoderKind { Nvenc, Amf, Qsv, Cpu }

    /// <summary>
    /// Wählt den H.264-Encoder: Hardware (NVIDIA NVENC → AMD AMF → Intel Quick Sync), sonst CPU (x264).
    /// Jeder Hardware-Encoder wird einmal mit einer kurzen Test-Kodierung geprüft – "im FFmpeg vorhanden"
    /// heißt noch nicht, dass Grafikkarte und Treiber ihn auch können. Fällt ein Encoder später aus,
    /// wird er für die laufende Sitzung gesperrt und die CPU übernimmt.
    /// </summary>
    internal static class VideoEncoder
    {
        static readonly object gate = new();
        static readonly Dictionary<EncoderKind, bool> probed = new();
        static readonly HashSet<EncoderKind> failed = new();
        static string? cpuCodec;

        static readonly EncoderKind[] HardwareOrder = { EncoderKind.Nvenc, EncoderKind.Amf, EncoderKind.Qsv };

        /// <summary>Zuletzt tatsächlich verwendeter Encoder (für die Anzeige).</summary>
        public static EncoderKind? LastUsed { get; set; }

        public static string Name(EncoderKind k) => k switch
        {
            EncoderKind.Nvenc => "NVIDIA NVENC",
            EncoderKind.Amf => "AMD AMF",
            EncoderKind.Qsv => "Intel Quick Sync",
            _ => CpuCodec == "libx264" ? "CPU (x264)" : "CPU"
        };

        /// <summary>Bester funktionierender Encoder. Kann beim ersten Aufruf ~0,5 s dauern (Test-Kodierungen).</summary>
        public static EncoderKind Best()
        {
            if (!App.Settings.HardwareEncoding) return EncoderKind.Cpu;
            foreach (var k in HardwareOrder)
            {
                lock (gate) if (failed.Contains(k)) continue;
                if (Probe(k)) return k;
            }
            return EncoderKind.Cpu;
        }

        /// <summary>Im Hintergrund vorab prüfen, damit Aufnahme und Export sofort starten.</summary>
        public static void WarmUp() => Task.Run(() =>
        {
            try { if (FFmpeg.IsAvailable) Best(); } catch { }
        });

        public static void MarkFailed(EncoderKind k)
        {
            if (k == EncoderKind.Cpu) return;
            lock (gate) failed.Add(k);
        }

        static bool Probe(EncoderKind k)
        {
            lock (gate)
            {
                if (probed.TryGetValue(k, out bool ok)) return ok;
                ok = false;
                try
                {
                    var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30", "-frames:v", "10" };
                    args.AddRange(Args(k, 23, realtime: false, probe: true));
                    args.AddRange(new[] { "-f", "null", "-" });
                    using var p = FFmpeg.Start(args);
                    _ = p.StandardOutput.ReadToEndAsync();
                    _ = p.StandardError.ReadToEndAsync();
                    ok = p.WaitForExit(8000) && p.ExitCode == 0;
                    if (!p.HasExited) try { p.Kill(true); } catch { }
                }
                catch (Exception ex) { Debug.WriteLine($"Encoder-Test {k}: {ex.Message}"); }
                probed[k] = ok;
                return ok;
            }
        }

        static string CpuCodec
        {
            get
            {
                if (cpuCodec != null) return cpuCodec;
                try
                {
                    using var p = FFmpeg.Start(new[] { "-encoders" });
                    _ = p.StandardError.ReadToEndAsync();
                    string list = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    cpuCodec = list.Contains(" libx264 ") ? "libx264" : list.Contains(" h264_mf ") ? "h264_mf" : "mpeg4";
                }
                catch { cpuCodec = "libx264"; }
                return cpuCodec;
            }
        }

        /// <summary>Encoder-Argumente inkl. Pixelformat. crf: 18 = sehr gut … 28 = klein.</summary>
        public static IEnumerable<string> Args(EncoderKind k, int crf, bool realtime, bool probe = false)
        {
            var a = new List<string>();
            switch (k)
            {
                case EncoderKind.Nvenc:
                    a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", realtime ? "p3" : "p5", "-tune", realtime ? "ll" : "hq",
                        "-rc", "vbr", "-cq", (crf + 1).ToString(), "-b:v", "0", "-pix_fmt", "nv12" });
                    if (!realtime) a.AddRange(new[] { "-spatial-aq", "1" });
                    break;
                case EncoderKind.Amf:
                    a.AddRange(new[] { "-c:v", "h264_amf", "-quality", realtime ? "speed" : "quality",
                        "-rc", "cqp", "-qp_i", (crf + 2).ToString(), "-qp_p", (crf + 2).ToString(), "-pix_fmt", "nv12" });
                    break;
                case EncoderKind.Qsv:
                    a.AddRange(new[] { "-c:v", "h264_qsv", "-preset", realtime ? "veryfast" : "slow",
                        "-global_quality", (crf + 2).ToString(), "-look_ahead", "0", "-pix_fmt", "nv12" });
                    break;
                default:
                    a.AddRange(CpuCodec switch
                    {
                        "libx264" => new[] { "-c:v", "libx264", "-preset", realtime ? "veryfast" : "fast", "-crf", crf.ToString() },
                        "h264_mf" => new[] { "-c:v", "h264_mf", "-b:v", crf <= 20 ? "12M" : crf <= 24 ? "8M" : "4M" },
                        _ => new[] { "-c:v", "mpeg4", "-q:v", "3" }
                    });
                    a.AddRange(new[] { "-pix_fmt", "yuv420p" });
                    break;
            }
            // Test-Schalter: Hardware-Ausfall simulieren, um den Rückfall auf die CPU zu prüfen
            if (!probe && k != EncoderKind.Cpu && Environment.GetEnvironmentVariable("SNIPSNIP_SIMULATE_HW_FAILURE") == "1")
                a.AddRange(new[] { "-simulated_hw_failure", "1" });
            return a;
        }
    }
}
