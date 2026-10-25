using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnipSnip
{
    /// <summary>Ablauf einer Videoaufnahme: Vorbereitung → Countdown → Aufnahme mit Steuerleiste → Studio.</summary>
    internal sealed class RecordingSession
    {
        public static RecordingSession? Current { get; private set; }

        readonly Rect region;
        readonly AfterCapture action;
        readonly Window? hidden;
        ScreenRecorder? recorder;
        WebcamCapture? webcam;
        RecordingBar? bar;
        RegionFrame? frame;
        SpeakerNotesWindow? notes;
        DispatcherTimer? timer;
        bool stopping;
        string? dir;

        RecordingSession(Rect physicalRegion, AfterCapture action, Window? hidden)
        {
            region = physicalRegion;
            this.action = action;
            this.hidden = hidden;
        }

        public static void Start(Rect physicalRegion, AfterCapture action, Window? hidden)
        {
            if (Current != null) return;
            Current = new RecordingSession(physicalRegion, action, hidden);
            Current.Begin();
        }

        async void Begin()
        {
            var s = App.Settings;
            try
            {
                dir = StudioProject.NewDir();
                if (s.HideDesktopIcons) DesktopIcons.Hide();

                // Webcam vorab starten, damit sie bei Aufnahmebeginn bereits Bilder liefert
                if (s.RecordWebcam && !string.IsNullOrWhiteSpace(s.WebcamDevice))
                {
                    webcam = new WebcamCapture(Path.Combine(dir, "webcam.mkv"));
                    webcam.Start(s.WebcamDevice, 30);
                }

                if (s.ShowSpeakerNotes)
                {
                    notes = new SpeakerNotesWindow(region);
                    notes.Show();
                }

                if (s.RecordingCountdown)
                {
                    var cd = new CountdownWindow();
                    cd.Show();
                    CenterOn(cd, region);
                    for (int i = 3; i > 0; i--)
                    {
                        cd.Set(i);
                        await Task.Delay(800);
                    }
                    cd.Close();
                    await Task.Delay(150);
                }

                if (webcam != null && !await webcam.WaitStartedAsync(TimeSpan.FromSeconds(4)))
                {
                    Toast.Notify("Webcam nicht verfügbar", $"„{s.WebcamDevice}“ konnte nicht gestartet werden – Aufnahme läuft ohne Kamera.", null, null, 6);
                    await webcam.StopAsync();
                    webcam = null;
                }

                recorder = new ScreenRecorder(region, dir, webcam);
                recorder.Failed += ex => Application.Current.Dispatcher.InvokeAsync(() => Fail(ex));
                recorder.Start();

                frame = new RegionFrame(new Rect(region.X, region.Y, recorder.Width, recorder.Height));
                frame.Show();
                bar = new RecordingBar(this, region, s.RecordSystemAudio, s.RecordMicrophone, webcam != null);
                bar.Show();

                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += (_, _) => bar?.UpdateTime(recorder.Elapsed);
                timer.Start();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>Fenster mittig über dem Aufnahmebereich platzieren.</summary>
        internal static void CenterOn(Window w, Rect phys)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            double sc = VisualTreeHelper.GetDpi(w).DpiScaleX;
            int ww = (int)(w.ActualWidth * sc), wh = (int)(w.ActualHeight * sc);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)(phys.X + phys.Width / 2 - ww / 2.0),
                (int)(phys.Y + phys.Height / 2 - wh / 2.0), 0, 0, 0x0001 | Native.SWP_NOACTIVATE);
        }

        public void TogglePause()
        {
            if (recorder == null || stopping) return;
            if (recorder.IsPaused) recorder.Resume(); else recorder.Pause();
            bar?.SetPaused(recorder.IsPaused);
        }

        public async void Stop()
        {
            if (recorder == null || stopping) return;
            stopping = true;
            CloseUi();

            var toast = Toast.Notify("Aufnahme wird gespeichert …", $"{recorder.Width} × {recorder.Height} · {Format(recorder.Elapsed)}", null, null, 0);
            toast.SetProgress(true);
            try
            {
                string project = await recorder.StopAsync();
                toast.Update("Aufnahme fertig", "", null, 1);
                End();
                await App.Instance.HandleRecording(project, action);
            }
            catch (Exception ex)
            {
                toast.Update("Aufnahme fehlgeschlagen", ex.Message, null, 12);
                End();
            }
        }

        public async void Discard()
        {
            if (stopping) return;
            stopping = true;
            CloseUi();
            if (recorder != null) await recorder.DiscardAsync();
            else
            {
                if (webcam != null) await webcam.StopAsync();
                TryDeleteDir();
            }
            Toast.Notify("Aufnahme verworfen", "", null, null, 3);
            End();
        }

        void Fail(Exception ex)
        {
            if (stopping) return;
            stopping = true;
            CloseUi();
            if (recorder != null) _ = recorder.DiscardAsync();
            else
            {
                _ = webcam?.StopAsync();
                TryDeleteDir();
            }
            Toast.Notify("Aufnahme fehlgeschlagen", ex.Message, null, App.Instance.ShowSettings, 12);
            End();
        }

        void TryDeleteDir()
        {
            try { if (dir != null) Directory.Delete(dir, true); } catch { }
        }

        void CloseUi()
        {
            timer?.Stop();
            bar?.Close();
            frame?.Close();
            notes?.Close();
            bar = null;
            frame = null;
            notes = null;
            DesktopIcons.Restore();
        }

        void End()
        {
            Current = null;
            DesktopIcons.Restore();
            if (hidden != null)
            {
                hidden.Show();
                hidden.Activate();
            }
        }

        public static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>Sprechernotizen während der Aufnahme – nur für dich sichtbar, nicht im Video.</summary>
    internal sealed class SpeakerNotesWindow : Window
    {
        public SpeakerNotesWindow(Rect region)
        {
            Title = "SnipSnip Notizen";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Width = 360;
            Height = 260;
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

            var box = new TextBox
            {
                Text = App.Settings.SpeakerNotes,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                CaretBrush = Brushes.White,
                FontSize = 17,
                Margin = new Thickness(0, 6, 0, 0)
            };
            box.TextChanged += (_, _) => App.Settings.SpeakerNotes = box.Text;
            var header = new TextBlock
            {
                Text = "Notizen  ·  nur für dich sichtbar",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0xA8)),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.SizeAll
            };
            header.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
            var dock = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            dock.Children.Add(box);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x17, 0x17, 0x1B)),
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 10),
                Child = dock
            };

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Win.ExcludeFromCapture(hwnd);
                // rechts oben auf dem Monitor des Aufnahmebereichs
                var mons = ScreenCapture.GetMonitors();
                var center = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
                var mon = mons.Find(m => m.Contains(center));
                if (mon.Width <= 0) mon = ScreenCapture.PrimaryMonitor();
                double sc = VisualTreeHelper.GetDpi(this).DpiScaleX;
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)(mon.Right - Width * sc - 24), (int)(mon.Top + 80), 0, 0, 0x0001 | Native.SWP_NOACTIVATE);
            };
            Closed += (_, _) => App.Settings.Save();
        }
    }
}
