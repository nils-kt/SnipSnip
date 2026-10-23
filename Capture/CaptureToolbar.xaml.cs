using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SnipSnip
{
    /// <summary>Die Leiste, die oben zentriert erscheint, sobald das Tastenkürzel gedrückt wird.</summary>
    public partial class CaptureToolbar : UserControl
    {
        readonly CaptureSession session;
        bool syncing;

        public CaptureToolbar(CaptureSession s)
        {
            InitializeComponent();
            session = s;
            Sync();
            Loaded += (_, _) =>
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Slide.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(-70, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                Bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
                HintPill.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(150) });
            };
        }

        public void Sync()
        {
            syncing = true;
            ModeRect.IsChecked = session.Mode == CaptureMode.Rectangle;
            ModeWindow.IsChecked = session.Mode == CaptureMode.Window;
            ModeFree.IsChecked = session.Mode == CaptureMode.Freeform;
            ModeFull.IsChecked = session.Mode == CaptureMode.Fullscreen;

            ActEditor.IsChecked = session.Action == AfterCapture.Editor;
            ActCopy.IsChecked = session.Action == AfterCapture.Copy;
            ActSave.IsChecked = session.Action == AfterCapture.Save;
            ActUpload.IsChecked = session.Action == AfterCapture.Upload;
            ActUpload.ToolTip = $"Direkt zu {Uploader.Name(App.Settings.Provider)} hochladen – Link landet in der Zwischenablage";

            Hint.Text = session.Mode switch
            {
                CaptureMode.Window => "Fenster anklicken   ·   Esc = Abbrechen",
                CaptureMode.Freeform => "Form mit gedrückter Maustaste zeichnen   ·   Esc = Abbrechen",
                CaptureMode.Fullscreen => "Monitor anklicken   ·   Strg + Klick = alle Monitore   ·   Esc = Abbrechen",
                _ => "Bereich aufziehen   ·   Klick = Fenster   ·   Pfeiltasten = pixelgenau   ·   C = Farbe kopieren   ·   Esc = Abbrechen"
            };
            syncing = false;
        }

        void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (syncing) return;
            var m = sender == ModeWindow ? CaptureMode.Window
                  : sender == ModeFree ? CaptureMode.Freeform
                  : sender == ModeFull ? CaptureMode.Fullscreen
                  : CaptureMode.Rectangle;
            session.SetMode(m);
        }

        void Act_Checked(object sender, RoutedEventArgs e)
        {
            if (syncing) return;
            var a = sender == ActCopy ? AfterCapture.Copy
                  : sender == ActSave ? AfterCapture.Save
                  : sender == ActUpload ? AfterCapture.Upload
                  : AfterCapture.Editor;
            session.SetAction(a);
        }

        void Delay_Click(object sender, RoutedEventArgs e)
        {
            var menu = DelayBtn.ContextMenu!;
            menu.PlacementTarget = DelayBtn;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        void DelayItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: string s } && int.TryParse(s, out int sec))
                session.StartDelay(sec);
        }

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            session.Cancel();
            App.Instance.ShowSettings();
        }

        void Close_Click(object sender, RoutedEventArgs e) => session.Cancel();

        void Swallow(object sender, MouseButtonEventArgs e) => e.Handled = true;
    }
}
