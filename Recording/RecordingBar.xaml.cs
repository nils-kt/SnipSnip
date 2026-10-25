using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SnipSnip
{
    /// <summary>Schwebende Steuerleiste während der Aufnahme (wird selbst nicht mit aufgenommen).</summary>
    public partial class RecordingBar : Window
    {
        readonly RecordingSession session;
        readonly Rect region; // physisch
        DoubleAnimation? blink;

        internal RecordingBar(RecordingSession s, Rect physicalRegion, bool systemAudio, bool mic, bool webcam = false)
        {
            InitializeComponent();
            session = s;
            region = physicalRegion;
            if (systemAudio) AddAudioIcon("IconSpeaker", "System-Ton wird aufgenommen");
            if (mic) AddAudioIcon("IconMic", "Mikrofon wird aufgenommen");
            if (webcam) AddAudioIcon("IconWebcam", "Webcam wird aufgenommen");
            AudioIcons.Visibility = systemAudio || mic || webcam ? Visibility.Visible : Visibility.Collapsed;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Win.ExcludeFromCapture(hwnd);
                Win.AddExStyle(hwnd, Win.WS_EX_NOACTIVATE); // nicht den Fokus der aufgenommenen App stehlen
            };
            Loaded += (_, _) => { Place(); SetPaused(false); };
        }

        void AddAudioIcon(string key, string tip)
        {
            AudioIcons.Children.Add(new Viewbox
            {
                Width = 15,
                Height = 15,
                Margin = new Thickness(3, 0, 3, 0),
                ToolTip = tip,
                Child = new Path
                {
                    Width = 24,
                    Height = 24,
                    Data = (Geometry)FindResource(key),
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                }
            });
        }

        /// <summary>Unter dem Bereich platzieren – oder darüber bzw. innen, wenn kein Platz ist.</summary>
        void Place()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int bw = (int)(ActualWidth * scale), bh = (int)(ActualHeight * scale);
            var mons = ScreenCapture.GetMonitors();
            var center = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
            Rect mon = mons.Find(m => m.Contains(center));
            if (mon.Width <= 0) mon = ScreenCapture.PrimaryMonitor();

            double px = Math.Clamp(center.X - bw / 2.0, mon.Left, mon.Right - bw);
            double py;
            if (region.Bottom + bh + 4 <= mon.Bottom) py = region.Bottom + 4;
            else if (region.Top - bh - 4 >= mon.Top) py = region.Top - bh - 4;
            else py = region.Bottom - bh - 16; // innen – ist ohnehin aus der Aufnahme ausgeblendet
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)px, (int)py, 0, 0, 0x0001 | Native.SWP_NOACTIVATE);
        }

        public void UpdateTime(TimeSpan t) =>
            TimeText.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        public void SetPaused(bool paused)
        {
            PauseBtn.Tag = FindResource(paused ? "IconPlay" : "IconPause");
            PauseBtn.ToolTip = paused ? "Fortsetzen" : "Pause";
            if (paused)
            {
                Dot.BeginAnimation(OpacityProperty, null);
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
                Dot.Opacity = 1;
            }
            else
            {
                Dot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F));
                blink ??= new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                Dot.BeginAnimation(OpacityProperty, blink);
            }
        }

        void Bar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        void Pause_Click(object sender, RoutedEventArgs e) => session.TogglePause();
        void Stop_Click(object sender, RoutedEventArgs e) => session.Stop();
        void Discard_Click(object sender, RoutedEventArgs e) => session.Discard();
    }

    /// <summary>Gestrichelter Rahmen um den Aufnahmebereich – klick-durchlässig und nicht im Video.</summary>
    internal sealed class RegionFrame : Window
    {
        const int Pad = 6; // physische Pixel außerhalb des Bereichs
        readonly Rect region;

        public RegionFrame(Rect physicalRegion)
        {
            region = physicalRegion;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            Content = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4F)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                Margin = new Thickness(0)
            };
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Win.ExcludeFromCapture(hwnd);
                Win.AddExStyle(hwnd, Win.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW);
                Apply(hwnd);
            };
            Loaded += (_, _) => Apply(new WindowInteropHelper(this).Handle);
            DpiChanged += (_, _) => Dispatcher.InvokeAsync(() => Apply(new WindowInteropHelper(this).Handle));
        }

        void Apply(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)region.X - Pad, (int)region.Y - Pad,
                (int)region.Width + 2 * Pad, (int)region.Height + 2 * Pad, Native.SWP_NOACTIVATE);
        }
    }
}
