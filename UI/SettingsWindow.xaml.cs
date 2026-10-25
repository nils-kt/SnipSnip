using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace SnipSnip
{
    public partial class SettingsWindow : Window
    {
        readonly AppSettings s;
        int vk, mods;

        public SettingsWindow()
        {
            InitializeComponent();
            s = App.Settings.Clone();
            vk = s.HotkeyVk;
            mods = s.HotkeyMods;

            HotkeyBox.Text = HotkeyText.Describe(vk, mods);

            AEditor.IsChecked = s.Action == AfterCapture.Editor;
            ACopy.IsChecked = s.Action == AfterCapture.Copy;
            ASave.IsChecked = s.Action == AfterCapture.Save;
            AUpload.IsChecked = s.Action == AfterCapture.Upload;
            CopyCheck.IsChecked = s.CopyToClipboard;
            AutoSaveCheck.IsChecked = s.AutoSave;
            MagnifierCheck.IsChecked = s.ShowMagnifier;

            FolderBox.Text = s.SaveFolder;
            FmtPng.IsChecked = s.ImageFormat != "jpg";
            FmtJpg.IsChecked = s.ImageFormat == "jpg";

            PImgur.IsChecked = s.Provider == UploadProvider.Imgur;
            PCatbox.IsChecked = s.Provider == UploadProvider.Catbox;
            ClientIdBox.Text = s.ImgurClientId;
            CopyLinkCheck.IsChecked = s.CopyLinkAfterUpload;
            OpenLinkCheck.IsChecked = s.OpenLinkAfterUpload;

            AutostartCheck.IsChecked = s.StartWithWindows;
            UpdateProviderUi();

            Fps15.IsChecked = s.VideoFps <= 15;
            Fps30.IsChecked = s.VideoFps > 15 && s.VideoFps < 60;
            Fps60.IsChecked = s.VideoFps >= 60;
            QHigh.IsChecked = s.VideoCrf <= 20;
            QMid.IsChecked = s.VideoCrf > 20 && s.VideoCrf < 26;
            QSmall.IsChecked = s.VideoCrf >= 26;
            SysAudioCheck.IsChecked = s.RecordSystemAudio;
            MicCheck.IsChecked = s.RecordMicrophone;
            WebcamCheck.IsChecked = s.RecordWebcam;
            CountdownCheck.IsChecked = s.RecordingCountdown;
            IconsCheck.IsChecked = s.HideDesktopIcons;
            NotesCheck.IsChecked = s.ShowSpeakerNotes;
            AllKeysCheck.IsChecked = s.RecordAllKeys;
            WinSizeOff.IsChecked = string.IsNullOrEmpty(s.RecordWindowSize);
            WinSize720.IsChecked = s.RecordWindowSize == "1280x720";
            WinSize1080.IsChecked = s.RecordWindowSize == "1920x1080";
            WinSize1440.IsChecked = s.RecordWindowSize == "2560x1440";
            HwCheck.IsChecked = s.HardwareEncoding;
            Loaded += async (_, _) => await LoadWebcamsAsync();
            Loaded += async (_, _) => await ShowEncoderAsync();
            VideoFolderBox.Text = s.VideoFolder;
            UpdateFfmpegUi();
        }

        void UpdateFfmpegUi()
        {
            FFmpeg.ResetCache();
            var path = FFmpeg.Find();
            FfmpegStatus.Text = path != null ? "✓ Gefunden: " + path : "Nicht gefunden – wird für Videoaufnahmen benötigt.";
            FfmpegStatus.Foreground = path != null
                ? (System.Windows.Media.Brush)FindResource("FgDimBrush")
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x9F, 0x43));
            FfmpegBtn.Content = path != null ? "Anderes wählen …" : "Einrichten …";
        }

        async void Ffmpeg_Click(object sender, RoutedEventArgs e)
        {
            if (FFmpeg.IsAvailable)
            {
                var dlg = new OpenFileDialog { Title = "ffmpeg.exe auswählen", Filter = "ffmpeg.exe|ffmpeg.exe|Programme (*.exe)|*.exe" };
                if (dlg.ShowDialog(this) == true) App.Settings.FfmpegPath = dlg.FileName;
            }
            else await App.Instance.EnsureFfmpegAsync(this);
            UpdateFfmpegUi();
        }

        async System.Threading.Tasks.Task ShowEncoderAsync()
        {
            if (!FFmpeg.IsAvailable) { HwStatus.Text = "Benötigt FFmpeg."; return; }
            HwStatus.Text = "Prüfe Grafikkarte …";
            bool prev = App.Settings.HardwareEncoding;
            App.Settings.HardwareEncoding = true; // nur zur Anzeige: was wäre verfügbar?
            var best = await System.Threading.Tasks.Task.Run(VideoEncoder.Best);
            App.Settings.HardwareEncoding = prev;
            HwStatus.Text = best == EncoderKind.Cpu
                ? "Kein nutzbarer Hardware-Encoder gefunden – es wird die CPU verwendet."
                : $"Erkannt: {VideoEncoder.Name(best)}. Fällt die Grafikkarte aus, übernimmt automatisch die CPU.";
        }

        async System.Threading.Tasks.Task LoadWebcamsAsync()
        {
            WebcamDevices.Children.Clear();
            WebcamDevices.Children.Add(new TextBlock { Text = "Suche Kameras …", Style = (Style)FindResource("Note") });
            var list = await WebcamCapture.ListDevicesAsync();
            WebcamDevices.Children.Clear();
            if (list.Count == 0)
            {
                WebcamDevices.Children.Add(new TextBlock { Text = FFmpeg.IsAvailable ? "Keine Kamera gefunden." : "Kameraliste benötigt FFmpeg.", Style = (Style)FindResource("Note") });
                return;
            }
            foreach (var d in list)
                WebcamDevices.Children.Add(new RadioButton
                {
                    Content = d,
                    GroupName = "Cam",
                    Style = (Style)FindResource("Seg"),
                    IsChecked = d == s.WebcamDevice || (string.IsNullOrEmpty(s.WebcamDevice) && d == list[0])
                });
        }

        void BrowseVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Video-Ordner wählen", InitialDirectory = VideoFolderBox.Text };
            if (dlg.ShowDialog(this) == true) VideoFolderBox.Text = dlg.FolderName;
        }

        // --------------------------------------------------------- Tastenkürzel aufnehmen

        void HotkeyBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            App.Instance.SuspendHotkey(); // sonst löst das alte Kürzel beim Drücken eine Aufnahme aus
            HotkeyBox.Text = "Taste drücken …";
        }

        void HotkeyBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            HotkeyBox.Text = HotkeyText.Describe(vk, mods);
            App.Instance.ResumeHotkey();
        }

        void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            RecordKey(e.Key == Key.System ? e.SystemKey : e.Key);
        }

        void HotkeyBox_KeyUp(object sender, KeyEventArgs e)
        {
            // Die Druck-Taste liefert in WPF nur KeyUp.
            if (e.Key == Key.Snapshot) { e.Handled = true; RecordKey(Key.Snapshot); }
        }

        void RecordKey(Key key)
        {
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
                or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.Tab)
                return;
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }

            vk = KeyInterop.VirtualKeyFromKey(key);
            mods = HotkeyText.ModsFromKeyboard(Keyboard.Modifiers);
            HotkeyBox.Text = HotkeyText.Describe(vk, mods);
            HotkeyHint.Text = mods == 0 && key is >= Key.A and <= Key.Z
                ? "Hinweis: Ein einzelner Buchstabe ohne Strg/Alt blockiert diesen Buchstaben überall."
                : "Wird beim Speichern übernommen.";
        }

        // --------------------------------------------------------- Sonstiges

        void Provider_Checked(object sender, RoutedEventArgs e) => UpdateProviderUi();

        void UpdateProviderUi()
        {
            if (ImgurPanel != null) ImgurPanel.Visibility = PImgur.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Bilder-Ordner wählen", InitialDirectory = FolderBox.Text };
            if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
        }

        void Link_Navigate(object sender, RequestNavigateEventArgs e)
        {
            App.OpenUrl(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        void DataFolder_Click(object sender, RoutedEventArgs e) => App.OpenFolder(AppSettings.DataDir);

        void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        void Save_Click(object sender, RoutedEventArgs e)
        {
            s.HotkeyVk = vk;
            s.HotkeyMods = mods;
            s.Action = ACopy.IsChecked == true ? AfterCapture.Copy
                     : ASave.IsChecked == true ? AfterCapture.Save
                     : AUpload.IsChecked == true ? AfterCapture.Upload
                     : AfterCapture.Editor;
            s.CopyToClipboard = CopyCheck.IsChecked == true;
            s.AutoSave = AutoSaveCheck.IsChecked == true;
            s.ShowMagnifier = MagnifierCheck.IsChecked == true;
            s.SaveFolder = FolderBox.Text.Trim();
            s.ImageFormat = FmtJpg.IsChecked == true ? "jpg" : "png";
            s.Provider = PCatbox.IsChecked == true ? UploadProvider.Catbox : UploadProvider.Imgur;
            s.ImgurClientId = ClientIdBox.Text.Trim();
            s.CopyLinkAfterUpload = CopyLinkCheck.IsChecked == true;
            s.OpenLinkAfterUpload = OpenLinkCheck.IsChecked == true;
            s.StartWithWindows = AutostartCheck.IsChecked == true;

            s.VideoFps = Fps15.IsChecked == true ? 15 : Fps60.IsChecked == true ? 60 : 30;
            s.VideoCrf = QHigh.IsChecked == true ? 18 : QSmall.IsChecked == true ? 28 : 23;
            s.RecordSystemAudio = SysAudioCheck.IsChecked == true;
            s.RecordMicrophone = MicCheck.IsChecked == true;
            s.HardwareEncoding = HwCheck.IsChecked == true;
            s.RecordWebcam = WebcamCheck.IsChecked == true;
            s.WebcamDevice = WebcamDevices.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Content as string ?? s.WebcamDevice;
            s.RecordingCountdown = CountdownCheck.IsChecked == true;
            s.HideDesktopIcons = IconsCheck.IsChecked == true;
            s.ShowSpeakerNotes = NotesCheck.IsChecked == true;
            s.RecordAllKeys = AllKeysCheck.IsChecked == true;
            s.RecordWindowSize = WinSize720.IsChecked == true ? "1280x720" : WinSize1080.IsChecked == true ? "1920x1080"
                               : WinSize1440.IsChecked == true ? "2560x1440" : "";
            s.SpeakerNotes = App.Settings.SpeakerNotes;
            s.StudioDefaultStyle = App.Settings.StudioDefaultStyle;
            s.StudioPresets = App.Settings.StudioPresets;
            s.VideoFolder = VideoFolderBox.Text.Trim();
            s.FfmpegPath = App.Settings.FfmpegPath; // kann im Dialog geändert worden sein

            if (!App.Instance.ApplySettings(s))
            {
                MessageBox.Show(this,
                    $"Das Tastenkürzel „{HotkeyText.Describe(vk, mods)}“ konnte nicht registriert werden – " +
                    "es wird vermutlich schon von einem anderen Programm (oder vom Windows-Snipping-Tool) verwendet.\n\n" +
                    "Tipp: Für die Druck-Taste in den Windows-Einstellungen unter Barrierefreiheit › Tastatur " +
                    "„Druck-Taste zum Öffnen des Bildschirmausschnitts verwenden“ deaktivieren.",
                    "SnipSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Close();
        }
    }
}
