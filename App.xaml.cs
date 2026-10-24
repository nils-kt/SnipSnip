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
        public async void StartRecording(Rect physicalRegion, AfterCapture action, Window? hidden)
        {
            if (!FFmpeg.IsAvailable && !await EnsureFfmpegAsync(null))
            {
                if (hidden != null) { hidden.Show(); hidden.Activate(); }
                return;
            }
            RecordingSession.Start(physicalRegion, action, hidden);
        }

        /// <summary>Fragt nach, ob FFmpeg heruntergeladen werden soll. true = FFmpeg ist danach verfügbar.</summary>
        public Task<bool> EnsureFfmpegAsync(Window? owner)
        {
            if (FFmpeg.IsAvailable) return Task.FromResult(true);
            var w = new FfmpegSetupWindow();
            if (owner is { IsVisible: true }) w.Owner = owner;
            return Task.FromResult(w.ShowDialog() == true && FFmpeg.IsAvailable);
        }

        public async Task HandleRecording(string path, AfterCapture action)
        {
            string? saved = null;
            try
            {
                if (Settings.AutoSave || action == AfterCapture.Save)
                {
                    Directory.CreateDirectory(Settings.VideoFolder);
                    saved = Path.Combine(Settings.VideoFolder, Path.GetFileName(path));
                    File.Copy(path, saved, true);
                }
                if (Settings.CopyToClipboard || action == AfterCapture.Copy)
                {
                    var list = new System.Collections.Specialized.StringCollection { saved ?? path };
                    try { Clipboard.SetFileDropList(list); } catch { }
                }
            }
            catch (Exception ex)
            {
                Toast.Notify("Fehler", ex.Message, null);
            }

            switch (action)
            {
                case AfterCapture.Editor:
                    OpenVideoEditor(path, isTempRecording: saved == null);
                    break;
                case AfterCapture.Copy:
                {
                    var thumb = await VideoExporter.ThumbnailAsync(path);
                    Toast.Notify("Video kopiert", "Zum Einfügen in Discord, Teams, Explorer … – klicken zum Bearbeiten.", thumb,
                        () => OpenVideoEditor(path, saved == null), 6);
                    break;
                }
                case AfterCapture.Save:
                    if (saved != null)
                    {
                        var thumb = await VideoExporter.ThumbnailAsync(saved);
                        Toast.Notify("Video gespeichert", Path.GetFileName(saved), thumb, () => OpenVideoEditor(saved, false), 6,
                            ("Ordner öffnen", () => OpenFolder(saved, true)),
                            ("Bearbeiten", () => OpenVideoEditor(saved, false)));
                    }
                    break;
                case AfterCapture.Upload:
                    await UploadFileAsync(path);
                    break;
            }
        }

        public void OpenVideoEditor(string path, bool isTempRecording)
        {
            var w = new VideoEditorWindow(path, isTempRecording);
            w.Show();
            w.Activate();
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
                Filter = "Bilder und Videos|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.m4v" +
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
                if (IsVideoFile(path)) OpenVideoEditor(path, isTempRecording: false);
                else if (ImageUtil.IsImageFile(path)) OpenEditor(ImageUtil.Load(path));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Die Datei konnte nicht geöffnet werden:\n" + ex.Message, "SnipSnip");
            }
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
