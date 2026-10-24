using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace SnipSnip
{
    public partial class VideoEditorWindow : Window
    {
        sealed class Seg
        {
            public TimeSpan Start, End;
            public bool Removed;
            public Seg Clone() => new() { Start = Start, End = End, Removed = Removed };
        }

        readonly string path;
        readonly bool isTempRecording;
        MediaInfo info = new(TimeSpan.Zero, 0, 0, false);
        List<Seg> segs = new();
        int selected = -1;
        readonly Stack<List<Seg>> undo = new(), redo = new();
        readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
        readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        bool playing, scrubbing, muted, loaded, savedOrShared, syncing;
        TimeSpan pos;
        CancellationTokenSource? busyCts;
        string? tempExport, tempExportSig;
        BitmapSource? firstThumb;

        static readonly Brush HatchBrush = CreateHatch();
        static readonly TimeSpan MinSeg = TimeSpan.FromMilliseconds(80);

        public VideoEditorWindow(string videoPath, bool isTempRecording)
        {
            InitializeComponent();
            path = videoPath;
            this.isTempRecording = isTempRecording;
            Title = $"SnipSnip – {IOPath.GetFileName(path)}";
            UploadBtn.ToolTip = $"Zu {Uploader.Name(App.Settings.Provider)} hochladen  (Strg+U)";

            syncing = true;
            FmtMp4.IsChecked = true;
            Size100.IsChecked = true;
            Fps15.IsChecked = true;
            AudioToggle.IsChecked = true;
            syncing = false;

            tick.Tick += (_, _) => OnTick();
            statusTimer.Tick += (_, _) => { statusTimer.Stop(); StatusHint.Text = DefaultHint; };
            Player.MediaEnded += (_, _) => { SetPlaying(false); Seek(FirstKeptStart()); };
            Player.MediaFailed += (_, e) => Flash("Vorschau nicht möglich: " + e.ErrorException?.Message);
            Timeline.MouseLeftButtonDown += Timeline_MouseDown;
            Timeline.MouseMove += Timeline_MouseMove;
            Timeline.MouseLeftButtonUp += Timeline_MouseUp;
            Timeline.SizeChanged += (_, _) =>
            {
                TimelineClip.Rect = new Rect(0, 0, Timeline.ActualWidth, Timeline.ActualHeight);
                RenderTimeline();
            };
            Drop += Window_Drop;
            Loaded += async (_, _) => await LoadAsync();
            UpdateUndoUi();
        }

        const string DefaultHint = "Leertaste = Abspielen  ·  ← → = Bild für Bild (Umschalt: 1 s)  ·  Klick auf die Zeitleiste = springen & Abschnitt markieren";

        // ================================================================== Laden

        async Task LoadAsync()
        {
            if (loaded) return;
            loaded = true;
            if (!FFmpeg.IsAvailable && !await App.Instance.EnsureFfmpegAsync(this)) { Close(); return; }

            try { info = await FFmpeg.ProbeAsync(path); }
            catch (Exception ex) { MessageBox.Show(this, "Video konnte nicht gelesen werden:\n" + ex.Message, "SnipSnip"); Close(); return; }
            if (info.Duration <= TimeSpan.Zero) { MessageBox.Show(this, "Das Video hat keine erkennbare Länge.", "SnipSnip"); Close(); return; }

            segs = new List<Seg> { new() { Start = TimeSpan.Zero, End = info.Duration } };
            syncing = true;
            AudioToggle.IsEnabled = info.HasAudio;
            AudioToggle.IsChecked = info.HasAudio;
            syncing = false;
            MuteBtn.Visibility = info.HasAudio ? Visibility.Visible : Visibility.Collapsed;

            Player.Source = new Uri(path);
            Player.Play();
            Player.Pause();
            Seek(TimeSpan.Zero);
            tick.Start();

            RenderTimeline();
            UpdateInfo();

            try
            {
                int count = Math.Clamp((int)(Timeline.ActualWidth / 96), 6, 24);
                var thumbs = await VideoExporter.ThumbnailsAsync(path, info.Duration, count, 72);
                firstThumb = thumbs.FirstOrDefault();
                SetThumbs(thumbs);
            }
            catch { /* Vorschaubilder sind optional */ }
        }

        void SetThumbs(IEnumerable<BitmapSource> thumbs)
        {
            Thumbs.Children.Clear();
            foreach (var t in thumbs)
                Thumbs.Children.Add(new Image { Source = t, Stretch = Stretch.UniformToFill });
        }

        // ================================================================== Zeitleiste

        double X(TimeSpan t) => info.Duration.TotalSeconds <= 0 ? 0 : t.TotalSeconds / info.Duration.TotalSeconds * Timeline.ActualWidth;

        TimeSpan T(double x) => TimeSpan.FromSeconds(Math.Clamp(x / Math.Max(1, Timeline.ActualWidth), 0, 1) * info.Duration.TotalSeconds);

        static Brush CreateHatch()
        {
            var g = new GeometryGroup();
            g.Children.Add(new LineGeometry(new Point(0, 10), new Point(10, 0)));
            var b = new DrawingBrush(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0x4D, 0x4F)), 2), g))
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 10, 10),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 10, 10),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            b.Freeze();
            return b;
        }

        void RenderTimeline()
        {
            SegLayer.Children.Clear();
            double hgt = Timeline.ActualHeight;
            if (hgt <= 0 || info.Duration <= TimeSpan.Zero) return;

            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                double x0 = X(s.Start), x1 = X(s.End);
                if (s.Removed)
                {
                    AddRect(x0, x1, new SolidColorBrush(Color.FromArgb(0xC0, 0x0C, 0x0C, 0x0E)), null, 0);
                    AddRect(x0, x1, HatchBrush, null, 0);
                }
                if (i == selected)
                    AddRect(x0 + 1.5, x1 - 1.5, new SolidColorBrush(Color.FromArgb(0x30, 0x4F, 0x8C, 0xFF)), (Brush)FindResource("AccentBrush"), 3);
                if (i > 0)
                {
                    var cut = new Rectangle { Width = 3, Height = hgt, Fill = Brushes.White, Opacity = 0.9 };
                    Canvas.SetLeft(cut, x0 - 1.5);
                    SegLayer.Children.Add(cut);
                }
            }
            UpdatePlayhead();
        }

        void AddRect(double x0, double x1, Brush fill, Brush? stroke, double thickness)
        {
            var r = new Rectangle
            {
                Width = Math.Max(0, x1 - x0),
                Height = Timeline.ActualHeight,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = thickness,
                RadiusX = stroke != null ? 6 : 0,
                RadiusY = stroke != null ? 6 : 0
            };
            Canvas.SetLeft(r, x0);
            SegLayer.Children.Add(r);
        }

        void UpdatePlayhead()
        {
            double x = X(pos);
            Canvas.SetLeft(Playhead, x - 1);
            Canvas.SetLeft(PlayheadKnob, x - 6);
            Playhead.Height = Timeline.ActualHeight;
            UpdateTime();
        }

        void UpdateTime()
        {
            TimeText.Text = $"{Fmt(pos)}  /  {Fmt(info.Duration)}";
        }

        static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");

        void UpdateInfo()
        {
            var kept = KeptDuration();
            var o = CurrentOptions;
            int ow = (int)(info.Width * o.Scale) & ~1, oh = (int)(info.Height * o.Scale) & ~1;
            OutInfo.Text = $"→  {Fmt(kept)}  ·  {ow} × {oh}" + (o.Gif ? $"  ·  {o.GifFps} fps" : "");
            StatusInfo.Text = $"Original {Fmt(info.Duration)} · {info.Width} × {info.Height}" + (info.HasAudio ? " · mit Ton" : " · ohne Ton");
            GifPanel.Visibility = o.Gif ? Visibility.Visible : Visibility.Collapsed;
            AudioToggle.Visibility = o.Gif ? Visibility.Collapsed : Visibility.Visible;
        }

        int SegIndexAt(TimeSpan t)
        {
            for (int i = 0; i < segs.Count; i++)
                if (t >= segs[i].Start && t < segs[i].End) return i;
            return segs.Count - 1;
        }

        TimeSpan KeptDuration() => TimeSpan.FromTicks(segs.Where(s => !s.Removed).Sum(s => (s.End - s.Start).Ticks));

        TimeSpan FirstKeptStart() => segs.FirstOrDefault(s => !s.Removed)?.Start ?? TimeSpan.Zero;

        List<(TimeSpan Start, TimeSpan End)> Keep() => segs.Where(s => !s.Removed).Select(s => (s.Start, s.End)).ToList();

        // ================================================================== Abspielen

        void Seek(TimeSpan t)
        {
            pos = TimeSpan.FromTicks(Math.Clamp(t.Ticks, 0, info.Duration.Ticks));
            Player.Position = pos;
            UpdatePlayhead();
        }

        void SetPlaying(bool on)
        {
            playing = on;
            if (on) Player.Play(); else Player.Pause();
            PlayBtn.Tag = FindResource(on ? "IconPause" : "IconPlay");
        }

        void TogglePlay()
        {
            if (playing) { SetPlaying(false); return; }
            int i = SegIndexAt(pos);
            if (segs[i].Removed || pos >= info.Duration - TimeSpan.FromMilliseconds(50))
            {
                var next = segs.Skip(i).FirstOrDefault(s => !s.Removed && s.Start >= pos) ?? segs.First(s => !s.Removed);
                Seek(next.Start);
            }
            SetPlaying(true);
        }

        void OnTick()
        {
            if (!playing || scrubbing) return;
            pos = Player.Position;
            int i = SegIndexAt(pos);
            if (segs[i].Removed)
            {
                var next = segs.Skip(i + 1).FirstOrDefault(s => !s.Removed);
                if (next == null) { SetPlaying(false); Seek(FirstKeptStart()); return; }
                Seek(next.Start);
            }
            else if (i == segs.FindLastIndex(s => !s.Removed) && pos >= segs[i].End - TimeSpan.FromMilliseconds(40))
            {
                SetPlaying(false);
                Seek(FirstKeptStart());
                return;
            }
            UpdatePlayhead();
        }

        void Step(double seconds)
        {
            if (playing) SetPlaying(false);
            Seek(pos + TimeSpan.FromSeconds(seconds));
        }

        void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (info.Duration <= TimeSpan.Zero) return;
            scrubbing = true;
            Timeline.CaptureMouse();
            var t = T(e.GetPosition(Timeline).X);
            selected = SegIndexAt(t);
            Seek(t);
            RenderTimeline();
            UpdateButtons();
        }

        void Timeline_MouseMove(object sender, MouseEventArgs e)
        {
            if (!scrubbing) return;
            Seek(T(e.GetPosition(Timeline).X));
        }

        void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
        {
            scrubbing = false;
            Timeline.ReleaseMouseCapture();
            if (playing) Player.Play();
        }

        void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

        void Mute_Click(object sender, RoutedEventArgs e)
        {
            muted = !muted;
            Player.IsMuted = muted;
            MuteBtn.Tag = FindResource(muted ? "IconMute" : "IconSpeaker");
        }

        // ================================================================== Schneiden

        void PushUndo()
        {
            undo.Push(segs.Select(s => s.Clone()).ToList());
            redo.Clear();
            UpdateUndoUi();
        }

        void UpdateUndoUi()
        {
            UndoBtn.IsEnabled = undo.Count > 0;
            RedoBtn.IsEnabled = redo.Count > 0;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            RemoveBtn.IsEnabled = selected >= 0 && selected < segs.Count && segs.Count > 1;
            RemoveBtn.Content = selected >= 0 && selected < segs.Count && segs[selected].Removed ? "Wiederherstellen" : "Abschnitt";
        }

        void Changed()
        {
            RenderTimeline();
            UpdateInfo();
            UpdateButtons();
        }

        /// <summary>Teilt den Abschnitt an t. Gibt den Index des rechten Teils zurück (oder -1).</summary>
        int SplitAt(TimeSpan t)
        {
            int i = SegIndexAt(t);
            var s = segs[i];
            if (t - s.Start < MinSeg || s.End - t < MinSeg) return t - s.Start < MinSeg ? i : i + 1;
            segs.Insert(i + 1, new Seg { Start = t, End = s.End, Removed = s.Removed });
            s.End = t;
            return i + 1;
        }

        void Split()
        {
            PushUndo();
            int i = SplitAt(pos);
            selected = Math.Min(i, segs.Count - 1);
            Changed();
            Flash("Geteilt bei " + Fmt(pos) + " – Abschnitt markieren und mit Entf entfernen");
        }

        void TrimStart()
        {
            if (pos <= MinSeg) return;
            PushUndo();
            int i = SplitAt(pos);
            for (int k = 0; k < i && k < segs.Count; k++) segs[k].Removed = true;
            selected = Math.Min(i, segs.Count - 1);
            Changed();
        }

        void TrimEnd()
        {
            if (info.Duration - pos <= MinSeg) return;
            PushUndo();
            int i = SplitAt(pos);
            for (int k = i; k < segs.Count; k++) segs[k].Removed = true;
            selected = Math.Max(0, i - 1);
            Changed();
        }

        void ToggleRemove()
        {
            if (selected < 0 || selected >= segs.Count) return;
            var s = segs[selected];
            if (!s.Removed && segs.Count(x => !x.Removed) <= 1)
            {
                Flash("Mindestens ein Abschnitt muss übrig bleiben.");
                return;
            }
            PushUndo();
            s.Removed = !s.Removed;
            Changed();
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            redo.Push(segs);
            segs = undo.Pop();
            selected = Math.Min(selected, segs.Count - 1);
            UpdateUndoUi();
            Changed();
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            undo.Push(segs);
            segs = redo.Pop();
            selected = Math.Min(selected, segs.Count - 1);
            UpdateUndoUi();
            Changed();
        }

        void Split_Click(object sender, RoutedEventArgs e) => Split();
        void TrimStart_Click(object sender, RoutedEventArgs e) => TrimStart();
        void TrimEnd_Click(object sender, RoutedEventArgs e) => TrimEnd();
        void Remove_Click(object sender, RoutedEventArgs e) => ToggleRemove();
        void Undo_Click(object sender, RoutedEventArgs e) => Undo();
        void Redo_Click(object sender, RoutedEventArgs e) => Redo();

        // ================================================================== Export

        ExportOptions CurrentOptions => new()
        {
            Gif = FmtGif.IsChecked == true,
            Scale = Size50.IsChecked == true ? 0.5 : Size75.IsChecked == true ? 0.75 : 1,
            Audio = AudioToggle.IsChecked == true,
            GifFps = Fps10.IsChecked == true ? 10 : Fps24.IsChecked == true ? 24 : 15
        };

        void Options_Changed(object sender, RoutedEventArgs e)
        {
            if (syncing || !IsLoaded) return;
            // GIFs in voller Auflösung werden riesig -> beim Umschalten sinnvoll verkleinern
            if (sender == FmtGif && info.Width > 1000 && Size100.IsChecked == true)
            {
                syncing = true;
                Size50.IsChecked = true;
                syncing = false;
            }
            UpdateInfo();
        }

        string Signature(ExportOptions o) =>
            string.Join("|", Keep().Select(k => $"{k.Start.Ticks}-{k.End.Ticks}")) + $"|{o.Gif}|{o.Scale}|{o.Audio}|{o.GifFps}";

        /// <summary>Exportiert nach target (oder in eine Temp-Datei). Gibt null zurück bei Abbruch/Fehler.</summary>
        async Task<string?> ExportAsync(string? target)
        {
            var o = CurrentOptions;
            string sig = Signature(o);
            if (target == null && tempExport != null && tempExportSig == sig && File.Exists(tempExport))
                return tempExport;

            string ext = o.Gif ? "gif" : "mp4";
            bool isTemp = target == null;
            if (isTemp)
            {
                string dir = IOPath.Combine(IOPath.GetTempPath(), "SnipSnip", $"export_{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                target = IOPath.Combine(dir, $"SnipSnip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}");
            }

            if (playing) SetPlaying(false);
            busyCts = new CancellationTokenSource();
            BusyTitle.Text = o.Gif ? "GIF wird erstellt …" : "Video wird exportiert …";
            BusyBar.Value = 0;
            BusyText.Text = "0 %";
            BusyPanel.Visibility = Visibility.Visible;
            SetToolbarEnabled(false);
            try
            {
                await VideoExporter.ExportAsync(path, info, Keep(), target!, o,
                    p => Dispatcher.InvokeAsync(() => { BusyBar.Value = p; BusyText.Text = $"{p * 100:0} %"; }),
                    busyCts.Token);
                if (isTemp) { tempExport = target; tempExportSig = sig; }
                return target;
            }
            catch (OperationCanceledException)
            {
                TryDelete(target!);
                return null;
            }
            catch (Exception ex)
            {
                TryDelete(target!);
                MessageBox.Show(this, ex.Message, "SnipSnip – Export fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            finally
            {
                BusyPanel.Visibility = Visibility.Collapsed;
                SetToolbarEnabled(true);
                busyCts.Dispose();
                busyCts = null;
            }
        }

        static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        void SetToolbarEnabled(bool on)
        {
            foreach (var b in new UIElement[] { CopyBtn, SaveBtn, UploadBtn, SplitBtn, TrimStartBtn, TrimEndBtn, RemoveBtn, UndoBtn, RedoBtn })
                b.IsEnabled = on;
            if (on) UpdateUndoUi();
        }

        void CancelBusy_Click(object sender, RoutedEventArgs e) => busyCts?.Cancel();

        async Task CopyAsync()
        {
            var f = await ExportAsync(null);
            if (f == null) return;
            var list = new StringCollection { f };
            try { Clipboard.SetFileDropList(list); } catch { }
            savedOrShared = true;
            Flash("✓  Videodatei kopiert – z. B. in Discord, Teams oder den Explorer einfügen");
        }

        async Task SaveAsync()
        {
            var o = CurrentOptions;
            string ext = o.Gif ? "gif" : "mp4";
            var dlg = new SaveFileDialog
            {
                Title = "Video speichern",
                Filter = o.Gif ? "GIF-Animation (*.gif)|*.gif" : "MP4-Video (*.mp4)|*.mp4",
                FileName = $"SnipSnip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}",
                AddExtension = true
            };
            try
            {
                Directory.CreateDirectory(App.Settings.VideoFolder);
                dlg.InitialDirectory = App.Settings.VideoFolder;
            }
            catch { }
            if (dlg.ShowDialog(this) != true) return;
            var f = await ExportAsync(dlg.FileName);
            if (f == null) return;
            savedOrShared = true;
            Flash("✓  Gespeichert: " + f);
        }

        async Task UploadAsync()
        {
            var f = await ExportAsync(null);
            if (f == null) return;
            savedOrShared = true;
            Flash("Wird hochgeladen …");
            await App.Instance.UploadFileAsync(f, firstThumb);
        }

        async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAsync();
        async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
        async void Upload_Click(object sender, RoutedEventArgs e) => await UploadAsync();
        void New_Click(object sender, RoutedEventArgs e) => App.Instance.StartCapture(this);
        void Open_Click(object sender, RoutedEventArgs e) => App.Instance.OpenFile(this);

        void Flash(string msg)
        {
            StatusHint.Text = msg;
            statusTimer.Stop();
            statusTimer.Start();
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files) App.Instance.OpenPath(f);
        }

        // ================================================================== Tastatur & Schließen

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || BusyPanel.Visibility == Visibility.Visible) return;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;

            if (ctrl)
            {
                switch (key)
                {
                    case Key.Z when shift: Redo(); break;
                    case Key.Z: Undo(); break;
                    case Key.Y: Redo(); break;
                    case Key.C: await CopyAsync(); break;
                    case Key.S: await SaveAsync(); break;
                    case Key.U: await UploadAsync(); break;
                    case Key.N: App.Instance.StartCapture(this); break;
                    case Key.O: App.Instance.OpenFile(this); break;
                    default: e.Handled = false; break;
                }
                return;
            }

            switch (key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Left: Step(shift ? -1 : -1.0 / 30); break;
                case Key.Right: Step(shift ? 1 : 1.0 / 30); break;
                case Key.Home: Seek(TimeSpan.Zero); break;
                case Key.End: Seek(info.Duration); break;
                case Key.S: Split(); break;
                case Key.A: TrimStart(); break;
                case Key.E: TrimEnd(); break;
                case Key.Delete:
                case Key.Back: ToggleRemove(); break;
                default: e.Handled = false; break;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            busyCts?.Cancel();
            if (isTempRecording && !savedOrShared && loaded && !docsMode)
            {
                var r = MessageBox.Show(this, "Die Aufnahme wurde noch nicht gespeichert, kopiert oder hochgeladen.\nVerwerfen?",
                    "SnipSnip", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            }
            tick.Stop();
            Player.Close();
            base.OnClosing(e);
        }

        // ================================================================== Doku-Bilder

        bool docsMode;

        /// <summary>Für DocRenderer: Zustand ohne echtes Video herstellen.</summary>
        internal void DocsSetup(BitmapSource poster, IEnumerable<BitmapSource> thumbs, TimeSpan duration, int w, int h,
            IEnumerable<(double Start, double End, bool Removed)> segments, double position, int selectedIndex)
        {
            docsMode = true;
            loaded = true;
            info = new MediaInfo(duration, w, h, true);
            segs = segments.Select(s => new Seg { Start = TimeSpan.FromSeconds(s.Start), End = TimeSpan.FromSeconds(s.End), Removed = s.Removed }).ToList();
            selected = selectedIndex;
            pos = TimeSpan.FromSeconds(position);
            Poster.Source = poster;
            Poster.Visibility = Visibility.Visible;
            Player.Visibility = Visibility.Collapsed;
            SetThumbs(thumbs);
            undo.Push(segs.Select(s => s.Clone()).ToList());
            UpdateUndoUi();
            UpdateInfo();
            Title = "SnipSnip – Aufnahme.mp4";
        }

        internal void DocsRefresh()
        {
            TimelineClip.Rect = new Rect(0, 0, Timeline.ActualWidth, Timeline.ActualHeight);
            RenderTimeline();
        }
    }
}
