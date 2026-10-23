using System.Windows;
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
