using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnipSnip
{
    // ======================================================================= Aufnahme-Metadaten

    public sealed class PauseInfo
    {
        /// <summary>Aufnahmezeit (ohne Pausen), an der pausiert wurde.</summary>
        public double At { get; set; }
        public double Duration { get; set; }
    }

    public sealed class RecordingMeta
    {
        public int Version { get; set; } = 1;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }
        public int RegionX { get; set; }
        public int RegionY { get; set; }
        public double Duration { get; set; }
        /// <summary>Unix-Sekunden, als die Aufnahme-Uhr gestartet wurde.</summary>
        public double StartEpoch { get; set; }
        public List<PauseInfo> Pauses { get; set; } = new();
        public bool HasCursor { get; set; }
        public bool HasSystemAudio { get; set; }
        public bool HasMic { get; set; }
        public string? Webcam { get; set; }
        public double WebcamStartEpoch { get; set; }
        public bool Complete { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
        /// <summary>Importiertes Video statt eigener Aufnahme.</summary>
        public string? SourceFile { get; set; }

        /// <summary>Wanduhrzeit (Unix-Sekunden) zur Aufnahmezeit t – berücksichtigt Pausen.</summary>
        public double WallAt(double t) => StartEpoch + t + Pauses.Where(p => p.At <= t).Sum(p => p.Duration);
    }

    // ======================================================================= Bearbeitung (project.json)

    public enum BackgroundKind { Gradient, Solid, Image, Wallpaper }
    public enum ZoomType { Classic, Loupe }
    public enum ClickEffect { None, Circle, Ripple }
    public enum CamLayout { ScreenOnly, Overlay, Split, CameraOnly }
    public enum CamCorner { BottomRight, BottomLeft, TopRight, TopLeft }
    public enum CamShape { Circle, Rounded, Square }
    public enum MaskKind { Blur, Pixelate, Fill, Highlight }
    public enum NoiseMode { Off, Standard, Podcast }

    public sealed class StudioStyle
    {
        // Hintergrund
        public BackgroundKind Background { get; set; } = BackgroundKind.Wallpaper;
        public int Preset { get; set; } = 0;
        public string SolidColor { get; set; } = "#FF1E1E24";
        public string? ImagePath { get; set; }
        public double BackgroundBlur { get; set; }

        // Rahmen
        /// <summary>Abstand in % der kürzeren Ausgabekante.</summary>
        public double Padding { get; set; } = 8;
        public double Inset { get; set; }
        public double Radius { get; set; } = 14;
        public double ShadowOpacity { get; set; } = 0.55;
        public double ShadowBlur { get; set; } = 40;
        public double ShadowDistance { get; set; } = 18;
        public double ShadowAngle { get; set; } = 90;
        /// <summary>0 = automatisch (Seitenverhältnis der Aufnahme + Abstand).</summary>
        public string Aspect { get; set; } = "auto";

        // Kamera / Zoom
        public bool AutoZoom { get; set; } = true;
        public double ZoomLevel { get; set; } = 2.0;
        public double ZoomSpeed { get; set; } = 1.0;
        public bool MotionBlur { get; set; } = true;
        public double MotionBlurAmount { get; set; } = 0.35;

        // Cursor
        public bool ShowCursor { get; set; } = true;
        public double CursorSize { get; set; } = 1.5;
        public double CursorSmoothing { get; set; } = 0.6;
        public bool HighResCursor { get; set; } = true;
        public bool HideIdleCursor { get; set; } = true;
        public double IdleSeconds { get; set; } = 2.5;
        public ClickEffect ClickEffect { get; set; } = ClickEffect.Ripple;
        public bool ClickSound { get; set; }
        public bool LoopCursor { get; set; }

        // Webcam
        public CamLayout Layout { get; set; } = CamLayout.Overlay;
        public CamCorner CamCorner { get; set; } = CamCorner.BottomRight;
        public CamShape CamShape { get; set; } = CamShape.Rounded;
        public double CamSize { get; set; } = 22;
        public bool CamMirror { get; set; } = true;
        public bool CamSmartPosition { get; set; } = true;
        public bool CamHideOnSilence { get; set; }
        public bool CamShrinkOnZoom { get; set; } = true;
        public double CamBrightness { get; set; }
        public double CamContrast { get; set; }
        public double CamSaturation { get; set; }
        public double CamWarmth { get; set; }
        public bool CamSharpen { get; set; }

        // Tastatur
        public bool ShowKeys { get; set; } = true;
        public bool SpeedUpTyping { get; set; }

        // Ton
        public double SystemVolume { get; set; } = 1.0;
        public double MicVolume { get; set; } = 1.0;
        public NoiseMode Noise { get; set; } = NoiseMode.Off;
        public bool Normalize { get; set; } = true;
        public string? MusicPath { get; set; }
        public double MusicVolume { get; set; } = 0.15;

        public StudioStyle Clone() => JsonSerializer.Deserialize<StudioStyle>(JsonSerializer.Serialize(this, AppSettings.Json), AppSettings.Json)!;
    }

    public sealed class ClipSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public bool Removed { get; set; }
        public double Speed { get; set; } = 1;
        /// <summary>Automatisch erzeugte Beschleunigung (Tipp-Phase).</summary>
        public bool AutoSpeed { get; set; }
        public ClipSegment Clone() => (ClipSegment)MemberwiseClone();
    }

    public sealed class ZoomSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public double Level { get; set; } = 2;
        public ZoomType Type { get; set; } = ZoomType.Classic;
        /// <summary>true = folgt dem Cursor; false = fester Punkt (FocusX/Y relativ 0..1).</summary>
        public bool FollowCursor { get; set; } = true;
        public double FocusX { get; set; } = 0.5;
        public double FocusY { get; set; } = 0.5;
        public bool Auto { get; set; }
        public ZoomSegment Clone() => (ZoomSegment)MemberwiseClone();
    }

    public sealed class MaskSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public MaskKind Kind { get; set; } = MaskKind.Blur;
        /// <summary>Rechteck relativ zur Aufnahme (0..1).</summary>
        public double X { get; set; } = 0.35;
        public double Y { get; set; } = 0.35;
        public double W { get; set; } = 0.3;
        public double H { get; set; } = 0.12;
        public MaskSegment Clone() => (MaskSegment)MemberwiseClone();
    }

    public sealed class LayoutSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public CamLayout Layout { get; set; } = CamLayout.CameraOnly;
        public LayoutSegment Clone() => (LayoutSegment)MemberwiseClone();
    }

    public sealed class ProjectEdits
    {
        public StudioStyle Style { get; set; } = new();
        public List<ClipSegment> Clips { get; set; } = new();
        public List<ZoomSegment> Zooms { get; set; } = new();
        public List<MaskSegment> Masks { get; set; } = new();
        public List<LayoutSegment> Layouts { get; set; } = new();
        /// <summary>Zuschnitt relativ (0..1).</summary>
        public double CropX { get; set; }
        public double CropY { get; set; }
        public double CropW { get; set; } = 1;
        public double CropH { get; set; } = 1;

        public ProjectEdits Clone() => JsonSerializer.Deserialize<ProjectEdits>(JsonSerializer.Serialize(this, AppSettings.Json), AppSettings.Json)!;
    }

    // ======================================================================= Projekt

    /// <summary>Ein Studio-Projekt: Ordner mit Aufnahme-Dateien, Eingaben und Bearbeitung.</summary>
    public sealed class StudioProject
    {
        public string Dir { get; }
        public RecordingMeta Meta { get; private set; } = new();
        public ProjectEdits Edits { get; set; } = new();
        public InputData Input { get; private set; } = InputData.Empty;

        public string Name => System.IO.Path.GetFileName(Dir);
        public string ScreenFile => Meta.SourceFile ?? System.IO.Path.Combine(Dir, "screen.mkv");
        public string? SystemAudio => Meta.HasSystemAudio ? System.IO.Path.Combine(Dir, "system.wav") : null;
        public string? MicAudio => Meta.HasMic ? System.IO.Path.Combine(Dir, "mic.wav") : null;
        public string? WebcamFile => Meta.Webcam != null ? System.IO.Path.Combine(Dir, Meta.Webcam) : null;
        public bool HasWebcam => WebcamFile != null && File.Exists(WebcamFile);

        StudioProject(string dir) => Dir = dir;

        public static string ProjectsRoot =>
            System.IO.Path.Combine(App.Settings.VideoFolder, "Projekte");

        public static string NewDir()
        {
            // Eindeutig, auch in der doppelten Stunde bei der Zeitumstellung: nie einen vorhandenen Ordner wiederverwenden
            string baseName = System.IO.Path.Combine(ProjectsRoot, $"Aufnahme_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
            string d = baseName;
            for (int i = 2; Directory.Exists(d); i++) d = $"{baseName}_{i}";
            Directory.CreateDirectory(d);
            return d;
        }

        public static StudioProject Load(string dir)
        {
            var p = new StudioProject(dir);
            p.Meta = JsonSerializer.Deserialize<RecordingMeta>(File.ReadAllText(System.IO.Path.Combine(dir, "meta.json")), AppSettings.Json)!;
            string edits = System.IO.Path.Combine(dir, "project.json");
            if (File.Exists(edits))
            {
                try { p.Edits = JsonSerializer.Deserialize<ProjectEdits>(File.ReadAllText(edits), AppSettings.Json) ?? new(); }
                catch { p.Edits = new(); }
            }
            p.Input = InputData.Load(dir, p.Meta);
            if (p.Edits.Clips.Count == 0) p.InitEdits();
            return p;
        }

        /// <summary>Importiert ein vorhandenes Video als Projekt (ohne Cursor-Daten).</summary>
        public static StudioProject Import(string videoFile, MediaInfo info)
        {
            string baseDir = System.IO.Path.Combine(ProjectsRoot, "Import_" + System.IO.Path.GetFileNameWithoutExtension(videoFile) + $"_{DateTime.Now:yyyyMMdd_HHmmss}");
            string dir = baseDir;
            for (int i = 2; Directory.Exists(dir); i++) dir = $"{baseDir}_{i}";
            Directory.CreateDirectory(dir);
            var meta = new RecordingMeta
            {
                Width = info.Width,
                Height = info.Height,
                Fps = 30,
                Duration = info.Duration.TotalSeconds,
                Complete = true,
                SourceFile = System.IO.Path.GetFullPath(videoFile),
                HasCursor = false
            };
            SaveMeta(dir, meta);
            var p = Load(dir);
            p.Edits.Style.AutoZoom = false;
            p.Edits.Zooms.Clear();
            p.Save();
            return p;
        }

        public static void SaveMeta(string dir, RecordingMeta meta) =>
            File.WriteAllText(System.IO.Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta, AppSettings.Json));

        public void Save() =>
            File.WriteAllText(System.IO.Path.Combine(Dir, "project.json"), JsonSerializer.Serialize(Edits, AppSettings.Json));

        /// <summary>Erst-Bearbeitung: Standard-Stil, ein Clip, automatische Zooms und Tipp-Beschleunigung.</summary>
        public void InitEdits()
        {
            Edits = new ProjectEdits
            {
                Style = App.Settings.StudioDefaultStyle?.Clone() ?? new StudioStyle(),
                Clips = new List<ClipSegment> { new() { Start = 0, End = Meta.Duration } }
            };
            if (!Meta.HasCursor) Edits.Style.AutoZoom = false;
            if (Edits.Style.AutoZoom) Edits.Zooms = AutoZoom.Generate(this, Edits.Style);
            if (Edits.Style.SpeedUpTyping) ApplyTypingSpeedUp();
            Save();
        }

        /// <summary>Lange Tipp-Phasen (≥ 3 s) automatisch auf 3× beschleunigen.</summary>
        public void ApplyTypingSpeedUp()
        {
            // alte Auto-Beschleunigungen entfernen
            foreach (var c in Edits.Clips.Where(c => c.AutoSpeed)) { c.Speed = 1; c.AutoSpeed = false; }
            foreach (var (s, e) in Input.TypingSpans(minDuration: 3, maxGap: 0.9))
            {
                SplitClipAt(s);
                SplitClipAt(e);
                foreach (var c in Edits.Clips.Where(c => c.Start >= s - 1e-6 && c.End <= e + 1e-6 && !c.Removed))
                {
                    c.Speed = 3;
                    c.AutoSpeed = true;
                }
            }
        }

        public int SplitClipAt(double t)
        {
            for (int i = 0; i < Edits.Clips.Count; i++)
            {
                var c = Edits.Clips[i];
                if (t <= c.Start + 0.05 || t >= c.End - 0.05) { if (t >= c.Start && t < c.End) return i; continue; }
                var right = c.Clone();
                right.Start = t;
                c.End = t;
                Edits.Clips.Insert(i + 1, right);
                return i + 1;
            }
            return Edits.Clips.Count - 1;
        }

        // ------------------------------------------------------------------ Zeit-Abbildung

        /// <summary>Behaltene Abschnitte in Reihenfolge.</summary>
        public IEnumerable<ClipSegment> Kept => Edits.Clips.Where(c => !c.Removed);

        /// <summary>Länge des fertigen Videos (Schnitte und Geschwindigkeit berücksichtigt).</summary>
        public double OutputDuration => Kept.Sum(c => (c.End - c.Start) / Math.Max(0.1, c.Speed));

        /// <summary>Ausgabezeit → Quellzeit.</summary>
        public double SourceAt(double outT)
        {
            double acc = 0;
            ClipSegment? last = null;
            foreach (var c in Kept)
            {
                double len = (c.End - c.Start) / Math.Max(0.1, c.Speed);
                if (outT < acc + len) return c.Start + (outT - acc) * c.Speed;
                acc += len;
                last = c;
            }
            return last?.End ?? 0;
        }

        /// <summary>Quellzeit → Ausgabezeit (null, wenn die Stelle herausgeschnitten ist).</summary>
        public double? OutputAt(double srcT)
        {
            double acc = 0;
            foreach (var c in Kept)
            {
                if (srcT >= c.Start && srcT <= c.End) return acc + (srcT - c.Start) / Math.Max(0.1, c.Speed);
                acc += (c.End - c.Start) / Math.Max(0.1, c.Speed);
            }
            return null;
        }

        public double SpeedAt(double srcT) => Kept.FirstOrDefault(c => srcT >= c.Start && srcT < c.End)?.Speed ?? 1;
    }
}
