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

            mutex = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                // Läuft schon -> der laufenden Instanz sagen, dass sie eine Aufnahme starten soll.
                try { EventWaitHandle.OpenExisting(SignalName).Set(); } catch { }
                Shutdown();
                return;
            }

            signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            new Thread(() =>
            {
                while (signal.WaitOne() && !exiting)
                    Dispatcher.InvokeAsync(() => StartCapture());
            }) { IsBackground = true, Name = "SnipSnip.Signal" }.Start();

            DispatcherUnhandledException += (_, a) =>
            {
                MessageBox.Show(a.Exception.Message, "SnipSnip – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };

            Settings = AppSettings.Load();
            SetupTray();

            hotkey = new HotkeyManager();
            hotkey.Pressed += () => StartCapture();
            bool ok = RegisterHotkey();

            if (e.Args.Contains("--capture"))
                StartCapture();
            else if (!e.Args.Contains("--tray"))
                Toast.Notify("SnipSnip ist bereit",
                    ok ? $"Drücke {HotkeyDisplay} für eine neue Aufnahme – oder klicke auf das Tray-Symbol."
                       : $"{HotkeyDisplay} ist bereits von einem anderen Programm belegt. Bitte in den Einstellungen ein anderes Kürzel wählen.",
                    null, ok ? null : ShowSettings, 6);
        }

        /// <summary>Rendert die README-Bilder (auch in der CI) und beendet sich. Exit-Code 1 bei Fehlern.</summary>
        void RenderDocsAndExit(string outDir)
        {
            // Deterministisch und ohne persönliche Daten (Benutzername im Pfad); Benutzer-Einstellungen bleiben unberührt
            Settings = new AppSettings { SaveFolder = @"C:\Users\Du\Pictures\SnipSnip" };
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
            menu.Items.Add("Bild öffnen …", null, (_, _) => OpenImageFile());
            menu.Items.Add("Upload-Verlauf", null, (_, _) => ShowHistory());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Einstellungen", null, (_, _) => ShowSettings());
            menu.Items.Add("Bilder-Ordner öffnen", null, (_, _) => OpenFolder(Settings.SaveFolder));
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

        public void StartCapture(Window? hide = null) => CaptureSession.Start(hide);

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

        public void OpenImageFile(Window? owner = null)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Bild öffnen",
                Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|Alle Dateien|*.*"
            };
            if (dlg.ShowDialog(owner) != true) return;
            try { OpenEditor(ImageUtil.Load(dlg.FileName)); }
            catch (Exception ex) { MessageBox.Show("Das Bild konnte nicht geöffnet werden:\n" + ex.Message, "SnipSnip"); }
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

        public async Task UploadAsync(BitmapSource img)
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
            var toast = Toast.Notify("Wird hochgeladen …", $"zu {name}", img, null, 0);
            toast.SetProgress(true);

            try
            {
                byte[] png = await Task.Run(() => ImageUtil.Encode(img, "png"));
                // Encode läuft auf einem Hintergrund-Thread -> img muss eingefroren sein (ist es).
                var res = await Uploader.UploadAsync(png, provider, ImageUtil.DefaultFileName("png"));

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
                    ("Erneut versuchen", () => _ = UploadAsync(img)),
                    ("Einstellungen", ShowSettings));
            }
        }
    }
}
