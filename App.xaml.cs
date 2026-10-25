using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace SnipSnip
{
    public partial class App : Application
    {
        const string MutexName = "SnipSnip.SingleInstance.v1";
        const string SignalName = "SnipSnip.Signal.v1";

        public static AppSettings Settings { get; private set; } = new();
        public static App Instance => (App)Current;

        Mutex? mutex;
        EventWaitHandle? signal;
        WinForms.NotifyIcon? tray;
        WinForms.ToolStripMenuItem? captureItem;
        HotkeyManager? hotkey;
        SettingsWindow? settingsWindow;
        HistoryWindow? historyWindow;
        volatile bool exiting;

        public string HotkeyDisplay => HotkeyText.Describe(Settings.HotkeyVk, Settings.HotkeyMods);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            int docsIdx = Array.IndexOf(e.Args, "--render-docs");
            if (docsIdx >= 0)
            {
                RenderDocsAndExit(docsIdx + 1 < e.Args.Length ? e.Args[docsIdx + 1] : Path.Combine("docs", "images"));
                return;
            }

            string? fileArg = e.Args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));

            int exportIdx = Array.IndexOf(e.Args, "--export");
            if (exportIdx >= 0 && exportIdx + 2 < e.Args.Length)
            {
                ExportAndExit(e.Args[exportIdx + 1], e.Args[exportIdx + 2]);
                return;
            }

            int frameIdx = Array.IndexOf(e.Args, "--render-frame");
            if (frameIdx >= 0 && frameIdx + 3 < e.Args.Length)
            {
                RenderFrameAndExit(e.Args[frameIdx + 1], e.Args[frameIdx + 2], e.Args[frameIdx + 3]);
                return;
            }

            mutex = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                // Läuft schon -> der laufenden Instanz eine Datei zum Öffnen übergeben oder eine Aufnahme starten lassen.
                try
                {
                    if (fileArg != null)
                    {
                        Directory.CreateDirectory(AppSettings.DataDir);
                        File.WriteAllText(PendingOpenFile, Path.GetFullPath(fileArg));
                    }
                    EventWaitHandle.OpenExisting(SignalName).Set();
                }
                catch { }
                Shutdown();
                return;
            }

            signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            new Thread(() =>
            {
                while (signal.WaitOne() && !exiting)
                    Dispatcher.InvokeAsync(OnSignal);
            }) { IsBackground = true, Name = "SnipSnip.Signal" }.Start();

            DispatcherUnhandledException += (_, a) =>
            {
                MessageBox.Show(a.Exception.Message, "SnipSnip – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };

            Settings = AppSettings.Load();
            SetupTray();
            System.Threading.Tasks.Task.Run(ScreenRecorder.CleanupOldTemp);
            DesktopIcons.Restore();
            Dispatcher.InvokeAsync(CheckCrashRecovery, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            VideoEncoder.WarmUp();

            hotkey = new HotkeyManager();
            hotkey.Pressed += () => StartCapture();
            bool ok = RegisterHotkey();

            if (fileArg != null)
                OpenPath(Path.GetFullPath(fileArg));
            else if (e.Args.Contains("--capture"))
                StartCapture();
            else if (!e.Args.Contains("--tray"))
                Toast.Notify("SnipSnip ist bereit",
                    ok ? $"Drücke {HotkeyDisplay} für eine neue Aufnahme – oder klicke auf das Tray-Symbol."
                       : $"{HotkeyDisplay} ist bereits von einem anderen Programm belegt. Bitte in den Einstellungen ein anderes Kürzel wählen.",
                    null, ok ? null : ShowSettings, 6);
        }

        static string PendingOpenFile => Path.Combine(AppSettings.DataDir, "pending-open.txt");

        /// <summary>Signal einer zweiten Instanz: Datei öffnen (falls übergeben), sonst neue Aufnahme.</summary>
        void OnSignal()
        {
            string? path = null;
            try
            {
                if (File.Exists(PendingOpenFile))
                {
                    path = File.ReadAllText(PendingOpenFile).Trim();
                    File.Delete(PendingOpenFile);
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) OpenPath(path);
            else StartCapture();
        }

        /// <summary>Rendert die README-Bilder (auch in der CI) und beendet sich. Exit-Code 1 bei Fehlern.</summary>
        void RenderDocsAndExit(string outDir)
        {
            // Deterministisch und ohne persönliche Daten (Benutzername im Pfad); Benutzer-Einstellungen bleiben unberührt
            Settings = new AppSettings { SaveFolder = @"C:\Users\Du\Pictures\SnipSnip", VideoFolder = @"C:\Users\Du\Videos\SnipSnip" };
            FFmpeg.DocsOverride = @"C:\Program Files\ffmpeg\bin\ffmpeg.exe";
            // Watchdog: in der CI darf ein hängender Dialog o. Ä. den Build nicht blockieren
            new Thread(() => { Thread.Sleep(TimeSpan.FromMinutes(2)); Environment.Exit(2); }) { IsBackground = true }.Start();
            try
            {
                outDir = Path.GetFullPath(outDir);
                DocRenderer.Run(outDir);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "render-error.txt"), ex.ToString());
                }
                catch { }
                Shutdown(1);
            }
        }

        /// <summary>Kommandozeilen-Export eines Studio-Projekts (MP4, 1080p, Einstellungen aus dem Projekt).</summary>
        async void ExportAndExit(string projectDir, string output)
        {
            try
            {
                Settings = AppSettings.Load();
                var p = StudioProject.Load(projectDir);
                var opts = new StudioExportOptions
                {
                    Gif = output.EndsWith(".gif", StringComparison.OrdinalIgnoreCase),
                    ShortSide = int.TryParse(Settings.ExportResolution, out int ss) ? ss : 1080,
                    Fps = Settings.ExportFps,
                    Crf = 20
                };
                await StudioExporter.ExportAsync(p, opts, Path.GetFullPath(output), (_, _) => { }, CancellationToken.None);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(output + ".error.txt", ex.ToString()); } catch { }
                Shutdown(1);
            }
        }

        /// <summary>Entwickler-Werkzeug: ein Bild eines Studio-Projekts in 1080p rendern (wie im Export).</summary>
        void RenderFrameAndExit(string projectDir, string time, string output)
        {
            try
            {
                Settings = AppSettings.Load();
                var p = StudioProject.Load(projectDir);
                double t = double.Parse(time, System.Globalization.CultureInfo.InvariantCulture);
                using var r = new StudioRenderer(p);
                var (w, h) = r.OutputSize(1080);
                using var screen = new FrameDecoder(p.ScreenFile, p.Meta.Width, p.Meta.Height, Math.Max(1, p.Meta.Fps));
                using var cam = p.HasWebcam ? new FrameDecoder(p.WebcamFile!, 1280, 720, 30, 960) : null;
                var camImg = cam?.Get(p.Meta.WallAt(t) - p.Meta.WebcamStartEpoch);
                using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
                r.Render(surface.Canvas, w, h, t, p.OutputAt(t) ?? t, screen.Get(t), camImg, export: true);
                using var img = surface.Snapshot();
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(output, data.ToArray());
                File.WriteAllText(output + ".txt", r.DebugInfo(w, h, t));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(output + ".txt", ex.ToString()); } catch { }
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            exiting = true;
            signal?.Set();
            hotkey?.Dispose();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnExit(e);
        }

        // ---------------------------------------------------------------- Tray

        void SetupTray()
        {
            var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
            captureItem = new WinForms.ToolStripMenuItem("", null, (_, _) => StartCapture());
            captureItem.Font = new System.Drawing.Font(captureItem.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(captureItem);
            menu.Items.Add("Neue Videoaufnahme", null, (_, _) => StartCapture(video: true));
            menu.Items.Add("Bild oder Video öffnen …", null, (_, _) => OpenFile());
            menu.Items.Add("Upload-Verlauf", null, (_, _) => ShowHistory());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Einstellungen", null, (_, _) => ShowSettings());
            menu.Items.Add("Bilder-Ordner öffnen", null, (_, _) => OpenFolder(Settings.SaveFolder));
            menu.Items.Add("Video-Ordner öffnen", null, (_, _) => OpenFolder(Settings.VideoFolder));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Beenden", null, (_, _) => Shutdown());

            System.Drawing.Icon icon;
            var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            using (var s = res!.Stream) icon = new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);

            tray = new WinForms.NotifyIcon { Icon = icon, Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (_, a) => { if (a.Button == WinForms.MouseButtons.Left) StartCapture(); };
            UpdateTrayText();
        }

        void UpdateTrayText()
        {
            if (tray != null) tray.Text = $"SnipSnip – {HotkeyDisplay} für neue Aufnahme";
            if (captureItem != null) captureItem.Text = $"Neue Aufnahme   ({HotkeyDisplay})";
        }

        // ---------------------------------------------------------------- Hotkey / Settings

        bool RegisterHotkey() => hotkey != null && hotkey.Register(Settings.HotkeyVk, Settings.HotkeyMods);

        public void SuspendHotkey() => hotkey?.Unregister();

        public void ResumeHotkey() => RegisterHotkey();

        /// <summary>Übernimmt neue Einstellungen. Gibt false zurück, wenn das Tastenkürzel nicht registriert werden konnte.</summary>
        public bool ApplySettings(AppSettings s)
        {
            Settings = s;
            Settings.Save();
            Autostart.Set(s.StartWithWindows);
            bool ok = RegisterHotkey();
            UpdateTrayText();
            return ok;
        }

        public void ShowSettings()
        {
            if (settingsWindow is { IsLoaded: true }) { settingsWindow.Activate(); return; }
            settingsWindow = new SettingsWindow();
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        public void ShowHistory()
        {
            if (historyWindow is { IsLoaded: true }) { historyWindow.Reload(); historyWindow.Activate(); return; }
            historyWindow = new HistoryWindow();
            historyWindow.Closed += (_, _) => historyWindow = null;
            historyWindow.Show();
            historyWindow.Activate();
        }

        // ---------------------------------------------------------------- Aufnahme

        /// <summary>Startet eine Aufnahme – oder beendet eine laufende Videoaufnahme (Tastenkürzel erneut gedrückt).</summary>
        public void StartCapture(Window? hide = null, bool? video = null)
        {
            if (RecordingSession.Current != null)
            {
                RecordingSession.Current.Stop();
                return;
            }
            if (video.HasValue) Settings.VideoMode = video.Value;
            CaptureSession.Start(hide);
        }

        /// <summary>Wird nach einer Bereichsauswahl im Video-Modus aufgerufen.</summary>
        public async void StartRecording(Rect physicalRegion, AfterCapture action, Window? hidden, IntPtr windowHandle = default)
        {
            if (!FFmpeg.IsAvailable && !await EnsureFfmpegAsync(null))
            {
                if (hidden != null) { hidden.Show(); hidden.Activate(); }
                return;
            }

            // Fenster-Modus: Fenster vorher auf eine feste Größe bringen (z. B. 1920 × 1080)
            if (windowHandle != IntPtr.Zero && TryParseSize(Settings.RecordWindowSize, out int tw, out int th))
            {
                var r = await ResizeWindowAsync(windowHandle, tw, th);
                if (r != null) physicalRegion = r.Value;
            }
            RecordingSession.Start(physicalRegion, action, hidden);
        }

        static bool TryParseSize(string s, out int w, out int h)
        {
            w = h = 0;
            var parts = (s ?? "").Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h) && w > 100 && h > 100;
        }

        /// <summary>Setzt die sichtbare Fenstergröße exakt (DWM-Rahmen wird herausgerechnet) und liefert die neuen Grenzen.</summary>
        static async Task<Rect?> ResizeWindowAsync(IntPtr hwnd, int w, int h)
        {
            try
            {
                Native.GetWindowRect(hwnd, out var wr);
                Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT fr, 16);
                int extraW = (wr.Right - wr.Left) - (fr.Right - fr.Left), extraH = (wr.Bottom - wr.Top) - (fr.Bottom - fr.Top);
                ShowWindowNormal(hwnd);
                Native.SetWindowPos(hwnd, IntPtr.Zero, wr.Left, wr.Top, w + extraW, h + extraH, 0x0004 | Native.SWP_NOACTIVATE);
                await Task.Delay(350);
                Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out fr, 16);
                return new Rect(fr.Left, fr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top);
            }
            catch { return null; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        static void ShowWindowNormal(IntPtr hwnd) => ShowWindow(hwnd, 9 /* SW_RESTORE */);

        /// <summary>Fragt nach, ob FFmpeg heruntergeladen werden soll. true = FFmpeg ist danach verfügbar.</summary>
        public Task<bool> EnsureFfmpegAsync(Window? owner)
        {
            if (FFmpeg.IsAvailable) return Task.FromResult(true);
            var w = new FfmpegSetupWindow();
            if (owner is { IsVisible: true }) w.Owner = owner;
            return Task.FromResult(w.ShowDialog() == true && FFmpeg.IsAvailable);
        }

        /// <summary>Nach der Aufnahme: Studio öffnen oder direkt mit dem Standard-Stil exportieren.</summary>
        public async Task HandleRecording(string projectDir, AfterCapture action)
        {
            StudioProject project;
            try { project = StudioProject.Load(projectDir); }
            catch (Exception ex)
            {
                Toast.Notify("Projekt konnte nicht geladen werden", ex.Message, null, () => OpenFolder(projectDir), 10);
                return;
            }

            // Editor: direkt ins Studio (das Projekt wird dort automatisch gespeichert)
            if (action == AfterCapture.Editor)
            {
                OpenStudio(project);
                return;
            }

            // Direkt-Export mit dem Standard-Stil (inkl. Auto-Zoom, Hintergrund, Cursor …)
            string? file = null;
            var toast = Toast.Notify("Video wird erstellt …", project.Name, null, null, 0);
            toast.SetProgress(true);
            try
            {
                Directory.CreateDirectory(Settings.VideoFolder);
                bool keep = Settings.AutoSave || action == AfterCapture.Save;
                file = keep
                    ? Path.Combine(Settings.VideoFolder, project.Name + ".mp4")
                    : Path.Combine(Path.GetTempPath(), "SnipSnip", $"export_{Guid.NewGuid():N}", project.Name + ".mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                int fps = Math.Min(Settings.ExportFps, Math.Max(30, project.Meta.Fps));
                var opts = new StudioExportOptions { ShortSide = int.TryParse(Settings.ExportResolution, out int ss) ? ss : 1080, Fps = fps, Crf = Settings.VideoCrf };
                int lastPct = -1;
                await StudioExporter.ExportAsync(project, opts, file, (v, _) => Dispatcher.InvokeAsync(() =>
                {
                    int pct = (int)(v * 100);
                    if (pct != lastPct) { lastPct = pct; toast.Update("Video wird erstellt …", $"{project.Name}  ·  {pct} %", null, 0); toast.SetProgress(true); }
                }), CancellationToken.None);

                if (Settings.CopyToClipboard || action == AfterCapture.Copy)
                    try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { file }); } catch { }
            }
            catch (Exception ex)
            {
                toast.Update("Export fehlgeschlagen", ex.Message, () => OpenStudio(project), 12, ("Im Studio öffnen", () => OpenStudio(project)));
                return;
            }

            var thumb = await VideoExporter.ThumbnailAsync(file);
            switch (action)
            {
                case AfterCapture.Editor:
                    toast.Update("Video gespeichert", Path.GetFileName(file), () => OpenFolder(file, true), 4);
                    OpenStudio(project);
                    break;
                case AfterCapture.Copy:
                    toast.Update("Video kopiert", "Zum Einfügen in Discord, Teams, Explorer … – klicken zum Bearbeiten.", () => OpenStudio(project), 6,
                        ("Im Studio bearbeiten", () => OpenStudio(project)));
                    break;
                case AfterCapture.Save:
                    toast.Update("Video gespeichert", Path.GetFileName(file), () => OpenFolder(file, true), 6,
                        ("Ordner öffnen", () => OpenFolder(file, true)),
                        ("Im Studio bearbeiten", () => OpenStudio(project)));
                    break;
                case AfterCapture.Upload:
                    await UploadFileAsync(file, thumb);
                    break;
            }
        }

        public void OpenStudio(StudioProject project)
        {
            if (!FFmpeg.IsAvailable && !EnsureFfmpegAsync(null).Result) return;
            var w = new StudioWindow(project);
            w.Show();
            w.Activate();
        }

        /// <summary>Unvollständige Aufnahmen (z. B. nach einem Absturz) finden und Wiederherstellung anbieten.</summary>
        void CheckCrashRecovery()
        {
            try
            {
                if (!Directory.Exists(StudioProject.ProjectsRoot)) return;
                foreach (var dir in Directory.GetDirectories(StudioProject.ProjectsRoot))
                {
                    string metaFile = Path.Combine(dir, "meta.json");
                    if (!File.Exists(metaFile) || !File.Exists(Path.Combine(dir, "screen.mkv"))) continue;
                    var meta = System.Text.Json.JsonSerializer.Deserialize<RecordingMeta>(File.ReadAllText(metaFile), AppSettings.Json);
                    if (meta == null || meta.Complete || meta.Duration < 1) continue;
                    string d = dir;
                    Toast.Notify("Unterbrochene Aufnahme gefunden",
                        $"{Path.GetFileName(d)} · {RecordingSession.Format(TimeSpan.FromSeconds(meta.Duration))} – wiederherstellen?", null, null, 0,
                        ("Wiederherstellen", () =>
                        {
                            meta.Complete = true;
                            meta.HasSystemAudio = File.Exists(Path.Combine(d, "system.wav"));
                            meta.HasMic = File.Exists(Path.Combine(d, "mic.wav"));
                            StudioProject.SaveMeta(d, meta);
                            OpenStudio(StudioProject.Load(d));
                        }),
                        ("Verwerfen", () => { try { Directory.Delete(d, true); } catch { } }));
                    break; // eine Meldung reicht
                }
            }
            catch { }
        }

        public void HandleCapture(BitmapSource img, AfterCapture action)
        {
            string? savedPath = null;
            try
            {
                if (Settings.CopyToClipboard || action == AfterCapture.Copy)
                    ImageUtil.CopyToClipboard(img);
                if (Settings.AutoSave || action == AfterCapture.Save)
                    savedPath = ImageUtil.SaveToFolder(img);
            }
            catch (Exception ex)
            {
                Toast.Notify("Fehler", ex.Message, img);
            }

            switch (action)
            {
                case AfterCapture.Editor:
                    OpenEditor(img);
                    break;
                case AfterCapture.Copy:
                    Toast.Notify("In die Zwischenablage kopiert", "Klicken, um das Bild zu bearbeiten.", img, () => OpenEditor(img));
                    break;
                case AfterCapture.Save:
                    if (savedPath != null)
                        Toast.Notify("Gespeichert", Path.GetFileName(savedPath), img, () => OpenEditor(img), 6,
                            ("Ordner öffnen", () => OpenFolder(savedPath, true)),
                            ("Bearbeiten", () => OpenEditor(img)));
                    break;
                case AfterCapture.Upload:
                    _ = UploadAsync(img);
                    break;
            }
        }

        public void OpenEditor(BitmapSource img)
        {
            var w = new EditorWindow(img);
            w.Show();
            w.Activate();
        }

        public static bool IsVideoFile(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".wmv" or ".m4v";

        public void OpenFile(Window? owner = null)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Bild oder Video öffnen",
                Filter = "Bilder, Videos und Projekte|meta.json;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.m4v" +
                         "|Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|Videos|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.m4v|Alle Dateien|*.*"
            };
            if (dlg.ShowDialog(owner) != true) return;
            OpenPath(dlg.FileName);
        }

        /// <summary>Öffnet ein Bild im Bild-Editor oder ein Video im Video-Editor.</summary>
        public void OpenPath(string path)
        {
            try
            {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "meta.json"))) OpenStudio(StudioProject.Load(path));
                else if (Path.GetFileName(path).Equals("meta.json", StringComparison.OrdinalIgnoreCase)) OpenStudio(StudioProject.Load(Path.GetDirectoryName(path)!));
                else if (Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase)) OpenStudio(StudioProject.Load(Path.GetDirectoryName(path)!));
                else if (IsVideoFile(path)) _ = ImportVideoAsync(path);
                else if (ImageUtil.IsImageFile(path)) OpenEditor(ImageUtil.Load(path));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Die Datei konnte nicht geöffnet werden:\n" + ex.Message, "SnipSnip");
            }
        }

        /// <summary>Vorhandenes Video als Studio-Projekt importieren.</summary>
        async Task ImportVideoAsync(string path)
        {
            if (!FFmpeg.IsAvailable && !await EnsureFfmpegAsync(null)) return;
            var info = await FFmpeg.ProbeAsync(path);
            if (info.Width <= 0 || info.Duration <= TimeSpan.Zero)
            {
                MessageBox.Show("Das Video konnte nicht gelesen werden.", "SnipSnip");
                return;
            }
            OpenStudio(StudioProject.Import(path, info));
        }

        public static void OpenFolder(string path, bool selectFile = false)
        {
            try
            {
                if (selectFile && File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else
                {
                    Directory.CreateDirectory(path);
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch { }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ---------------------------------------------------------------- Upload

        public Task UploadAsync(BitmapSource img) =>
            UploadCoreAsync(async () => (await Task.Run(() => ImageUtil.Encode(img, "png")), ImageUtil.DefaultFileName("png")),
                img, () => _ = UploadAsync(img));

        /// <summary>Lädt eine Datei (MP4, GIF, PNG …) hoch.</summary>
        public async Task UploadFileAsync(string path, BitmapSource? thumb = null)
        {
            thumb ??= IsVideoFile(path) ? await VideoExporter.ThumbnailAsync(path) : null;
            long size = new FileInfo(path).Length;
            if (size > 200L * 1024 * 1024)
            {
                Toast.Notify("Datei zu groß", $"{size / 1024 / 1024} MB – Imgur und catbox.moe erlauben höchstens 200 MB. " +
                    "Tipp: im Video-Editor kürzen oder auf 50 % verkleinern.", thumb, null, 10);
                return;
            }
            await UploadCoreAsync(async () => (await File.ReadAllBytesAsync(path), Path.GetFileName(path)),
                thumb, () => _ = UploadFileAsync(path, thumb));
        }

        async Task UploadCoreAsync(Func<Task<(byte[] Data, string FileName)>> load, BitmapSource? thumb, Action retry)
        {
            var provider = Settings.Provider;
            if (provider == UploadProvider.Imgur && string.IsNullOrWhiteSpace(Settings.ImgurClientId))
            {
                var r = MessageBox.Show(
                    "Für Uploads zu Imgur wird eine (kostenlose) Imgur-Client-ID benötigt.\n\n" +
                    "Ja – Einstellungen öffnen und Client-ID eintragen\n" +
                    "Nein – dieses Mal ohne Konto zu catbox.moe hochladen\n" +
                    "Abbrechen – nicht hochladen",
                    "SnipSnip – Hochladen", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
                if (r == MessageBoxResult.Yes) { ShowSettings(); return; }
                if (r != MessageBoxResult.No) return;
                provider = UploadProvider.Catbox;
            }

            string name = Uploader.Name(provider);
            var toast = Toast.Notify("Wird hochgeladen …", $"zu {name}", thumb, null, 0);
            toast.SetProgress(true);

            try
            {
                // Bilder werden auf einem Hintergrund-Thread kodiert -> müssen eingefroren sein (sind sie).
                var (data, fileName) = await load();
                var res = await Uploader.UploadAsync(data, provider, fileName);

                UploadHistory.Add(new UploadRecord { Time = DateTime.Now, Provider = name, Url = res.Url, DeleteUrl = res.DeleteUrl });
                historyWindow?.Reload();

                if (Settings.CopyLinkAfterUpload) ImageUtil.CopyText(res.Url);
                if (Settings.OpenLinkAfterUpload) OpenUrl(res.Url);

                toast.Update("Hochgeladen ✓",
                    Settings.CopyLinkAfterUpload ? $"{res.Url}\nLink wurde kopiert." : res.Url,
                    () => OpenUrl(res.Url), 8,
                    ("Öffnen", () => OpenUrl(res.Url)),
                    ("Link kopieren", () => ImageUtil.CopyText(res.Url)));
            }
            catch (Exception ex)
            {
                toast.Update("Upload fehlgeschlagen", ex.Message, null, 10,
                    ("Erneut versuchen", retry),
                    ("Einstellungen", ShowSettings));
            }
        }
    }
}
