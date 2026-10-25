using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnipSnip
{
    public enum CaptureMode { Rectangle, Window, Freeform, Fullscreen }

    public enum AfterCapture { Editor, Copy, Save, Upload }

    public enum UploadProvider { Imgur, Catbox }

    public class AppSettings
    {
        public int HotkeyVk { get; set; } = 0x13; // VK_PAUSE
        public int HotkeyMods { get; set; }

        public CaptureMode Mode { get; set; } = CaptureMode.Rectangle;
        public AfterCapture Action { get; set; } = AfterCapture.Editor;

        public bool CopyToClipboard { get; set; } = true;
        public bool AutoSave { get; set; }
        public string SaveFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnipSnip");
        public string ImageFormat { get; set; } = "png";

        public UploadProvider Provider { get; set; } = UploadProvider.Imgur;
        public string ImgurClientId { get; set; } = "";
        public bool CopyLinkAfterUpload { get; set; } = true;
        public bool OpenLinkAfterUpload { get; set; }

        public bool ShowMagnifier { get; set; } = true;
        public bool StartWithWindows { get; set; }

        // ---- Video
        /// <summary>Zuletzt gewählter Medientyp in der Leiste (Foto oder Video).</summary>
        public bool VideoMode { get; set; }
        public int VideoFps { get; set; } = 30;
        /// <summary>x264-CRF: kleiner = bessere Qualität / größere Datei.</summary>
        public int VideoCrf { get; set; } = 23;
        public bool RecordSystemAudio { get; set; } = true;
        public bool RecordMicrophone { get; set; }
        public bool RecordWebcam { get; set; }
        public string WebcamDevice { get; set; } = "";
        public bool RecordingCountdown { get; set; } = true;
        public bool HideDesktopIcons { get; set; }
        /// <summary>Fenster-Modus: Fenster vor der Aufnahme auf diese Größe setzen ("" = aus, z. B. "1920x1080").</summary>
        public string RecordWindowSize { get; set; } = "";
        public bool ShowSpeakerNotes { get; set; }
        public string SpeakerNotes { get; set; } = "";
        /// <summary>Alle Tasten aufzeichnen (sonst nur Tastenkürzel – Datenschutz).</summary>
        public bool RecordAllKeys { get; set; }

        // ---- Studio
        public StudioStyle? StudioDefaultStyle { get; set; }
        public Dictionary<string, StudioStyle> StudioPresets { get; set; } = new();
        public string ExportResolution { get; set; } = "1080";
        public int ExportFps { get; set; } = 60;
        public string VideoFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SnipSnip");
        /// <summary>Optionaler fester Pfad zu ffmpeg.exe (leer = automatisch suchen).</summary>
        public string FfmpegPath { get; set; } = "";

        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipSnip");

        static string FilePath => Path.Combine(DataDir, "settings.json");

        internal static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            }
            catch { /* kaputte Datei -> Standardwerte */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch { /* nicht kritisch */ }
        }

        public AppSettings Clone() =>
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;
    }

    public class UploadRecord
    {
        public DateTime Time { get; set; }
        public string Provider { get; set; } = "";
        public string Url { get; set; } = "";
        public string? DeleteUrl { get; set; }

        [JsonIgnore] public string TimeText => Time.ToString("dd.MM.yyyy  HH:mm");
        [JsonIgnore] public bool CanDelete => !string.IsNullOrEmpty(DeleteUrl);
    }

    public static class UploadHistory
    {
        static string FilePath => Path.Combine(AppSettings.DataDir, "uploads.json");

        public static List<UploadRecord> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<UploadRecord>>(File.ReadAllText(FilePath), AppSettings.Json) ?? new();
            }
            catch { }
            return new List<UploadRecord>();
        }

        public static void Save(List<UploadRecord> list)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list, AppSettings.Json));
            }
            catch { }
        }

        public static void Add(UploadRecord r)
        {
            var list = Load();
            list.Insert(0, r);
            if (list.Count > 500) list.RemoveRange(500, list.Count - 500);
            Save(list);
        }
    }
}
