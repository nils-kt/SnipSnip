using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SnipSnip
{
    /// <summary>Ablauf einer Videoaufnahme: Countdown → Aufnahme mit Steuerleiste → Verarbeitung → Aktion danach.</summary>
    internal sealed class RecordingSession
    {
        public static RecordingSession? Current { get; private set; }

        readonly Rect region;
        readonly AfterCapture action;
        readonly Window? hidden;
        ScreenRecorder? recorder;
        RecordingBar? bar;
        RegionFrame? frame;
        DispatcherTimer? timer;
        bool stopping;

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
            try
            {
                if (App.Settings.RecordingCountdown)
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

                recorder = new ScreenRecorder(region);
                recorder.Failed += ex => Application.Current.Dispatcher.InvokeAsync(() => Fail(ex));
                recorder.Start();

                frame = new RegionFrame(new Rect(region.X, region.Y, recorder.Width, recorder.Height));
                frame.Show();
                bar = new RecordingBar(this, region, App.Settings.RecordSystemAudio, App.Settings.RecordMicrophone);
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

        /// <summary>Countdown-Fenster mittig über dem Aufnahmebereich platzieren.</summary>
        static void CenterOn(Window w, Rect phys)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            double s = VisualTreeHelper.GetDpi(w).DpiScaleX;
            int ww = (int)(w.ActualWidth * s), wh = (int)(w.ActualHeight * s);
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

            var toast = Toast.Notify("Video wird verarbeitet …", $"{recorder.Width} × {recorder.Height} · {Format(recorder.Elapsed)}", null, null, 0);
            toast.SetProgress(true);
            try
            {
                string path = await recorder.StopAsync();
                toast.Update("Aufnahme fertig", "", null, 1);
                End();
                await App.Instance.HandleRecording(path, action);
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
            Toast.Notify("Aufnahme verworfen", "", null, null, 3);
            End();
        }

        void Fail(Exception ex)
        {
            if (stopping) return;
            stopping = true;
            CloseUi();
            _ = recorder?.DiscardAsync();
            Toast.Notify("Aufnahme fehlgeschlagen", ex.Message, null, App.Instance.ShowSettings, 12);
            End();
        }

        void CloseUi()
        {
            timer?.Stop();
            bar?.Close();
            frame?.Close();
            bar = null;
            frame = null;
        }

        void End()
        {
            Current = null;
            if (hidden != null)
            {
                hidden.Show();
                hidden.Activate();
            }
        }

        public static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
