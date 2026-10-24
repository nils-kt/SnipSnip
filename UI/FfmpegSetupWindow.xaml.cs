using System;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace SnipSnip
{
    public partial class FfmpegSetupWindow : Window
    {
        CancellationTokenSource? cts;

        public FfmpegSetupWindow()
        {
            InitializeComponent();
            TargetRun.Text = FFmpeg.ManagedDir;
            DownloadBtn.Content = FFmpeg.DownloadName.Contains("arm64") ? "Herunterladen (~140 MB)" : "Herunterladen (~200 MB)";
        }

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            DownloadBtn.IsEnabled = false;
            BrowseBtn.IsEnabled = false;
            ErrorText.Visibility = Visibility.Collapsed;
            ProgressBox.Visibility = Visibility.Visible;
            ProgressText.Text = "Verbinde …";
            cts = new CancellationTokenSource();
            try
            {
                var progress = new Progress<(long Done, long? Total)>(p =>
                {
                    double mb = p.Done / 1048576.0;
                    if (p.Total is long t && t > 0)
                    {
                        Bar.IsIndeterminate = false;
                        Bar.Value = (double)p.Done / t;
                        ProgressText.Text = $"{mb:0} von {t / 1048576.0:0} MB";
                    }
                    else
                    {
                        Bar.IsIndeterminate = true;
                        ProgressText.Text = $"{mb:0} MB";
                    }
                });
                await FFmpeg.DownloadAsync(progress, cts.Token);
                ProgressText.Text = "Fertig ✓";
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                if (IsLoaded) DialogResult = false;
            }
            catch (Exception ex)
            {
                ErrorText.Text = "Download fehlgeschlagen: " + ex.Message;
                ErrorText.Visibility = Visibility.Visible;
                ProgressBox.Visibility = Visibility.Collapsed;
                DownloadBtn.IsEnabled = true;
                BrowseBtn.IsEnabled = true;
            }
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "ffmpeg.exe auswählen", Filter = "ffmpeg.exe|ffmpeg.exe|Programme (*.exe)|*.exe" };
            if (dlg.ShowDialog(this) != true) return;
            if (!File.Exists(dlg.FileName)) return;
            App.Settings.FfmpegPath = dlg.FileName;
            App.Settings.Save();
            FFmpeg.ResetCache();
            DialogResult = FFmpeg.IsAvailable;
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (cts != null) cts.Cancel();
            else DialogResult = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            cts?.Cancel();
            base.OnClosed(e);
        }
    }
}
