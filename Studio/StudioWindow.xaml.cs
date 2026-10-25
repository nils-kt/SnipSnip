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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SkiaSharp;
using IOPath = System.IO.Path;
using WinForms = System.Windows.Forms;

namespace SnipSnip
{
    public partial class StudioWindow : Window
    {
        readonly StudioProject p;
        readonly StudioPlayer player;
        readonly Stack<ProjectEdits> undo = new(), redo = new();
        readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
        string? lastUndoKey;
        DateTime lastUndoAt;
        string tab = "background";
        bool pickFocus;
        // Maske in der Vorschau ziehen (Maus wird auf dem Overlay festgehalten – die Rahmen selbst werden neu aufgebaut)
        MaskSegment? dragMask;
        bool dragResize, dragMoved;
        Point dragStart;
        (double X, double Y, double W, double H) dragBase;
        double dragT;
        CancellationTokenSource? trackCts;
        string? trackStatus;
        CancellationTokenSource? exportCts;
        static readonly Dictionary<string, BitmapSource> TileCache = new();

        public StudioWindow(StudioProject project)
        {
            InitializeComponent();
            p = project;
            ProjectName.Text = p.Name;
            Title = $"SnipSnip Studio – {p.Name}";

            player = new StudioPlayer(p);
            player.FrameReady += () => Preview.Source = player.Bitmap;
            player.TimeChanged += OnTime;
            player.PlaybackEnded += () => PlayBtn.Tag = FindResource("IconPlay");

            Timeline.SetProject(p);
            Timeline.ShowLayoutRow = p.HasWebcam;
            AddLayoutBtn.Visibility = p.HasWebcam ? Visibility.Visible : Visibility.Collapsed;
            Timeline.SeekRequested += src => SeekSource(src);
            Timeline.BeforeEdit += () => PushUndo("timeline-" + Timeline.SelectedKind + Timeline.SelectedIndex);
            Timeline.Edited += () => AfterChange();
            Timeline.SelectionChanged += () => { pickFocus = false; ShowTab(tab); UpdateOverlay(); };

            saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
            PreviewHost.SizeChanged += (_, _) => UpdateOverlay();
            PreviewOverlay.MouseLeftButtonDown += Overlay_MouseDown;
            PreviewOverlay.MouseMove += Overlay_MouseMove;
            PreviewOverlay.MouseLeftButtonUp += (_, _) => EndMaskDrag();
            PreviewOverlay.LostMouseCapture += (_, _) => EndMaskDrag();
            Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files) App.Instance.OpenPath(f);
            };

            BuildAspectBar();
            BuildTabs();
            ShowTab(tab);
            UpdateOutInfo();
            UpdateUndoUi();
            Loaded += async (_, _) => await LoadThumbsAsync();
        }

        // ================================================================== Zeit & Vorschau

        void OnTime(double outT)
        {
            TimeText.Text = $"{Fmt(outT)} / {Fmt(p.OutputDuration)}";
            Timeline.SetPosition(p.SourceAt(outT));
            UpdateOverlay();
        }

        void SeekSource(double src)
        {
            var o = p.OutputAt(src);
            if (o == null)
            {
                // in einem entfernten Abschnitt → zum nächsten behaltenen springen
                var next = p.Kept.FirstOrDefault(c => c.Start >= src) ?? p.Kept.LastOrDefault();
                o = next != null ? p.OutputAt(next.Start) ?? 0 : 0;
            }
            player.Seek(o.Value);
        }

        static string Fmt(double t) => t >= 3600 ? TimeSpan.FromSeconds(t).ToString(@"h\:mm\:ss\.f") : TimeSpan.FromSeconds(t).ToString(@"m\:ss\.f");

        void UpdateOutInfo()
        {
            var (w, h) = player.Renderer.OutputSize(ShortSide);
            OutInfo.Text = $"{Fmt(p.OutputDuration)}  ·  {w} × {h}";
            TimeText.Text = $"{Fmt(player.OutTime)} / {Fmt(p.OutputDuration)}";
        }

        async Task LoadThumbsAsync()
        {
            try
            {
                int count = Math.Clamp((int)(Timeline.ActualWidth / 100), 8, 24);
                var thumbs = await VideoExporter.ThumbnailsAsync(p.ScreenFile, TimeSpan.FromSeconds(p.Meta.Duration), count, 64);
                Timeline.Thumbs.Clear();
                Timeline.Thumbs.AddRange(thumbs);
                Timeline.InvalidateVisual();
            }
            catch { }
        }

        // ================================================================== Änderungen, Rückgängig, Speichern

        void PushUndo(string? key = null)
        {
            if (key != null && key == lastUndoKey && (DateTime.Now - lastUndoAt).TotalMilliseconds < 1200)
            {
                lastUndoAt = DateTime.Now;
                return;
            }
            undo.Push(p.Edits.Clone());
            redo.Clear();
            lastUndoKey = key;
            lastUndoAt = DateTime.Now;
            UpdateUndoUi();
        }

        void Change(Action apply, string? key = null)
        {
            PushUndo(key);
            apply();
            AfterChange();
        }

        void AfterChange()
        {
            player.Renderer.Rebuild();
            Timeline.InvalidateVisual();
            UpdateOutInfo();
            ScheduleSave();
            player.Refresh();
            UpdateOverlay();
        }

        void ScheduleSave()
        {
            SaveState.Text = "Nicht gespeichert …";
            saveTimer.Stop();
            saveTimer.Start();
        }

        void SaveNow()
        {
            try
            {
                p.Save();
                SaveState.Text = "Gespeichert ✓";
            }
            catch (Exception ex) { SaveState.Text = "Speichern fehlgeschlagen: " + ex.Message; }
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            redo.Push(p.Edits.Clone());
            p.Edits = undo.Pop();
            lastUndoKey = null;
            AfterUndoRedo();
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            undo.Push(p.Edits.Clone());
            p.Edits = redo.Pop();
            lastUndoKey = null;
            AfterUndoRedo();
        }

        void AfterUndoRedo()
        {
            Timeline.Select(TrackKind.None, -1);
            AfterChange();
            BuildAspectBar();
            ShowTab(tab);
            UpdateUndoUi();
        }

        void UpdateUndoUi()
        {
            UndoBtn.IsEnabled = undo.Count > 0;
            RedoBtn.IsEnabled = redo.Count > 0;
        }

        StudioStyle St => p.Edits.Style;

        // ================================================================== Kopfzeile: Seitenverhältnis

        void BuildAspectBar()
        {
            AspectBar.Children.Clear();
            foreach (var (label, value) in new[] { ("Auto", "auto"), ("16:9", "16:9"), ("4:3", "4:3"), ("1:1", "1:1"), ("4:5", "4:5"), ("9:16", "9:16") })
            {
                var rb = new RadioButton
                {
                    Content = label,
                    GroupName = "Aspect",
                    Style = (Style)FindResource("Seg"),
                    IsChecked = St.Aspect == value,
                    ToolTip = value switch { "9:16" => "Hochformat für Reels, TikTok, Shorts", "1:1" => "Quadratisch", "auto" => "Wie die Aufnahme", _ => value }
                };
                rb.Checked += (_, _) => { if (St.Aspect != value) Change(() => St.Aspect = value); };
                AspectBar.Children.Add(rb);
            }
        }

        // ================================================================== Seitenleiste

        static readonly (string Key, string Icon, string Tip)[] TabDefs =
        {
            ("background", "IconImage", "Hintergrund"),
            ("frame", "IconFrame", "Rahmen & Zuschnitt"),
            ("effects", "IconSparkle", "Effekte"),
            ("zoom", "IconZoom", "Kamera & Zoom"),
            ("cursor", "IconCursor", "Cursor"),
            ("webcam", "IconWebcam", "Webcam"),
            ("keys", "IconKeyboard", "Tastatur"),
            ("audio", "IconSpeaker", "Ton"),
            ("presets", "IconStar", "Vorlagen")
        };

        void BuildTabs()
        {
            Tabs.Children.Clear();
            foreach (var (key, icon, tip) in TabDefs)
            {
                var rb = new RadioButton
                {
                    GroupName = "Tab",
                    Style = (Style)FindResource("ToolToggle"),
                    Tag = FindResource(icon),
                    ToolTip = tip,
                    IsChecked = key == tab,
                    Margin = new Thickness(1, 0, 1, 0)
                };
                rb.Checked += (_, _) => ShowTab(key);
                Tabs.Children.Add(rb);
            }
        }

        void ShowTab(string key)
        {
            tab = key;
            Side.Children.Clear();
            AddSelectionSection();
            switch (key)
            {
                case "background": BuildBackground(); break;
                case "frame": BuildFrame(); break;
                case "effects": BuildEffects(); break;
                case "zoom": BuildZoom(); break;
                case "cursor": BuildCursor(); break;
                case "webcam": BuildWebcam(); break;
                case "keys": BuildKeys(); break;
                case "audio": BuildAudio(); break;
                case "presets": BuildPresets(); break;
            }
        }

        // ---------------------------------------------------------------- Bausteine

        void Header(string text, bool first = false) => Side.Children.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Style = (Style)FindResource("SectionHeader"),
            Margin = new Thickness(0, first ? 10 : 22, 0, 8)
        });

        void Note(string text) => Side.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = (Brush)FindResource("FgDimBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8)
        });

        void SliderRow(string label, double min, double max, double value, Action<double> set, Func<double, string> fmt, Panel? host = null)
        {
            var valueText = new TextBlock { Text = fmt(value), Foreground = (Brush)FindResource("FgDimBrush"), FontSize = 12 };
            var head = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(valueText, Dock.Right);
            head.Children.Add(valueText);
            head.Children.Add(new TextBlock { Text = label });
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Focusable = false, Margin = new Thickness(0, 4, 0, 2) };
            slider.ValueChanged += (_, e) =>
            {
                valueText.Text = fmt(e.NewValue);
                Change(() => set(e.NewValue), "slider-" + label);
            };
            var sp = new StackPanel();
            sp.Children.Add(head);
            sp.Children.Add(slider);
            (host ?? Side).Children.Add(sp);
        }

        void Toggle(string label, bool value, Action<bool> set, Panel? host = null)
        {
            var cb = new CheckBox { Content = label, IsChecked = value, Style = (Style)FindResource("DarkCheck") };
            cb.Click += (_, _) => Change(() => set(cb.IsChecked == true));
            (host ?? Side).Children.Add(cb);
        }

        void Choice<T>(string? label, (string Text, T Value)[] options, T current, Action<T> set, Panel? host = null, bool rebuildTab = false)
        {
            var target = host ?? Side;
            if (label != null) target.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var wrap = new WrapPanel();
            string group = "c" + Guid.NewGuid().ToString("N");
            foreach (var (text, value) in options)
            {
                var rb = new RadioButton
                {
                    Content = text,
                    GroupName = group,
                    Style = (Style)FindResource("Seg"),
                    IsChecked = EqualityComparer<T>.Default.Equals(value, current)
                };
                rb.Checked += (_, _) =>
                {
                    Change(() => set(value));
                    if (rebuildTab) Dispatcher.InvokeAsync(() => ShowTab(tab));
                };
                wrap.Children.Add(rb);
            }
            target.Children.Add(wrap);
        }

        Button SideButton(string text, string? icon, Action click, bool accent = false)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource(accent ? "AccentButton" : "IconButton"),
                Tag = icon != null ? FindResource(icon) : null,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 6, 0)
            };
            if (!accent) b.Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            b.Click += (_, _) => click();
            return b;
        }

        static readonly string[] SwatchColors =
            { "#FFFFFFFF", "#FF111114", "#FF4F8CFF", "#FF7C3AED", "#FFDB2777", "#FFEF4444", "#FFF97316", "#FFFACC15", "#FF22C55E", "#FF14B8A6" };

        /// <summary>Farbauswahl: feste Farbfelder plus eigener Farbdialog.</summary>
        void ColorRow(string label, string current, Action<string> set, Panel? host = null)
        {
            var target = host ?? Side;
            target.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            var wrap = new WrapPanel();
            foreach (var hex in SwatchColors)
            {
                var col = (Color)ColorConverter.ConvertFromString(hex);
                var b = new Button
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(0, 0, 5, 5),
                    Cursor = Cursors.Hand,
                    Focusable = false,
                    Template = SwatchTemplate(col, current.Equals(hex, StringComparison.OrdinalIgnoreCase))
                };
                b.Click += (_, _) => { Change(() => set(hex)); ShowTab(tab); };
                wrap.Children.Add(b);
            }
            var custom = new Button { Style = (Style)FindResource("IconButton"), Tag = FindResource("IconPalette"), ToolTip = "Eigene Farbe …", Margin = new Thickness(0, 0, 0, 5) };
            custom.Click += (_, _) =>
            {
                var sc = Backgrounds.Parse(current);
                using var dlg = new WinForms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(sc.Red, sc.Green, sc.Blue) };
                if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
                Change(() => set($"#FF{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}"));
                ShowTab(tab);
            };
            wrap.Children.Add(custom);
            target.Children.Add(wrap);
        }

        static string Pct(double v) => $"{v:0} %";
        static string Deg(double v) => $"{v:0}°";
        static string Signed(double v) => $"{v * 100:+0;-0;0}";
        static string Sec(double v) => v < 0.05 ? "Aus" : $"{v:0.0} s";
        static string X1(double v) => $"{v:0.0}×";

        // ---------------------------------------------------------------- Auswahl (Segment der Zeitleiste)

        void AddSelectionSection()
        {
            var kind = Timeline.SelectedKind;
            int i = Timeline.SelectedIndex;
            if (kind == TrackKind.None || i < 0) return;

            var box = new StackPanel();
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x4F, 0x8C, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x4F, 0x8C, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 12, 0, 4),
                Child = box
            };

            void Title(string t) => box.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 14 });

            switch (kind)
            {
                case TrackKind.Zoom when i < p.Edits.Zooms.Count:
                {
                    var z = p.Edits.Zooms[i];
                    Title($"Zoom  ·  {Fmt(z.Start)} – {Fmt(z.End)}");
                    SliderRow("Zoomstufe", 1.1, 5, z.Level, v => { z.Level = Math.Round(v, 2); z.Auto = false; }, X1, box);
                    Choice("Art", new[] { ("Klassisch", ZoomType.Classic), ("Lupe", ZoomType.Loupe) }, z.Type, v => { z.Type = v; z.Auto = false; }, box);
                    if (p.Input.HasCursor)
                        Toggle("Folgt dem Cursor", z.FollowCursor, v => { z.FollowCursor = v; z.Auto = false; Dispatcher.InvokeAsync(() => ShowTab(tab)); }, box);
                    if (!z.FollowCursor || !p.Input.HasCursor)
                    {
                        box.Children.Add(SideButton(pickFocus ? "Jetzt in die Vorschau klicken …" : "Fokuspunkt in der Vorschau wählen", "IconZoom", () =>
                        {
                            pickFocus = true;
                            PreviewHint.Text = "Klicke in der Vorschau auf den Punkt, auf den gezoomt werden soll.";
                            ShowTab(tab);
                        }));
                    }
                    box.Children.Add(SideButton("Zoom löschen", "IconTrash", () => Timeline.DeleteSelected()));
                    break;
                }
                case TrackKind.Mask when i < p.Edits.Masks.Count:
                {
                    var m = p.Edits.Masks[i];
                    Title($"Maske  ·  {Fmt(m.Start)} – {Fmt(m.End)}");
                    Choice("Art", new[] { ("Weichzeichnen", MaskKind.Blur), ("Verpixeln", MaskKind.Pixelate), ("Abdecken", MaskKind.Fill), ("Hervorheben", MaskKind.Highlight) },
                        m.Kind, v => m.Kind = v, box);
                    Choice("Bewegung", new[] { ("Fest", MaskMotion.Fixed), ("Mitbewegen", MaskMotion.Track), ("Keyframes", MaskMotion.Keyframes) },
                        m.Motion, v => SetMaskMotion(m, v), box, rebuildTab: true);
                    string help = m.Motion switch
                    {
                        MaskMotion.Track => "Die Maske folgt dem Inhalt darunter – z. B. einem Fenster, das verschoben wird. Läuft sie davon: an der Stelle anhalten und den Rahmen zurechtschieben, ab dort wird neu verfolgt.",
                        MaskMotion.Keyframes => "An verschiedenen Stellen anhalten und den Rahmen verschieben oder die Größe ändern – dazwischen gleitet die Maske weich.",
                        _ => "Den Rahmen in der Vorschau verschieben; die Ecke unten rechts ändert die Größe."
                    };
                    if (m.Motion == MaskMotion.Track)
                        box.Children.Add(SideButton(trackCts != null ? "Verfolgung läuft …" : "Ab hier neu verfolgen", "IconSearch", () =>
                        {
                            PushUndo();
                            StartTracking(m, p.SourceAt(player.OutTime), forwardOnly: false);
                        }));
                    if (m.Motion == MaskMotion.Keyframes)
                    {
                        var keyRow = new WrapPanel();
                        keyRow.Children.Add(SideButton("Keyframe hier setzen", "IconStar", () => Change(() =>
                        {
                            double t = Math.Clamp(p.SourceAt(player.OutTime), m.Start, m.End);
                            var r = m.RectAt(t);
                            m.SetKey(t, r.X, r.Y, r.W, r.H);
                        })));
                        keyRow.Children.Add(SideButton("Keyframe hier löschen", "IconTrash", () =>
                        {
                            double t = p.SourceAt(player.OutTime);
                            var k = m.Keys.OrderBy(q => Math.Abs(q.T - t)).FirstOrDefault();
                            if (k != null && Math.Abs(k.T - t) < 0.3 && m.Keys.Count > 1) Change(() => m.Keys.Remove(k));
                        }));
                        box.Children.Add(keyRow);
                    }
                    box.Children.Add(new TextBlock
                    {
                        Text = help + " „Abdecken“ füllt mit der Umgebungsfarbe, „Hervorheben“ dunkelt alles andere ab.",
                        Foreground = (Brush)FindResource("FgDimBrush"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0)
                    });
                    box.Children.Add(SideButton("Maske löschen", "IconTrash", () => Timeline.DeleteSelected()));
                    break;
                }
                case TrackKind.Text when i < p.Edits.Texts.Count:
                {
                    var s = p.Edits.Texts[i];
                    Title($"Text  ·  {Fmt(s.Start)} – {Fmt(s.End)}");
                    var tb = new TextBox
                    {
                        Style = (Style)FindResource("DarkText"),
                        Text = s.Text,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        MinHeight = 54,
                        Margin = new Thickness(0, 8, 0, 0)
                    };
                    tb.TextChanged += (_, _) => Change(() => s.Text = tb.Text, "text-" + i);
                    box.Children.Add(tb);
                    Choice("Stil", new[] { ("Titel", TextStyle.Title), ("Untertitel", TextStyle.Subtitle), ("Hinweis", TextStyle.Callout), ("Bauchbinde", TextStyle.LowerThird) },
                        s.Style, v => s.Style = v, box);
                    Choice("Position", new[] { ("Oben", TextPosition.Top), ("Mitte", TextPosition.Center), ("Unten", TextPosition.Bottom) }, s.Position, v => s.Position = v, box);
                    SliderRow("Größe", 0.5, 2.5, s.Size, v => s.Size = v, X1, box);
                    box.Children.Add(new TextBlock
                    {
                        Text = "Bauchbinde: erste Zeile ist der Name, weitere Zeilen die Beschreibung. Farbe von Hinweis und Bauchbinde: Effekte → Akzentfarbe.",
                        Foreground = (Brush)FindResource("FgDimBrush"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0)
                    });
                    box.Children.Add(SideButton("Text löschen", "IconTrash", () => Timeline.DeleteSelected()));
                    break;
                }
                case TrackKind.Layout when i < p.Edits.Layouts.Count:
                {
                    var l = p.Edits.Layouts[i];
                    Title($"Layout  ·  {Fmt(l.Start)} – {Fmt(l.End)}");
                    Choice(null, new[] { ("Nur Bildschirm", CamLayout.ScreenOnly), ("Kamera-Overlay", CamLayout.Overlay), ("Geteilt", CamLayout.Split), ("Nur Kamera", CamLayout.CameraOnly) },
                        l.Layout, v => l.Layout = v, box);
                    box.Children.Add(SideButton("Layout-Abschnitt löschen", "IconTrash", () => Timeline.DeleteSelected()));
                    break;
                }
                case TrackKind.Clip when i < p.Edits.Clips.Count:
                {
                    var c = p.Edits.Clips[i];
                    Title($"Clip  ·  {Fmt(c.Start)} – {Fmt(c.End)}");
                    Choice("Geschwindigkeit", new[] { ("0,5×", 0.5), ("1×", 1.0), ("1,5×", 1.5), ("2×", 2.0), ("3×", 3.0), ("4×", 4.0), ("8×", 8.0) },
                        c.Speed, v => { c.Speed = v; c.AutoSpeed = false; }, box);
                    box.Children.Add(SideButton(c.Removed ? "Clip wiederherstellen" : "Clip entfernen", "IconTrash", () => Timeline.DeleteSelected()));
                    break;
                }
                default:
                    return;
            }
            Side.Children.Add(card);
        }

        // ---------------------------------------------------------------- Hintergrund

        void BuildBackground()
        {
            Header("Hintergrund", true);
            Choice(null, new[] { ("Wallpaper", BackgroundKind.Wallpaper), ("Verlauf", BackgroundKind.Gradient), ("Farbe", BackgroundKind.Solid), ("Bild", BackgroundKind.Image) },
                St.Background, v => { St.Background = v; St.Preset = 0; }, rebuildTab: true);

            switch (St.Background)
            {
                case BackgroundKind.Wallpaper:
                    Tiles(Backgrounds.Wallpapers.Length, i => new StudioStyle { Background = BackgroundKind.Wallpaper, Preset = i }, i => Backgrounds.Wallpapers[i].Name);
                    break;
                case BackgroundKind.Gradient:
                    Tiles(Backgrounds.Gradients.Length, i => new StudioStyle { Background = BackgroundKind.Gradient, Preset = i }, i => Backgrounds.Gradients[i].Name);
                    break;
                case BackgroundKind.Solid:
                {
                    var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                    foreach (var hex in new[] { "#FF1E1E24", "#FF0F172A", "#FFFFFFFF", "#FFF4F4F5", "#FF4F8CFF", "#FF7C3AED", "#FFDB2777", "#FFF97316", "#FFFACC15", "#FF22C55E", "#FF14B8A6", "#FF000000" })
                    {
                        var col = (Color)ColorConverter.ConvertFromString(hex);
                        var b = new Button
                        {
                            Width = 34,
                            Height = 34,
                            Margin = new Thickness(0, 0, 6, 6),
                            Cursor = Cursors.Hand,
                            Focusable = false,
                            Template = SwatchTemplate(col, St.SolidColor.Equals(hex, StringComparison.OrdinalIgnoreCase))
                        };
                        b.Click += (_, _) => { Change(() => St.SolidColor = hex); ShowTab(tab); };
                        wrap.Children.Add(b);
                    }
                    Side.Children.Add(wrap);
                    Side.Children.Add(SideButton("Eigene Farbe …", "IconPalette", () =>
                    {
                        var c = Backgrounds.Parse(St.SolidColor);
                        using var dlg = new WinForms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(c.Red, c.Green, c.Blue) };
                        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                        {
                            Change(() => St.SolidColor = $"#FF{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
                            ShowTab(tab);
                        }
                    }));
                    break;
                }
                case BackgroundKind.Image:
                    Side.Children.Add(SideButton(St.ImagePath == null ? "Bild wählen …" : IOPath.GetFileName(St.ImagePath), "IconFolder", () =>
                    {
                        var dlg = new OpenFileDialog { Filter = "Bilder|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
                        if (dlg.ShowDialog(this) == true) { Change(() => St.ImagePath = dlg.FileName); ShowTab(tab); }
                    }));
                    SliderRow("Unschärfe", 0, 60, St.BackgroundBlur, v => St.BackgroundBlur = v, v => $"{v:0}");
                    break;
            }
        }

        static ControlTemplate SwatchTemplate(Color c, bool selected)
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(Border.BackgroundProperty, new SolidColorBrush(c));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 2.5 : 1));
            bd.SetValue(Border.BorderBrushProperty, selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
            t.VisualTree = bd;
            return t;
        }

        void Tiles(int count, Func<int, StudioStyle> style, Func<int, string> name)
        {
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 0) };
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                var st = style(i);
                string key = $"{st.Background}-{idx}";
                if (!TileCache.TryGetValue(key, out var bmp))
                {
                    using var img = Backgrounds.Render(st, 160, 100);
                    bmp = ToBitmap(img);
                    TileCache[key] = bmp;
                }
                bool sel = St.Background == st.Background && St.Preset == idx;
                var b = new Button
                {
                    Height = 66,
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = Cursors.Hand,
                    Focusable = false,
                    ToolTip = name(i),
                    Template = TileTemplate(bmp, sel)
                };
                b.Click += (_, _) => { Change(() => { St.Background = st.Background; St.Preset = idx; }); ShowTab(tab); };
                grid.Children.Add(b);
            }
            Side.Children.Add(grid);
        }

        static ControlTemplate TileTemplate(BitmapSource img, bool selected)
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetValue(Border.BackgroundProperty, new ImageBrush(img) { Stretch = Stretch.UniformToFill });
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 2.5 : 1));
            bd.SetValue(Border.BorderBrushProperty, selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
            t.VisualTree = bd;
            return t;
        }

        internal static BitmapSource ToBitmap(SKImage img)
        {
            var info = new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var buf = new byte[img.Width * img.Height * 4];
            var h = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { img.ReadPixels(info, h.AddrOfPinnedObject(), img.Width * 4, 0, 0); }
            finally { h.Free(); }
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Pbgra32, null, buf, img.Width * 4);
            bmp.Freeze();
            return bmp;
        }

        // ---------------------------------------------------------------- Rahmen

        void BuildFrame()
        {
            Header("Rahmen", true);
            SliderRow("Abstand", 0, 30, St.Padding, v => St.Padding = v, Pct);
            SliderRow("Inset (Innenrand)", 0, 10, St.Inset, v => St.Inset = v, Pct);
            SliderRow("Ecken", 0, 60, St.Radius, v => St.Radius = v, v => $"{v:0}");
            SliderRow("Randlinie", 0, 8, St.BorderWidth, v => St.BorderWidth = v, v => v < 0.05 ? "Aus" : $"{v:0.0}");
            if (St.BorderWidth > 0.05)
                Choice("Farbe der Randlinie", new[] { ("Glas", "#55FFFFFF"), ("Weiß", "#FFFFFFFF"), ("Dunkel", "#80000000"), ("Akzent", St.AccentColor) },
                    St.BorderColor, v => St.BorderColor = v);
            Header("Fensterrahmen");
            Choice(null, new[] { ("Keiner", WindowChrome.None), ("macOS hell", WindowChrome.MacLight), ("macOS dunkel", WindowChrome.MacDark), ("Windows", WindowChrome.Windows), ("Browser", WindowChrome.Browser) },
                St.Chrome, v => St.Chrome = v, rebuildTab: true);
            if (St.Chrome != WindowChrome.None)
            {
                Side.Children.Add(new TextBlock { Text = St.Chrome == WindowChrome.Browser ? "Adresse" : "Fenstertitel", Margin = new Thickness(0, 8, 0, 4) });
                var title = new TextBox { Style = (Style)FindResource("DarkText"), Text = St.ChromeTitle };
                title.TextChanged += (_, _) => Change(() => St.ChromeTitle = title.Text, "chrome-title");
                Side.Children.Add(title);
            }
            Header("Schatten");
            SliderRow("Deckkraft", 0, 100, St.ShadowOpacity * 100, v => St.ShadowOpacity = v / 100, Pct);
            SliderRow("Weichheit", 0, 100, St.ShadowBlur, v => St.ShadowBlur = v, v => $"{v:0}");
            SliderRow("Abstand", 0, 60, St.ShadowDistance, v => St.ShadowDistance = v, v => $"{v:0}");
            SliderRow("Winkel", 0, 360, St.ShadowAngle, v => St.ShadowAngle = v, v => $"{v:0}°");
            Header("Zuschneiden");
            Note("Schneidet die Aufnahme zu – z. B. um Browserleisten oder Ränder zu entfernen.");
            var e = p.Edits;
            SliderRow("Links", 0, 45, e.CropX * 100, v => { double r = e.CropX + e.CropW; e.CropX = v / 100; e.CropW = Math.Max(0.1, r - e.CropX); }, Pct);
            SliderRow("Oben", 0, 45, e.CropY * 100, v => { double b = e.CropY + e.CropH; e.CropY = v / 100; e.CropH = Math.Max(0.1, b - e.CropY); }, Pct);
            SliderRow("Rechts", 0, 45, (1 - e.CropX - e.CropW) * 100, v => e.CropW = Math.Max(0.1, 1 - v / 100 - e.CropX), Pct);
            SliderRow("Unten", 0, 45, (1 - e.CropY - e.CropH) * 100, v => e.CropH = Math.Max(0.1, 1 - v / 100 - e.CropY), Pct);
            Side.Children.Add(SideButton("Zuschnitt zurücksetzen", "IconUndo", () =>
            {
                Change(() => { e.CropX = e.CropY = 0; e.CropW = e.CropH = 1; });
                ShowTab(tab);
            }));
        }

        // ---------------------------------------------------------------- Zoom

        void BuildZoom()
        {
            Header("Automatischer Zoom", true);
            if (!p.Input.HasCursor) Note("Für importierte Videos gibt es keine Klick-Daten – Zooms bitte manuell setzen (Doppelklick in die Zoom-Spur).");
            else
            {
                Toggle("Bei Klicks automatisch hineinzoomen", St.AutoZoom, v =>
                {
                    St.AutoZoom = v;
                    p.Edits.Zooms.RemoveAll(z => z.Auto);
                    if (v) p.Edits.Zooms.AddRange(AutoZoom.Generate(p, St));
                    p.Edits.Zooms.Sort((a, b) => a.Start.CompareTo(b.Start));
                });
                Side.Children.Add(SideButton("Auto-Zooms neu erzeugen", "IconUndo", () =>
                {
                    Change(() =>
                    {
                        p.Edits.Zooms.RemoveAll(z => z.Auto);
                        p.Edits.Zooms.AddRange(AutoZoom.Generate(p, St));
                        p.Edits.Zooms.Sort((a, b) => a.Start.CompareTo(b.Start));
                    });
                    Timeline.Select(TrackKind.None, -1);
                }));
            }
            SliderRow("Standard-Zoomstufe", 1.2, 4, St.ZoomLevel, v =>
            {
                St.ZoomLevel = Math.Round(v, 2);
                foreach (var z in p.Edits.Zooms.Where(z => z.Auto)) z.Level = St.ZoomLevel;
            }, X1);
            SliderRow("Kamera-Tempo", 0.4, 2.5, St.ZoomSpeed, v => St.ZoomSpeed = v, X1);
            Header("Bewegungsunschärfe");
            Toggle("Bewegungsunschärfe bei Kamerafahrten und schnellem Cursor", St.MotionBlur, v => St.MotionBlur = v);
            SliderRow("Stärke", 0, 1, St.MotionBlurAmount, v => St.MotionBlurAmount = v, v => $"{v * 100:0} %");
            Note("Tipp: Doppelklick in die Zoom-Spur fügt einen Zoom hinzu, Ziehen verschiebt ihn, die Ränder ändern die Länge. „Lupe“ vergrößert nur einen Kreis um den Cursor.");
        }

        // ---------------------------------------------------------------- Cursor

        void BuildCursor()
        {
            Header("Cursor", true);
            if (!p.Input.HasCursor)
            {
                Note("Dieses Video wurde importiert – es enthält keine separaten Cursor-Daten.");
                return;
            }
            Toggle("Cursor anzeigen", St.ShowCursor, v => St.ShowCursor = v);
            Choice("Stil", new[]
            {
                ("Original", CursorStyle.Original), ("macOS", CursorStyle.MacOS), ("Windows", CursorStyle.Windows), ("Modern", CursorStyle.Modern),
                ("Farbig", CursorStyle.Colored), ("Punkt", CursorStyle.Dot), ("Ring", CursorStyle.Ring), ("Touch", CursorStyle.Touch)
            }, St.CursorStyle, v => St.CursorStyle = v, rebuildTab: true);
            if (St.CursorStyle is CursorStyle.Colored or CursorStyle.Dot or CursorStyle.Ring || St.CursorHighlight || St.Spotlight)
                ColorRow("Cursor-Farbe", St.CursorColor, v => St.CursorColor = v);
            SliderRow("Größe", 0.5, 4, St.CursorSize, v => St.CursorSize = v, X1);
            SliderRow("Glättung", 0, 1, St.CursorSmoothing, v => St.CursorSmoothing = v, v => $"{v * 100:0} %");
            if (St.CursorStyle == CursorStyle.Original)
                Toggle("Hochauflösender Cursor (bleibt beim Zoomen scharf)", St.HighResCursor, v => St.HighResCursor = v);
            Toggle("Leuchtkreis hinter dem Cursor", St.CursorHighlight, v => St.CursorHighlight = v);
            Toggle("In Bewegungsrichtung neigen", St.CursorTilt, v => St.CursorTilt = v);
            Toggle("Im Ruhezustand ausblenden", St.HideIdleCursor, v => St.HideIdleCursor = v);
            SliderRow("Ausblenden nach", 0.5, 8, St.IdleSeconds, v => St.IdleSeconds = v, v => $"{v:0.0} s");
            Toggle("Cursor-Loop (kehrt am Ende zum Start zurück)", St.LoopCursor, v => St.LoopCursor = v);

            Header("Klicks");
            Choice("Klick-Effekt", new[]
            {
                ("Keiner", ClickEffect.None), ("Kreis", ClickEffect.Circle), ("Welle", ClickEffect.Ripple),
                ("Puls", ClickEffect.Pulse), ("Strahlen", ClickEffect.Burst), ("Fokus", ClickEffect.Focus)
            }, St.ClickEffect, v => St.ClickEffect = v, rebuildTab: true);
            if (St.ClickEffect != ClickEffect.None)
                ColorRow("Klick-Farbe", St.ClickColor, v => St.ClickColor = v);
            Toggle("Klick-Geräusch im Export", St.ClickSound, v => St.ClickSound = v);

            Header("Scheinwerfer");
            Toggle("Alles außer der Cursor-Umgebung abdunkeln", St.Spotlight, v => { St.Spotlight = v; Dispatcher.InvokeAsync(() => ShowTab(tab)); });
            if (St.Spotlight)
            {
                SliderRow("Größe", 6, 40, St.SpotlightSize, v => St.SpotlightSize = v, Pct);
                SliderRow("Abdunkeln", 10, 90, St.SpotlightDim * 100, v => St.SpotlightDim = v / 100, Pct);
            }
        }
        // ---------------------------------------------------------------- Webcam

        void BuildWebcam()
        {
            Header("Webcam", true);
            if (!p.HasWebcam)
            {
                Note("Diese Aufnahme enthält keine Webcam. Schalte in der Aufnahmeleiste (Video-Modus) oder in den Einstellungen „Webcam aufnehmen“ ein.");
                return;
            }
            Choice("Layout", new[] { ("Nur Bildschirm", CamLayout.ScreenOnly), ("Overlay", CamLayout.Overlay), ("Geteilt", CamLayout.Split), ("Nur Kamera", CamLayout.CameraOnly) },
                St.Layout, v => St.Layout = v);
            Choice("Position", new[] { ("Unten rechts", CamCorner.BottomRight), ("Unten links", CamCorner.BottomLeft), ("Oben rechts", CamCorner.TopRight), ("Oben links", CamCorner.TopLeft) },
                St.CamCorner, v => St.CamCorner = v);
            Choice("Form", new[] { ("Kreis", CamShape.Circle), ("Abgerundet", CamShape.Rounded), ("Eckig", CamShape.Square) }, St.CamShape, v => St.CamShape = v);
            SliderRow("Größe", 10, 45, St.CamSize, v => St.CamSize = v, Pct);
            Toggle("Spiegeln", St.CamMirror, v => St.CamMirror = v);
            Toggle("Smart: weicht dem Cursor aus", St.CamSmartPosition, v => St.CamSmartPosition = v);
            Toggle("Beim Zoomen verkleinern", St.CamShrinkOnZoom, v => St.CamShrinkOnZoom = v);
            Toggle("Bei Stille ausblenden (Mikrofon)", St.CamHideOnSilence, v => St.CamHideOnSilence = v);
            Header("Farbe & Schärfe");
            SliderRow("Helligkeit", -1, 1, St.CamBrightness, v => St.CamBrightness = v, v => $"{v * 100:+0;-0;0}");
            SliderRow("Kontrast", -0.5, 0.8, St.CamContrast, v => St.CamContrast = v, v => $"{v * 100:+0;-0;0}");
            SliderRow("Sättigung", -1, 1, St.CamSaturation, v => St.CamSaturation = v, v => $"{v * 100:+0;-0;0}");
            SliderRow("Wärme", -1, 1, St.CamWarmth, v => St.CamWarmth = v, v => $"{v * 100:+0;-0;0}");
            Toggle("Schärfen", St.CamSharpen, v => St.CamSharpen = v);
            Note("Tipp: Mit „Layout“ unten in der Zeitleiste lassen sich Abschnitte z. B. auf „Nur Kamera“ umschalten – mit weicher Überblendung.");
        }

        // ---------------------------------------------------------------- Tastatur

        void BuildKeys()
        {
            Header("Tastatur", true);
            Toggle("Tastenkürzel einblenden", St.ShowKeys, v => St.ShowKeys = v);
            Choice("Position", new[] { ("Unten", KeysPosition.Bottom), ("Oben", KeysPosition.Top), ("Unten links", KeysPosition.BottomLeft), ("Unten rechts", KeysPosition.BottomRight) },
                St.KeysPosition, v => St.KeysPosition = v);
            Choice("Darstellung", new[] { ("Dunkel", false), ("Hell", true) }, St.KeysLight, v => St.KeysLight = v);
            SliderRow("Größe", 0.6, 2, St.KeysSize, v => St.KeysSize = v, X1);
            Toggle("Lange Tipp-Phasen beschleunigen (3×)", St.SpeedUpTyping, v =>
            {
                St.SpeedUpTyping = v;
                if (v) p.ApplyTypingSpeedUp();
                else foreach (var c in p.Edits.Clips.Where(c => c.AutoSpeed)) { c.Speed = 1; c.AutoSpeed = false; }
            });
            int shortcuts = p.Input.Keys.Count(k => k.Text.Length > 1);
            int typing = p.Input.Keys.Count(k => k.Text.Length == 0);
            Note($"{shortcuts} Tastenkürzel und {typing} Tastenanschläge aufgezeichnet. Bei normalem Tippen wird nur der Zeitpunkt gespeichert, nicht die Taste.");
        }

        // ---------------------------------------------------------------- Ton

        void BuildAudio()
        {
            Header("Lautstärke", true);
            if (p.Meta.HasSystemAudio || p.Meta.SourceFile != null)
                SliderRow("System-Ton", 0, 200, St.SystemVolume * 100, v => St.SystemVolume = v / 100, Pct);
            if (p.Meta.HasMic)
                SliderRow("Mikrofon", 0, 200, St.MicVolume * 100, v => St.MicVolume = v / 100, Pct);
            if (!p.Meta.HasSystemAudio && !p.Meta.HasMic && p.Meta.SourceFile == null) Note("Diese Aufnahme hat keine Tonspur.");
            Header("Stimme");
            Choice("Rauschunterdrückung (Mikrofon)", new[] { ("Aus", NoiseMode.Off), ("Standard", NoiseMode.Standard), ("Podcast", NoiseMode.Podcast) }, St.Noise, v => St.Noise = v);
            Toggle("Lautheit angleichen (−16 LUFS)", St.Normalize, v => St.Normalize = v);
            Header("Hintergrundmusik");
            Side.Children.Add(SideButton(St.MusicPath == null ? "Musik wählen …" : IOPath.GetFileName(St.MusicPath), "IconFolder", () =>
            {
                var dlg = new OpenFileDialog { Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac" };
                if (dlg.ShowDialog(this) == true) { Change(() => St.MusicPath = dlg.FileName); ShowTab(tab); }
            }));
            if (St.MusicPath != null)
            {
                Side.Children.Add(SideButton("Musik entfernen", "IconTrash", () => { Change(() => St.MusicPath = null); ShowTab(tab); }));
                SliderRow("Musik-Lautstärke", 0, 100, St.MusicVolume * 100, v => St.MusicVolume = v / 100, Pct);
                Toggle("Musik leiser, wenn gesprochen wird (Ducking)", St.MusicDucking, v => St.MusicDucking = v);
            }
            Note("Rauschunterdrückung, Lautheit, Musik, Ducking, Ein-/Ausblenden und Klick-Geräusche wirken im Export. Die Musik wird passend zur Videolänge wiederholt und am Ende ausgeblendet.");
        }

        // ---------------------------------------------------------------- Effekte

        void BuildEffects()
        {
            Header("Blenden & Übergänge", true);
            SliderRow("Einblenden am Anfang", 0, 3, St.FadeIn, v => St.FadeIn = v, Sec);
            SliderRow("Ausblenden am Ende", 0, 3, St.FadeOut, v => St.FadeOut = v, Sec);
            Choice("Übergang an Schnitten", new[] { ("Keiner", CutTransition.None), ("Abblende", CutTransition.Fade), ("Blitz", CutTransition.Flash), ("Unschärfe", CutTransition.Blur) },
                St.CutTransition, v => St.CutTransition = v);

            Header("Farbe der Aufnahme");
            SliderRow("Helligkeit", -1, 1, St.Brightness, v => St.Brightness = v, Signed);
            SliderRow("Kontrast", -0.5, 0.8, St.Contrast, v => St.Contrast = v, Signed);
            SliderRow("Sättigung", -1, 1, St.Saturation, v => St.Saturation = v, Signed);
            SliderRow("Wärme", -1, 1, St.Warmth, v => St.Warmth = v, Signed);
            Side.Children.Add(SideButton("Farbe zurücksetzen", "IconUndo", () =>
            {
                Change(() => St.Brightness = St.Contrast = St.Saturation = St.Warmth = 0);
                ShowTab(tab);
            }));

            Header("Look");
            SliderRow("Vignette", 0, 100, St.Vignette * 100, v => St.Vignette = v / 100, v => v < 0.5 ? "Aus" : $"{v:0} %");
            SliderRow("Filmkorn", 0, 100, St.Grain * 100, v => St.Grain = v / 100, v => v < 0.5 ? "Aus" : $"{v:0} %");

            Header("3D & Tiefe");
            SliderRow("Neigung seitlich", -25, 25, St.TiltY, v => St.TiltY = Math.Round(v, 1), Deg);
            SliderRow("Neigung vorne/hinten", -25, 25, St.TiltX, v => St.TiltX = Math.Round(v, 1), Deg);
            var presets = new WrapPanel();
            foreach (var (name, ty, tx) in new[] { ("Flach", 0.0, 0.0), ("Links", 12.0, 4.0), ("Rechts", -12.0, 4.0), ("Liegend", 0.0, 16.0) })
                presets.Children.Add(SideButton(name, null, () => { Change(() => { St.TiltY = ty; St.TiltX = tx; }); ShowTab(tab); }));
            Side.Children.Add(presets);
            SliderRow("Parallaxe beim Zoomen", 0, 100, St.Parallax * 100, v => St.Parallax = v / 100, v => v < 0.5 ? "Aus" : $"{v:0} %");
            Note("Parallaxe: Der Hintergrund zoomt weniger mit als die Aufnahme – das wirkt räumlich.");

            Header("Einblendungen");
            Toggle("Fortschrittsbalken am unteren Rand", St.ProgressBar, v => St.ProgressBar = v);
            ColorRow("Akzentfarbe (Fortschritt, Hinweise, Bauchbinden)", St.AccentColor, v => St.AccentColor = v);
            Side.Children.Add(SideButton(St.WatermarkPath == null ? "Logo / Wasserzeichen wählen …" : IOPath.GetFileName(St.WatermarkPath), "IconImage", () =>
            {
                var dlg = new OpenFileDialog { Filter = "Bilder|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
                if (dlg.ShowDialog(this) == true) { Change(() => St.WatermarkPath = dlg.FileName); ShowTab(tab); }
            }));
            if (St.WatermarkPath != null)
            {
                Side.Children.Add(SideButton("Logo entfernen", "IconTrash", () => { Change(() => St.WatermarkPath = null); ShowTab(tab); }));
                Choice("Ecke", new[] { ("Oben rechts", CamCorner.TopRight), ("Oben links", CamCorner.TopLeft), ("Unten rechts", CamCorner.BottomRight), ("Unten links", CamCorner.BottomLeft) },
                    St.WatermarkCorner, v => St.WatermarkCorner = v);
                SliderRow("Größe", 4, 30, St.WatermarkSize, v => St.WatermarkSize = v, Pct);
                SliderRow("Deckkraft", 10, 100, St.WatermarkOpacity * 100, v => St.WatermarkOpacity = v / 100, Pct);
            }
            Side.Children.Add(SideButton("Text-Einblendung hinzufügen", "IconText", () => Timeline.AddSegment(TrackKind.Text, p.SourceAt(player.OutTime))));

            Header("Tempo");
            int idle = p.Edits.Clips.Count(c => c.AutoIdle);
            Note(idle > 0
                ? $"{idle} Leerlauf-Abschnitt(e) laufen schneller (gelb in der Zeitleiste)."
                : "Abschnitte ohne Maus, Klicks, Tasten und Sprache automatisch beschleunigen – Ladezeiten und Wartepausen fliegen vorbei.");
            var tempo = new WrapPanel();
            tempo.Children.Add(SideButton("Leerlauf beschleunigen (4×)", "IconSpeed", () =>
            {
                int n = 0;
                Change(() => n = p.ApplyIdleSpeedUp());
                SaveState.Text = n > 0 ? $"{n} Leerlauf-Abschnitt(e) beschleunigt" : "Kein Leerlauf gefunden";
                ShowTab(tab);
            }));
            if (idle > 0)
                tempo.Children.Add(SideButton("Zurücksetzen", "IconUndo", () => { Change(() => p.ResetIdleSpeedUp()); ShowTab(tab); }));
            Side.Children.Add(tempo);
        }

        // ---------------------------------------------------------------- Vorlagen

        void BuildPresets()
        {
            Header("Stil-Vorlagen", true);
            Note("Eine Vorlage speichert Hintergrund, Rahmen, Effekte sowie Zoom-, Cursor-, Webcam-, Tastatur- und Ton-Einstellungen.");
            var name = new TextBox { Style = (Style)FindResource("DarkText"), Margin = new Thickness(0, 4, 0, 0), Text = "Meine Vorlage" };
            Side.Children.Add(name);
            Side.Children.Add(SideButton("Aktuellen Stil als Vorlage speichern", "IconSave", () =>
            {
                string n = name.Text.Trim();
                if (n.Length == 0) return;
                App.Settings.StudioPresets[n] = St.Clone();
                App.Settings.Save();
                ShowTab(tab);
            }, accent: true));
            Side.Children.Add(SideButton("Als Standard für neue Aufnahmen", "IconStar", () =>
            {
                App.Settings.StudioDefaultStyle = St.Clone();
                App.Settings.Save();
                SaveState.Text = "Standard-Stil gespeichert ✓";
            }));

            if (App.Settings.StudioPresets.Count > 0) Header("Gespeichert");
            foreach (var (n, style) in App.Settings.StudioPresets.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var del = new Button { Style = (Style)FindResource("IconButton"), Tag = FindResource("IconTrash"), ToolTip = "Löschen" };
                del.Click += (_, _) => { App.Settings.StudioPresets.Remove(n); App.Settings.Save(); ShowTab(tab); };
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
                var apply = new Button
                {
                    Content = n,
                    Style = (Style)FindResource("IconButton"),
                    Tag = FindResource("IconStar"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
                };
                apply.Click += (_, _) =>
                {
                    Change(() => p.Edits.Style = style.Clone());
                    BuildAspectBar();
                    ShowTab(tab);
                };
                row.Children.Add(apply);
                Side.Children.Add(row);
            }
        }

        // ================================================================== Vorschau-Overlay (Masken, Fokus)

        Rect DisplayRect()
        {
            var bmp = player.Bitmap;
            if (bmp == null) return Rect.Empty;
            double aw = PreviewHost.ActualWidth - 56, ah = PreviewHost.ActualHeight - 56;
            double s = Math.Min(aw / bmp.PixelWidth, ah / bmp.PixelHeight);
            double w = bmp.PixelWidth * s, h = bmp.PixelHeight * s;
            return new Rect((PreviewHost.ActualWidth - w) / 2, (PreviewHost.ActualHeight - h) / 2, w, h);
        }

        Point ToDisplay(SKPoint canvasPt)
        {
            var d = DisplayRect();
            var bmp = player.Bitmap!;
            return new Point(d.X + canvasPt.X * d.Width / bmp.PixelWidth, d.Y + canvasPt.Y * d.Height / bmp.PixelHeight);
        }

        SKPoint ToCanvas(Point displayPt)
        {
            var d = DisplayRect();
            var bmp = player.Bitmap!;
            return new SKPoint((float)((displayPt.X - d.X) * bmp.PixelWidth / d.Width), (float)((displayPt.Y - d.Y) * bmp.PixelHeight / d.Height));
        }

        void UpdateOverlay()
        {
            PreviewOverlay.Children.Clear();
            PreviewOverlay.Background = pickFocus ? Brushes.Transparent : null;
            PreviewOverlay.Cursor = pickFocus ? Cursors.Cross : null;
            if (!pickFocus) PreviewHint.Text = trackStatus ?? "";
            if (player.Bitmap == null) return;
            if (Timeline.SelectedKind != TrackKind.Mask || Timeline.SelectedIndex < 0 || Timeline.SelectedIndex >= p.Edits.Masks.Count) return;

            var m = p.Edits.Masks[Timeline.SelectedIndex];
            var bmp = player.Bitmap;
            double t = p.SourceAt(player.OutTime);
            var (mx, my, mw, mh) = m.RectAt(t);
            var a = ToDisplay(player.Renderer.SourceRelToCanvas(mx, my, bmp.PixelWidth, bmp.PixelHeight, t, p.HasWebcam));
            var b = ToDisplay(player.Renderer.SourceRelToCanvas(mx + mw, my + mh, bmp.PixelWidth, bmp.PixelHeight, t, p.HasWebcam));

            var rect = new Rectangle
            {
                Width = Math.Max(8, b.X - a.X),
                Height = Math.Max(8, b.Y - a.Y),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xE8, 0x5D, 0x75)),
                Cursor = Cursors.SizeAll
            };
            Canvas.SetLeft(rect, a.X);
            Canvas.SetTop(rect, a.Y);
            var handle = new Rectangle { Width = 12, Height = 12, Fill = Brushes.White, Cursor = Cursors.SizeNWSE, RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(handle, b.X - 6);
            Canvas.SetTop(handle, b.Y - 6);
            PreviewOverlay.Children.Add(rect);
            PreviewOverlay.Children.Add(handle);
            rect.MouseLeftButtonDown += (_, e) => BeginMaskDrag(m, false, e);
            handle.MouseLeftButtonDown += (_, e) => BeginMaskDrag(m, true, e);
        }

        void BeginMaskDrag(MaskSegment m, bool resize, MouseButtonEventArgs e)
        {
            if (player.IsPlaying) TogglePlay();
            PushUndo();
            if (m.Motion == MaskMotion.Track) CancelTracking();
            dragMask = m;
            dragResize = resize;
            dragMoved = false;
            dragStart = e.GetPosition(PreviewOverlay);
            dragT = Math.Clamp(p.SourceAt(player.OutTime), m.Start, m.End);
            dragBase = m.RectAt(dragT);
            PreviewOverlay.CaptureMouse();
            e.Handled = true;
        }

        void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragMask is not { } m || !PreviewOverlay.IsMouseCaptured || player.Bitmap == null) return;
            var bmp = player.Bitmap;
            double t = p.SourceAt(player.OutTime);
            var now = player.Renderer.CanvasToSourceRel(ToCanvas(e.GetPosition(PreviewOverlay)), bmp.PixelWidth, bmp.PixelHeight, t, p.HasWebcam);
            var was = player.Renderer.CanvasToSourceRel(ToCanvas(dragStart), bmp.PixelWidth, bmp.PixelHeight, t, p.HasWebcam);
            double dx = now.X - was.X, dy = now.Y - was.Y;
            var (x, y, w, h) = dragBase;
            if (dragResize)
            {
                w = Math.Clamp(w + dx, 0.02, 1 - x);
                h = Math.Clamp(h + dy, 0.02, 1 - y);
            }
            else
            {
                x = Math.Clamp(x + dx, 0, 1 - w);
                y = Math.Clamp(y + dy, 0, 1 - h);
            }
            dragMoved = true;

            switch (m.Motion)
            {
                case MaskMotion.Fixed:
                    (m.X, m.Y, m.W, m.H) = (x, y, w, h);
                    break;
                case MaskMotion.Keyframes:
                    m.SetKey(dragT, x, y, w, h);
                    break;
                case MaskMotion.Track:
                    // Größe gilt für die ganze Verfolgung, die Position nur ab hier (wird beim Loslassen neu verfolgt)
                    if (dragResize) m.Keys = m.Keys.Select(k => k with { W = w, H = h }).ToList();
                    (m.X, m.Y, m.W, m.H) = (x, y, w, h);
                    m.SetKey(dragT, x, y, w, h);
                    break;
            }
            player.Refresh();
            UpdateOverlay();
            ScheduleSave();
        }

        void EndMaskDrag()
        {
            if (dragMask is not { } m) return;
            dragMask = null;
            if (PreviewOverlay.IsMouseCaptured) PreviewOverlay.ReleaseMouseCapture();
            if (!dragMoved) return;
            if (m.Motion == MaskMotion.Track) StartTracking(m, dragT, forwardOnly: true);
            AfterChange();
        }

        // ---------------------------------------------------------------- Masken-Bewegung

        void SetMaskMotion(MaskSegment m, MaskMotion v)
        {
            CancelTracking();
            double t = Math.Clamp(p.SourceAt(player.OutTime), m.Start, m.End);
            var cur = m.RectAt(t);
            m.Motion = v;
            (m.X, m.Y, m.W, m.H) = cur;
            m.Keys.Clear();
            if (v == MaskMotion.Keyframes) m.SetKey(t, cur.X, cur.Y, cur.W, cur.H);
            if (v == MaskMotion.Track) Dispatcher.InvokeAsync(() => StartTracking(m, t, forwardOnly: false));
        }

        void CancelTracking()
        {
            trackCts?.Cancel();
            trackCts = null;
            trackStatus = null;
        }

        /// <summary>Verfolgt den Inhalt unter der Maske ab t0 (vorwärts, sonst auch rückwärts bis zum Anfang der Maske).</summary>
        async void StartTracking(MaskSegment m, double t0, bool forwardOnly)
        {
            CancelTracking();
            var cts = trackCts = new CancellationTokenSource();
            t0 = Math.Clamp(t0, m.Start, m.End);
            var rect = m.RectAt(t0);
            trackStatus = "Maske wird verfolgt …";
            PreviewHint.Text = trackStatus;
            ShowTab(tab);
            var progress = new Progress<double>(v =>
            {
                if (trackCts != cts) return;
                trackStatus = $"Maske wird verfolgt … {v * 100:0} %";
                PreviewHint.Text = trackStatus;
            });
            try
            {
                var keys = await Task.Run(() => MaskTracker.TrackAsync(p, rect, t0, forwardOnly ? t0 : m.Start, m.End, progress, cts.Token));
                if (cts.IsCancellationRequested || m.Motion != MaskMotion.Track) return;
                if (forwardOnly) keys = m.Keys.Where(k => k.T < t0 - 0.001).Concat(keys).ToList();
                m.Keys = keys;
                trackStatus = null;
                AfterChange();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (trackCts == cts) trackStatus = "Verfolgung fehlgeschlagen: " + ex.Message;
            }
            finally
            {
                if (trackCts == cts)
                {
                    trackCts = null;
                    PreviewHint.Text = trackStatus ?? "";
                    ShowTab(tab);
                }
            }
        }
        void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!pickFocus || player.Bitmap == null) return;
            if (Timeline.SelectedKind == TrackKind.Zoom && Timeline.SelectedIndex >= 0 && Timeline.SelectedIndex < p.Edits.Zooms.Count)
            {
                var z = p.Edits.Zooms[Timeline.SelectedIndex];
                var bmp = player.Bitmap;
                // Fokus bezogen auf die ungezoomte Ansicht am Anfang des Zooms bestimmen
                var rel = player.Renderer.CanvasToSourceRel(ToCanvas(e.GetPosition(PreviewOverlay)), bmp.PixelWidth, bmp.PixelHeight, p.SourceAt(player.OutTime), p.HasWebcam);
                Change(() =>
                {
                    z.FollowCursor = false;
                    z.FocusX = Math.Clamp(rel.X, 0, 1);
                    z.FocusY = Math.Clamp(rel.Y, 0, 1);
                    z.Auto = false;
                });
            }
            pickFocus = false;
            PreviewHint.Text = "";
            ShowTab(tab);
            UpdateOverlay();
            e.Handled = true;
        }

        // ================================================================== Transport & Zeitleisten-Aktionen

        void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

        void TogglePlay()
        {
            player.Toggle();
            PlayBtn.Tag = FindResource(player.IsPlaying ? "IconPause" : "IconPlay");
        }

        void Split()
        {
            double src = p.SourceAt(player.OutTime);
            PushUndo();
            int i = p.SplitClipAt(src);
            Timeline.Select(TrackKind.Clip, Math.Min(i, p.Edits.Clips.Count - 1));
            AfterChange();
        }

        void Split_Click(object sender, RoutedEventArgs e) => Split();
        void Delete_Click(object sender, RoutedEventArgs e) => Timeline.DeleteSelected();
        void AddZoom_Click(object sender, RoutedEventArgs e) => Timeline.AddSegment(TrackKind.Zoom, p.SourceAt(player.OutTime));
        void AddMask_Click(object sender, RoutedEventArgs e) => Timeline.AddSegment(TrackKind.Mask, p.SourceAt(player.OutTime));
        void AddText_Click(object sender, RoutedEventArgs e) => Timeline.AddSegment(TrackKind.Text, p.SourceAt(player.OutTime));
        void AddLayout_Click(object sender, RoutedEventArgs e) => Timeline.AddSegment(TrackKind.Layout, p.SourceAt(player.OutTime));

        void Mute_Click(object sender, RoutedEventArgs e)
        {
            player.Muted = !player.Muted;
            MuteBtn.Tag = FindResource(player.Muted ? "IconMute" : "IconSpeaker");
        }

        void Undo_Click(object sender, RoutedEventArgs e) => Undo();
        void Redo_Click(object sender, RoutedEventArgs e) => Redo();
        void New_Click(object sender, RoutedEventArgs e) => App.Instance.StartCapture(this, video: true);
        void Open_Click(object sender, RoutedEventArgs e) => App.Instance.OpenFile(this);

        // ================================================================== Export

        int ShortSide => int.TryParse(App.Settings.ExportResolution, out int v) ? v : 1080;
        bool exportGif;
        int exportCrf = 20, gifFps = 15;

        void Export_Click(object sender, RoutedEventArgs e) => ShowExport();

        void ShowExport()
        {
            if (player.IsPlaying) TogglePlay();
            ExportOptionsPanel.Children.Clear();
            var host = ExportOptionsPanel;
            Choice("Format", new[] { ("MP4-Video", false), ("GIF", true) }, exportGif, v => { exportGif = v; Dispatcher.InvokeAsync(ShowExport); }, host);
            var res = exportGif
                ? new[] { ("480p", "480"), ("720p", "720"), ("1080p", "1080") }
                : new[] { ("720p", "720"), ("1080p", "1080"), ("1440p", "1440"), ("4K", "2160") };
            if (exportGif && ShortSide > 1080) App.Settings.ExportResolution = "720";
            Choice("Auflösung", res, App.Settings.ExportResolution, v => { App.Settings.ExportResolution = v; UpdateExportInfo(); }, host);
            if (exportGif)
                Choice("Bilder pro Sekunde", new[] { ("10", 10), ("15", 15), ("24", 24) }, gifFps, v => { gifFps = v; UpdateExportInfo(); }, host);
            else
            {
                Choice("Bilder pro Sekunde", new[] { ("30", 30), ("60", 60) }, App.Settings.ExportFps, v => { App.Settings.ExportFps = v; UpdateExportInfo(); }, host);
                Choice("Qualität", new[] { ("Hoch", 16), ("Ausgewogen", 20), ("Klein", 26) }, exportCrf, v => exportCrf = v, host);
            }
            UpdateExportInfo();
            ExportProgress.Visibility = Visibility.Collapsed;
            ExportButtons.IsEnabled = true;
            ExportPanel.Visibility = Visibility.Visible;
        }

        void UpdateExportInfo()
        {
            var (w, h) = player.Renderer.OutputSize(exportGif ? Math.Min(ShortSide, 1080) : ShortSide);
            string enc = App.Settings.HardwareEncoding ? (VideoEncoder.LastUsed is EncoderKind k ? VideoEncoder.Name(k) : "Grafikkarte, sonst CPU") : "CPU";
            ExportInfo.Text = $"{w} × {h}  ·  {Fmt(p.OutputDuration)}  ·  {(exportGif ? gifFps : App.Settings.ExportFps)} Bilder/s  ·  Kodierung: {enc}" +
                              (exportGif ? "  ·  GIFs werden schnell groß – kurz halten." : "");
            UpdateOutInfo();
        }

        StudioExportOptions Options() => new()
        {
            Gif = exportGif,
            ShortSide = ShortSide,
            Fps = App.Settings.ExportFps,
            Crf = exportCrf,
            GifFps = gifFps
        };

        async Task<string?> RunExportAsync(string? target)
        {
            SaveNow();
            App.Settings.Save();
            string ext = exportGif ? "gif" : "mp4";
            if (target == null)
            {
                string dir = IOPath.Combine(IOPath.GetTempPath(), "SnipSnip", $"export_{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                target = IOPath.Combine(dir, $"SnipSnip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}");
            }
            exportCts = new CancellationTokenSource();
            ExportProgress.Visibility = Visibility.Visible;
            ExportButtons.IsEnabled = false;
            ExportBar.Value = 0;
            try
            {
                await StudioExporter.ExportAsync(p, Options(), target, (v, s) => Dispatcher.InvokeAsync(() =>
                {
                    ExportBar.Value = v;
                    ExportStatus.Text = $"{v * 100:0} %  ·  {s}";
                }), exportCts.Token);
                return target;
            }
            catch (OperationCanceledException)
            {
                try { File.Delete(target); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                try { File.Delete(target); } catch { }
                MessageBox.Show(this, ex.Message, "SnipSnip – Export fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            finally
            {
                ExportProgress.Visibility = Visibility.Collapsed;
                ExportButtons.IsEnabled = true;
                exportCts?.Dispose();
                exportCts = null;
            }
        }

        async void ExportSave_Click(object sender, RoutedEventArgs e)
        {
            string ext = exportGif ? "gif" : "mp4";
            var dlg = new SaveFileDialog
            {
                Title = "Video exportieren",
                Filter = exportGif ? "GIF-Animation (*.gif)|*.gif" : "MP4-Video (*.mp4)|*.mp4",
                FileName = $"{p.Name}.{ext}",
                AddExtension = true
            };
            try { Directory.CreateDirectory(App.Settings.VideoFolder); dlg.InitialDirectory = App.Settings.VideoFolder; } catch { }
            if (dlg.ShowDialog(this) != true) return;
            var f = await RunExportAsync(dlg.FileName);
            if (f == null) return;
            ExportPanel.Visibility = Visibility.Collapsed;
            var thumb = await VideoExporter.ThumbnailAsync(f);
            Toast.Notify("Exportiert", IOPath.GetFileName(f), thumb, () => App.OpenFolder(f, true), 6,
                ("Ordner öffnen", () => App.OpenFolder(f, true)));
        }

        async void ExportCopy_Click(object sender, RoutedEventArgs e)
        {
            var f = await RunExportAsync(null);
            if (f == null) return;
            ExportPanel.Visibility = Visibility.Collapsed;
            try { Clipboard.SetFileDropList(new StringCollection { f }); } catch { }
            Toast.Notify("Video kopiert", "Zum Einfügen in Discord, Teams, Explorer …", await VideoExporter.ThumbnailAsync(f), null, 5);
        }

        async void ExportUpload_Click(object sender, RoutedEventArgs e)
        {
            var f = await RunExportAsync(null);
            if (f == null) return;
            ExportPanel.Visibility = Visibility.Collapsed;
            await App.Instance.UploadFileAsync(f);
        }

        void ExportStill_Click(object sender, RoutedEventArgs e) => ExportStill(copy: false);

        /// <summary>Aktuelles Bild in Export-Auflösung als PNG speichern oder in die Zwischenablage legen.</summary>
        async void ExportStill(bool copy)
        {
            if (player.IsPlaying) TogglePlay();
            string? file = null;
            if (!copy)
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Standbild speichern",
                    Filter = "PNG-Bild (*.png)|*.png",
                    FileName = $"{p.Name}_{player.OutTime:0.0}s.png".Replace(',', '-'),
                    AddExtension = true
                };
                try { Directory.CreateDirectory(App.Settings.SaveFolder); dlg.InitialDirectory = App.Settings.SaveFolder; } catch { }
                if (dlg.ShowDialog(this) != true) return;
                file = dlg.FileName;
            }
            SaveState.Text = "Standbild wird gerendert …";
            try
            {
                double outT = player.OutTime, src = p.SourceAt(outT);
                var screen = await FrameDecoder.GrabAsync(p.ScreenFile, src, p.Meta.Width, p.Meta.Height);
                SKImage? cam = p.HasWebcam ? await FrameDecoder.GrabAsync(p.WebcamFile!, p.Meta.WallAt(src) - p.Meta.WebcamStartEpoch, 1280, 720) : null;
                var bmp = await Task.Run(() =>
                {
                    using var r = new StudioRenderer(p);
                    var (w, h) = r.OutputSize(ShortSide);
                    using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                    surface.Canvas.Clear(SKColors.Black);
                    r.Render(surface.Canvas, w, h, src, outT, screen, cam, export: true);
                    using var img = surface.Snapshot();
                    if (file != null)
                    {
                        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                        using var fs = File.Create(file);
                        data.SaveTo(fs);
                        return null;
                    }
                    return ToBitmap(img);
                });
                screen?.Dispose();
                cam?.Dispose();
                if (bmp != null)
                {
                    Clipboard.SetImage(bmp);
                    SaveState.Text = "Standbild kopiert ✓";
                }
                else
                {
                    SaveState.Text = "Standbild gespeichert ✓";
                    Toast.Notify("Standbild gespeichert", IOPath.GetFileName(file!), null, () => App.OpenFolder(file!, true), 5,
                        ("Ordner öffnen", () => App.OpenFolder(file!, true)));
                }
            }
            catch (Exception ex) { SaveState.Text = "Standbild fehlgeschlagen: " + ex.Message; }
        }

        void CancelExport_Click(object sender, RoutedEventArgs e) => exportCts?.Cancel();

        void CloseExport_Click(object sender, RoutedEventArgs e)
        {
            exportCts?.Cancel();
            ExportPanel.Visibility = Visibility.Collapsed;
        }

        // ================================================================== Befehlspalette (Strg+K)

        List<(string Name, Action Run)> Commands() => new()
        {
            ("Abspielen / Pause", TogglePlay),
            ("Exportieren …", ShowExport),
            ("Clip teilen", Split),
            ("Zoom hinzufügen", () => Timeline.AddSegment(TrackKind.Zoom, p.SourceAt(player.OutTime))),
            ("Maske hinzufügen", () => Timeline.AddSegment(TrackKind.Mask, p.SourceAt(player.OutTime))),
            ("Text / Titel hinzufügen", () => Timeline.AddSegment(TrackKind.Text, p.SourceAt(player.OutTime))),
            ("Effekte (Blenden, Farbe, Vignette, 3D)", () => SelectTab("effects")),
            ("Übergänge an Schnitten", () => SelectTab("effects")),
            ("Fensterrahmen (macOS, Windows, Browser)", () => SelectTab("frame")),
            ("Cursor-Stil", () => SelectTab("cursor")),
            ("Scheinwerfer", () => SelectTab("cursor")),
            ("Leerlauf beschleunigen", () => { int n = 0; Change(() => n = p.ApplyIdleSpeedUp()); SaveState.Text = n > 0 ? $"{n} Leerlauf-Abschnitt(e) beschleunigt" : "Kein Leerlauf gefunden"; }),
            ("Standbild speichern …", () => ExportStill(copy: false)),
            ("Standbild kopieren", () => ExportStill(copy: true)),
            ("Hintergrund", () => SelectTab("background")),
            ("Rahmen & Schatten", () => SelectTab("frame")),
            ("Zuschneiden", () => SelectTab("frame")),
            ("Kamera & Zoom", () => SelectTab("zoom")),
            ("Bewegungsunschärfe", () => SelectTab("zoom")),
            ("Cursor", () => SelectTab("cursor")),
            ("Klick-Effekt", () => SelectTab("cursor")),
            ("Webcam", () => SelectTab("webcam")),
            ("Tastenkürzel einblenden", () => SelectTab("keys")),
            ("Ton & Rauschunterdrückung", () => SelectTab("audio")),
            ("Hintergrundmusik", () => SelectTab("audio")),
            ("Vorlagen", () => SelectTab("presets")),
            ("Seitenverhältnis 16:9", () => Change(() => { St.Aspect = "16:9"; BuildAspectBar(); })),
            ("Seitenverhältnis 9:16 (Reels, TikTok)", () => Change(() => { St.Aspect = "9:16"; BuildAspectBar(); })),
            ("Seitenverhältnis 1:1", () => Change(() => { St.Aspect = "1:1"; BuildAspectBar(); })),
            ("Rückgängig", Undo),
            ("Wiederholen", Redo),
            ("Neue Videoaufnahme", () => App.Instance.StartCapture(this, video: true)),
            ("Projektordner öffnen", () => App.OpenFolder(p.Dir))
        };

        void SelectTab(string key)
        {
            foreach (var rb in Tabs.Children.OfType<RadioButton>())
            {
                int i = Tabs.Children.IndexOf(rb);
                if (TabDefs[i].Key == key) rb.IsChecked = true;
            }
            ShowTab(key);
        }

        void Palette_Click(object sender, RoutedEventArgs e) => OpenPalette();

        void OpenPalette()
        {
            PalettePanel.Visibility = Visibility.Visible;
            PaletteBox.Text = "";
            FillPalette("");
            Dispatcher.InvokeAsync(() => { PaletteBox.Focus(); Keyboard.Focus(PaletteBox); }, DispatcherPriority.Input);
        }

        void FillPalette(string filter)
        {
            PaletteList.Items.Clear();
            foreach (var c in Commands().Where(c => c.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
                PaletteList.Items.Add(new ListBoxItem { Content = c.Name, Tag = c.Run, Padding = new Thickness(10, 6, 10, 6) });
            if (PaletteList.Items.Count > 0) PaletteList.SelectedIndex = 0;
        }

        void RunPalette()
        {
            if (PaletteList.SelectedItem is ListBoxItem { Tag: Action a })
            {
                PalettePanel.Visibility = Visibility.Collapsed;
                a();
            }
        }

        void PaletteBox_TextChanged(object sender, TextChangedEventArgs e) => FillPalette(PaletteBox.Text);

        void PaletteBox_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: PalettePanel.Visibility = Visibility.Collapsed; e.Handled = true; break;
                case Key.Enter: RunPalette(); e.Handled = true; break;
                case Key.Down: PaletteList.SelectedIndex = Math.Min(PaletteList.Items.Count - 1, PaletteList.SelectedIndex + 1); e.Handled = true; break;
                case Key.Up: PaletteList.SelectedIndex = Math.Max(0, PaletteList.SelectedIndex - 1); e.Handled = true; break;
            }
        }

        void PaletteList_DoubleClick(object sender, MouseButtonEventArgs e) => RunPalette();

        void PaletteBackdrop_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource == PalettePanel) PalettePanel.Visibility = Visibility.Collapsed;
        }

        // ================================================================== Tastatur & Schließen

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || Keyboard.FocusedElement is TextBox) return;
            if (ExportPanel.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape) { ExportPanel.Visibility = Visibility.Collapsed; e.Handled = true; }
                return;
            }
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
                    case Key.K: OpenPalette(); break;
                    case Key.E: ShowExport(); break;
                    case Key.N: App.Instance.StartCapture(this, video: true); break;
                    case Key.O: App.Instance.OpenFile(this); break;
                    case Key.S: SaveNow(); break;
                    default: e.Handled = false; break;
                }
                return;
            }
            switch (key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Left: player.Seek(player.OutTime - (shift ? 1 : 1.0 / 30)); break;
                case Key.Right: player.Seek(player.OutTime + (shift ? 1 : 1.0 / 30)); break;
                case Key.Home: player.Seek(0); break;
                case Key.End: player.Seek(p.OutputDuration); break;
                case Key.S: Split(); break;
                case Key.Z: Timeline.AddSegment(TrackKind.Zoom, p.SourceAt(player.OutTime)); break;
                case Key.M: Timeline.AddSegment(TrackKind.Mask, p.SourceAt(player.OutTime)); break;
                case Key.T: Timeline.AddSegment(TrackKind.Text, p.SourceAt(player.OutTime)); break;
                case Key.L when p.HasWebcam: Timeline.AddSegment(TrackKind.Layout, p.SourceAt(player.OutTime)); break;
                case Key.Delete:
                case Key.Back: Timeline.DeleteSelected(); break;
                case Key.Escape:
                    pickFocus = false;
                    Timeline.Select(TrackKind.None, -1);
                    break;
                default: e.Handled = false; break;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            exportCts?.Cancel();
            saveTimer.Stop();
            SaveNow();
            player.Dispose();
            base.OnClosing(e);
        }

        // ================================================================== Doku-Bilder

        internal void DocsSetup(BitmapSource preview, IEnumerable<BitmapSource> thumbs, string activeTab, TrackKind selKind, int selIndex, double srcT)
        {
            Preview.Source = preview;
            Timeline.Thumbs.Clear();
            Timeline.Thumbs.AddRange(thumbs);
            Timeline.SetPosition(srcT);
            Timeline.Select(selKind, selIndex);
            SelectTab(activeTab);
            TimeText.Text = $"{Fmt(p.OutputAt(srcT) ?? 0)} / {Fmt(p.OutputDuration)}";
            SaveState.Text = "Gespeichert ✓";
        }

        internal StudioPlayer DocsPlayer => player;
    }
}
