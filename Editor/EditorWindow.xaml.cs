using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace SnipSnip
{
    public partial class EditorWindow : Window
    {
        // ------------------------------------------------------------ Zustand

        BitmapSource baseBmp = null!;
        readonly Stack<(Action Undo, Action Redo)> undoStack = new();
        readonly Stack<(Action Undo, Action Redo)> redoStack = new();
        readonly Dictionary<RadioButton, Tool> toolButtons;
        readonly Dictionary<Tool, Color> colors = new();
        readonly Dictionary<Tool, double> sizes = new();
        readonly List<RadioButton> swatches = new();
        readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

        Tool tool = Tool.Arrow;
        bool fill;
        double zoom = 1;
        bool dirty;
        bool syncing;

        // Zeichnen
        bool isDown;
        Point start, last;
        FrameworkElement? live;
        double liveSize;
        Rectangle? preview;
        Annotation? dragAnn;
        Vector dragTotal;
        bool panning;
        Point panStart;
        Point panOffset;

        // Text
        TextBox? editingText;
        bool textFill;
        Color textColor;

        // Auswahl
        Annotation? selected;
        readonly Rectangle selAdorner = new()
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF)),
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false
        };

        static readonly string[] Palette =
        {
            "#FFE53935", "#FFFB8C00", "#FFFDD835", "#FF43A047", "#FF1E88E5",
            "#FF8E24AA", "#FFEC407A", "#FF000000", "#FFFFFFFF"
        };

        // ------------------------------------------------------------ Aufbau

        public EditorWindow(BitmapSource img)
        {
            InitializeComponent();

            toolButtons = new Dictionary<RadioButton, Tool>
            {
                [TSelect] = Tool.Select, [TPen] = Tool.Pen, [TMarker] = Tool.Marker, [TLine] = Tool.Line,
                [TArrow] = Tool.Arrow, [TRect] = Tool.Rect, [TEllipse] = Tool.Ellipse, [TText] = Tool.Text,
                [TStep] = Tool.Step, [TPixel] = Tool.Pixelate, [TBlur] = Tool.Blur, [TBlack] = Tool.Blackout,
                [TCrop] = Tool.Crop
            };

            var red = (Color)ColorConverter.ConvertFromString(Palette[0]);
            foreach (Tool t in Enum.GetValues(typeof(Tool)))
            {
                colors[t] = t switch { Tool.Marker => (Color)ColorConverter.ConvertFromString("#FFFDD835"), Tool.Blackout => Colors.Black, _ => red };
                sizes[t] = SizeSpec(t).Def;
            }

            foreach (var hex in Palette)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("Swatch"),
                    Background = new SolidColorBrush(c),
                    GroupName = "Swatch",
                    Tag = c,
                    ToolTip = hex.Replace("#FF", "#")
                };
                rb.Checked += (_, _) => { if (!syncing) SetColor(c); };
                swatches.Add(rb);
                SwatchHost.Children.Add(rb);
            }

            SetBase(img);

            // Fenstergröße passend zum Bild
            double dpi = SystemDpiScale();
            var wa = SystemParameters.WorkArea;
            Width = Math.Clamp(img.PixelWidth / dpi + 140, 1100, Math.Max(1100, wa.Width * 0.92));
            Height = Math.Clamp(img.PixelHeight / dpi + 270, 600, Math.Max(600, wa.Height * 0.92));

            UploadBtn.ToolTip = $"Zu {Uploader.Name(App.Settings.Provider)} hochladen  (Strg+U)";

            Area.MouseDown += Area_MouseDown;
            Area.MouseMove += Area_MouseMove;
            Area.MouseUp += Area_MouseUp;
            Area.LostMouseCapture += (_, _) => { if (isDown) FinishGesture(last); panning = false; };
            Scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
            statusTimer.Tick += (_, _) => { statusTimer.Stop(); UpdateHint(); };
            Drop += Window_Drop;
            DpiChanged += (_, _) => SetZoom(zoom);
            Loaded += (_, _) =>
            {
                Dispatcher.InvokeAsync(Fit, DispatcherPriority.Loaded);
                Area.Focus();
            };

            TArrow.IsChecked = true;
            UpdateUndoUi();
        }

        static double SystemDpiScale()
        {
            using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96.0;
        }

        static (double Min, double Max, double Def, string? Label) SizeSpec(Tool t) => t switch
        {
            Tool.Pen => (1, 30, 4, "Stärke"),
            Tool.Marker => (6, 60, 20, "Stärke"),
            Tool.Line or Tool.Arrow => (1, 30, 5, "Stärke"),
            Tool.Rect or Tool.Ellipse => (1, 30, 4, "Stärke"),
            Tool.Text => (10, 120, 26, "Schriftgröße"),
            Tool.Step => (16, 90, 34, "Größe"),
            Tool.Pixelate => (4, 60, 14, "Pixelgröße"),
            Tool.Blur => (3, 40, 14, "Stärke"),
            _ => (0, 0, 0, null)
        };

        static bool UsesColor(Tool t) => t is not (Tool.Select or Tool.Pixelate or Tool.Blur or Tool.Crop);
        static bool UsesFill(Tool t) => t is Tool.Rect or Tool.Ellipse or Tool.Text;

        Color CurrentColor => colors[tool];
        double CurrentSize => sizes[tool];

        void SetBase(BitmapSource b)
        {
            baseBmp = b;
            BaseImage.Source = b;
            BaseImage.Width = Surface.Width = b.PixelWidth;
            BaseImage.Height = Surface.Height = b.PixelHeight;
            StatusSize.Text = $"{b.PixelWidth} × {b.PixelHeight} px";
        }

        Rect ImageRect => new(0, 0, baseBmp.PixelWidth, baseBmp.PixelHeight);

        // ------------------------------------------------------------ Werkzeuge & Eigenschaften

        void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton rb || !toolButtons.TryGetValue(rb, out var t)) return;
            CommitText();
            tool = t;
            if (t != Tool.Select) Select(null);

            Area.Cursor = t switch
            {
                Tool.Select => Cursors.Arrow,
                Tool.Text => Cursors.IBeam,
                _ => Cursors.Cross
            };

            syncing = true;
            var spec = SizeSpec(t);
            SizePanel.Visibility = spec.Label != null ? Visibility.Visible : Visibility.Collapsed;
            if (spec.Label != null)
            {
                SizeLabel.Text = spec.Label;
                SizeSlider.Minimum = spec.Min;
                SizeSlider.Maximum = spec.Max;
                SizeSlider.Value = sizes[t];
                SizeValue.Text = ((int)sizes[t]).ToString();
            }
            ColorPanel.Visibility = UsesColor(t) ? Visibility.Visible : Visibility.Collapsed;
            FillToggle.Visibility = UsesFill(t) ? Visibility.Visible : Visibility.Collapsed;
            FillToggle.IsChecked = fill;
            syncing = false;

            ToolInfo.Text = t switch
            {
                Tool.Pixelate or Tool.Blur or Tool.Blackout => "Bereich aufziehen, um ihn unkenntlich zu machen",
                Tool.Crop => "Bereich aufziehen – alles außerhalb wird entfernt",
                Tool.Select => "Element anklicken & ziehen · Entf löscht",
                _ => ""
            };

            SyncColorUi();
            UpdateHint();
        }

        void SetTool(Tool t)
        {
            foreach (var kv in toolButtons)
                if (kv.Value == t) { kv.Key.IsChecked = true; break; }
        }

        void SetColor(Color c)
        {
            colors[tool] = c;
            // Gleiche Farbe für alle "normalen" Zeichenwerkzeuge übernehmen, Textmarker & Schwärzen behalten ihre eigene
            if (tool is not (Tool.Marker or Tool.Blackout))
                foreach (var t in new[] { Tool.Pen, Tool.Line, Tool.Arrow, Tool.Rect, Tool.Ellipse, Tool.Text, Tool.Step })
                    colors[t] = c;
            SyncColorUi();
        }

        void SyncColorUi()
        {
            syncing = true;
            var c = colors[tool];
            foreach (var s in swatches) s.IsChecked = s.Tag is Color sc && sc == c;
            CurrentColorDot.Background = new SolidColorBrush(c);
            syncing = false;
        }

        void CustomColor_Click(object sender, RoutedEventArgs e)
        {
            var c = CurrentColor;
            using var dlg = new WinForms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B)
            };
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                SetColor(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        }

        void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (syncing || SizeValue == null) return;
            sizes[tool] = SizeSlider.Value;
            SizeValue.Text = ((int)SizeSlider.Value).ToString();
        }

        void Fill_Click(object sender, RoutedEventArgs e) => fill = FillToggle.IsChecked == true;

        void UpdateHint()
        {
            string h = tool switch
            {
                Tool.Select => "Klicken = auswählen · Ziehen = verschieben · Entf = löschen",
                Tool.Pen or Tool.Marker => "Freihand zeichnen · Umschalt = gerade Linie",
                Tool.Line or Tool.Arrow => "Ziehen · Umschalt = in 15°-Schritten",
                Tool.Rect or Tool.Ellipse => "Ziehen · Umschalt = Quadrat / Kreis",
                Tool.Text => "Klicken und tippen · Strg+Enter oder Esc = fertig",
                Tool.Step => "Klicken setzt die nächste Nummer",
                Tool.Pixelate or Tool.Blur or Tool.Blackout => "Bereich aufziehen zum Zensieren",
                Tool.Crop => "Bereich aufziehen zum Zuschneiden",
                _ => ""
            };
            StatusHint.Text = h + "   ·   Rechtsklick = Element löschen   ·   Strg+Mausrad = Zoom   ·   Mittlere Maustaste = verschieben";
        }

        void Flash(string msg)
        {
            StatusHint.Text = msg;
            statusTimer.Stop();
            statusTimer.Start();
        }

        // ------------------------------------------------------------ Zoom

        void SetZoom(double z)
        {
            zoom = Math.Clamp(z, 0.05, 16);
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ZoomTf.ScaleX = ZoomTf.ScaleY = zoom / dpi; // 100 % = 1 Bildpixel pro Bildschirmpixel
            ZoomLabel.Content = $"{Math.Round(zoom * 100)} %";
            RenderOptions.SetBitmapScalingMode(BaseImage, zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            UpdateAdorner();
            if (preview != null) preview.StrokeThickness = 1 / ZoomTf.ScaleX;
        }

        void Fit()
        {
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double vw = Math.Max(50, Scroller.ViewportWidth - 80), vh = Math.Max(50, Scroller.ViewportHeight - 80);
            double z = Math.Min(1, Math.Min(vw * dpi / baseBmp.PixelWidth, vh * dpi / baseBmp.PixelHeight));
            SetZoom(z);
        }

        void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            SetZoom(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
            e.Handled = true;
        }

        void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(zoom * 1.25);
        void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(zoom / 1.25);
        void Zoom100_Click(object sender, RoutedEventArgs e) => SetZoom(1);
        void Fit_Click(object sender, RoutedEventArgs e) => Fit();

        // ------------------------------------------------------------ Maus

        void Area_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                panning = true;
                panStart = e.GetPosition(Scroller);
                panOffset = new Point(Scroller.HorizontalOffset, Scroller.VerticalOffset);
                Area.CaptureMouse();
                Mouse.OverrideCursor = Cursors.ScrollAll;
                e.Handled = true;
                return;
            }

            var p = e.GetPosition(Surface);

            if (e.ChangedButton == MouseButton.Right)
            {
                CommitText();
                var hit = HitAnnotation(p);
                if (hit != null) RemoveAnnotation(hit);
                e.Handled = true;
                return;
            }
            if (e.ChangedButton != MouseButton.Left) return;

            if (editingText != null)
            {
                CommitText();
                e.Handled = true;
                return;
            }

            Area.Focus();
            start = last = p;
            liveSize = CurrentSize;
            var c = CurrentColor;
            var brush = new SolidColorBrush(c);

            switch (tool)
            {
                case Tool.Select:
                    Select(HitAnnotation(p));
                    dragAnn = selected;
                    dragTotal = default;
                    if (dragAnn != null) Area.Cursor = Cursors.SizeAll;
                    break;

                case Tool.Pen:
                case Tool.Marker:
                {
                    bool marker = tool == Tool.Marker;
                    var pl = new Polyline
                    {
                        Stroke = marker ? new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B)) : brush,
                        StrokeThickness = liveSize,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = marker ? PenLineCap.Square : PenLineCap.Round,
                        StrokeEndLineCap = marker ? PenLineCap.Square : PenLineCap.Round
                    };
                    pl.Points.Add(p);
                    pl.Points.Add(p + new Vector(0.01, 0));
                    Layer.Children.Add(pl);
                    live = pl;
                    break;
                }

                case Tool.Line:
                case Tool.Arrow:
                {
                    var path = new Path
                    {
                        Stroke = brush,
                        Fill = tool == Tool.Arrow ? brush : null,
                        StrokeThickness = liveSize,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Data = ArrowGeometry(p, p, liveSize, tool == Tool.Arrow)
                    };
                    Layer.Children.Add(path);
                    live = path;
                    break;
                }

                case Tool.Rect:
                case Tool.Ellipse:
                case Tool.Blackout:
                {
                    Shape sh = tool == Tool.Ellipse ? new Ellipse() : new Rectangle { RadiusX = tool == Tool.Rect ? 3 : 0, RadiusY = tool == Tool.Rect ? 3 : 0 };
                    if (tool == Tool.Blackout)
                    {
                        sh.Fill = brush;
                    }
                    else
                    {
                        sh.Stroke = brush;
                        sh.StrokeThickness = liveSize;
                        sh.Fill = fill ? new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B)) : null;
                    }
                    Canvas.SetLeft(sh, p.X);
                    Canvas.SetTop(sh, p.Y);
                    sh.Width = sh.Height = 0;
                    Layer.Children.Add(sh);
                    live = sh;
                    break;
                }

                case Tool.Text:
                    StartText(p);
                    e.Handled = true;
                    return;

                case Tool.Step:
                    AddStep(p);
                    e.Handled = true;
                    return;

                case Tool.Pixelate:
                case Tool.Blur:
                case Tool.Crop:
                    preview = new Rectangle
                    {
                        Stroke = Brushes.White,
                        StrokeDashArray = new DoubleCollection { 4, 3 },
                        StrokeThickness = 1 / ZoomTf.ScaleX,
                        Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x4F, 0x8C, 0xFF))
                    };
                    Canvas.SetLeft(preview, p.X);
                    Canvas.SetTop(preview, p.Y);
                    Overlay.Children.Add(preview);
                    break;
            }

            isDown = true;
            Area.CaptureMouse();
            e.Handled = true;
        }

        void Area_MouseMove(object sender, MouseEventArgs e)
        {
            if (panning)
            {
                var d = e.GetPosition(Scroller) - panStart;
                Scroller.ScrollToHorizontalOffset(panOffset.X - d.X);
                Scroller.ScrollToVerticalOffset(panOffset.Y - d.Y);
                return;
            }

            var p = e.GetPosition(Surface);
            StatusPos.Text = ImageRect.Contains(p) ? $"{(int)p.X}, {(int)p.Y}" : "";
            if (!isDown) return;

            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (tool)
            {
                case Tool.Select:
                    if (dragAnn != null)
                    {
                        var d = p - last;
                        dragAnn.DragBy(d);
                        dragTotal += d;
                        UpdateAdorner();
                    }
                    break;

                case Tool.Pen:
                case Tool.Marker:
                    if (live is Polyline pl)
                    {
                        if (shift) pl.Points = new PointCollection { start, p };
                        else pl.Points.Add(p);
                    }
                    break;

                case Tool.Line:
                case Tool.Arrow:
                    if (live is Path path)
                        path.Data = ArrowGeometry(start, shift ? SnapAngle(start, p) : p, liveSize, tool == Tool.Arrow);
                    break;

                case Tool.Rect:
                case Tool.Ellipse:
                case Tool.Blackout:
                    if (live != null) PlaceRect(live, RectFrom(start, p, shift));
                    break;

                case Tool.Pixelate:
                case Tool.Blur:
                case Tool.Crop:
                    if (preview != null)
                    {
                        var r = Rect.Intersect(RectFrom(start, p, false), ImageRect);
                        PlaceRect(preview, r.IsEmpty ? new Rect(p, p) : r);
                    }
                    break;
            }
            last = p;
        }

        void Area_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (panning && e.ChangedButton == MouseButton.Middle)
            {
                panning = false;
                Mouse.OverrideCursor = null;
                Area.ReleaseMouseCapture();
                return;
            }
            if (!isDown || e.ChangedButton != MouseButton.Left) return;
            FinishGesture(e.GetPosition(Surface));
        }

        void FinishGesture(Point p)
        {
            if (!isDown) return;
            isDown = false;
            if (Area.IsMouseCaptured) Area.ReleaseMouseCapture();

            switch (tool)
            {
                case Tool.Select:
                    if (dragAnn != null)
                    {
                        var a = dragAnn;
                        var d = dragTotal;
                        a.EndDrag();
                        if (d.Length > 0.01)
                            Push(() => { a.MoveBy(-d); UpdateAdorner(); }, () => { a.MoveBy(d); UpdateAdorner(); });
                        UpdateAdorner();
                    }
                    dragAnn = null;
                    Area.Cursor = Cursors.Arrow;
                    break;

                case Tool.Pen:
                case Tool.Marker:
                case Tool.Line:
                case Tool.Arrow:
                    if (live != null)
                    {
                        if ((tool is Tool.Line or Tool.Arrow) && (p - start).Length < 3) Layer.Children.Remove(live);
                        else CommitAdd(new Annotation(live));
                    }
                    break;

                case Tool.Rect:
                case Tool.Ellipse:
                case Tool.Blackout:
                    if (live != null)
                    {
                        if (live.Width < 3 && live.Height < 3) Layer.Children.Remove(live);
                        else CommitAdd(new Annotation(live));
                    }
                    break;

                case Tool.Pixelate:
                case Tool.Blur:
                case Tool.Crop:
                {
                    if (preview != null) Overlay.Children.Remove(preview);
                    preview = null;
                    var r = Rect.Intersect(RectFrom(start, p, false), ImageRect);
                    if (!r.IsEmpty && r.Width >= 3 && r.Height >= 3)
                    {
                        if (tool == Tool.Crop) ApplyCrop(r);
                        else AddCensor(r, tool == Tool.Blur, (int)CurrentSize);
                    }
                    break;
                }
            }
            live = null;
        }

        void CancelGesture()
        {
            if (tool == Tool.Select) { FinishGesture(last); return; }
            isDown = false;
            if (live != null) Layer.Children.Remove(live);
            if (preview != null) Overlay.Children.Remove(preview);
            live = null;
            preview = null;
            if (Area.IsMouseCaptured) Area.ReleaseMouseCapture();
        }

        static Rect RectFrom(Point a, Point b, bool square)
        {
            if (square)
            {
                double s = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                b = new Point(a.X + Math.Sign(b.X - a.X == 0 ? 1 : b.X - a.X) * s, a.Y + Math.Sign(b.Y - a.Y == 0 ? 1 : b.Y - a.Y) * s);
            }
            return new Rect(a, b);
        }

        static void PlaceRect(FrameworkElement el, Rect r)
        {
            Canvas.SetLeft(el, r.X);
            Canvas.SetTop(el, r.Y);
            el.Width = r.Width;
            el.Height = r.Height;
        }

        static Point SnapAngle(Point a, Point b)
        {
            var v = b - a;
            double len = v.Length;
            double ang = Math.Atan2(v.Y, v.X);
            double step = Math.PI / 12;
            ang = Math.Round(ang / step) * step;
            return a + new Vector(Math.Cos(ang) * len, Math.Sin(ang) * len);
        }

        static Geometry ArrowGeometry(Point a, Point b, double t, bool head)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                var v = b - a;
                double len = v.Length;
                if (!head || len < 0.5)
                {
                    ctx.BeginFigure(a, false, false);
                    ctx.LineTo(b, true, true);
                }
                else
                {
                    v.Normalize();
                    double hl = Math.Min(Math.Max(12, t * 3.2), len);
                    double hw = hl * 0.62;
                    var n = new Vector(-v.Y, v.X);
                    var baseP = b - v * hl;
                    ctx.BeginFigure(a, false, false);
                    ctx.LineTo(baseP + v * Math.Min(hl * 0.3, t), true, true);
                    ctx.BeginFigure(b, true, true);
                    ctx.LineTo(baseP + n * hw, true, true);
                    ctx.LineTo(baseP - n * hw, true, true);
                }
            }
            g.Freeze();
            return g;
        }

        static Brush ContrastBrush(Color c)
        {
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
            return lum > 0.62 ? Brushes.Black : Brushes.White;
        }

        // ------------------------------------------------------------ Text

        void StartText(Point p)
        {
            var c = CurrentColor;
            double fs = CurrentSize;
            textFill = fill;
            textColor = c;

            var tb = new TextBox
            {
                FontSize = fs,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                MinWidth = Math.Max(30, fs),
                AcceptsReturn = true,
                Padding = new Thickness(4, 1, 4, 1),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x4F, 0x8C, 0xFF)),
                Background = fill ? new SolidColorBrush(c) : Brushes.Transparent,
                Foreground = fill ? ContrastBrush(c) : new SolidColorBrush(c),
                CaretBrush = fill ? ContrastBrush(c) : new SolidColorBrush(c)
            };
            Canvas.SetLeft(tb, p.X - 7);
            Canvas.SetTop(tb, p.Y - fs * 0.75);
            Layer.Children.Add(tb);
            editingText = tb;

            tb.LostKeyboardFocus += (_, _) => { if (editingText == tb) CommitText(); };
            tb.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
                {
                    CommitText();
                    e.Handled = true;
                }
            };
            Dispatcher.InvokeAsync(() => { tb.Focus(); Keyboard.Focus(tb); }, DispatcherPriority.Input);
        }

        void CommitText()
        {
            var tb = editingText;
            if (tb == null) return;
            editingText = null;

            int idx = Layer.Children.IndexOf(tb);
            Layer.Children.Remove(tb);
            Area.Focus();
            if (string.IsNullOrWhiteSpace(tb.Text)) return;

            var border = MakeText(tb.Text, tb.FontSize, textColor, textFill);
            Canvas.SetLeft(border, Canvas.GetLeft(tb));
            Canvas.SetTop(border, Canvas.GetTop(tb));
            Layer.Children.Insert(Math.Clamp(idx, 0, Layer.Children.Count), border);
            CommitAdd(new Annotation(border));
        }

        static Border MakeText(string text, double fontSize, Color c, bool withBackground)
        {
            var txt = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = withBackground ? ContrastBrush(c) : new SolidColorBrush(c)
            };
            if (!withBackground)
            {
                double l = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
                // Dezenter Schatten für Lesbarkeit; nur sehr dunkler Text bekommt einen hellen Schein
                txt.Effect = new DropShadowEffect
                {
                    ShadowDepth = 1,
                    BlurRadius = 4,
                    Opacity = l < 0.15 ? 0.6 : 0.8,
                    Color = l < 0.15 ? Colors.White : Colors.Black
                };
            }
            return new Border
            {
                Child = txt,
                Padding = new Thickness(7, 2, 7, 2),
                CornerRadius = new CornerRadius(5),
                Background = withBackground ? new SolidColorBrush(c) : null
            };
        }

        // ------------------------------------------------------------ Nummerierung / Zensur / Zuschneiden

        void AddStep(Point p)
        {
            int n = Annotations().Select(a => a.Step).DefaultIfEmpty(0).Max() + 1;
            double d = CurrentSize;
            var c = CurrentColor;
            var g = new Grid { Width = d, Height = d };
            g.Children.Add(new Ellipse
            {
                Fill = new SolidColorBrush(c),
                Stroke = Brushes.White,
                StrokeThickness = Math.Max(1.5, d / 14),
                Effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 6, Opacity = 0.45 }
            });
            g.Children.Add(new TextBlock
            {
                Text = n.ToString(),
                Foreground = ContrastBrush(c),
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = d * (n >= 10 ? 0.42 : 0.52),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, d * 0.04)
            });
            Canvas.SetLeft(g, p.X - d / 2);
            Canvas.SetTop(g, p.Y - d / 2);
            Layer.Children.Add(g);
            CommitAdd(new Annotation(g) { Step = n });
        }

        void AddCensor(Rect r, bool blur, int strength)
        {
            var a = new CensorAnnotation(r, blur, strength, () => baseBmp);
            Layer.Children.Add(a.El);
            a.Regenerate();
            CommitAdd(a);
        }

        void ApplyCrop(Rect r)
        {
            int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y);
            var ir = new Int32Rect(x, y,
                Math.Min(baseBmp.PixelWidth - x, (int)Math.Ceiling(r.Right) - x),
                Math.Min(baseBmp.PixelHeight - y, (int)Math.Ceiling(r.Bottom) - y));
            if (ir.Width < 2 || ir.Height < 2) return;

            var old = baseBmp;
            var cropped = new CroppedBitmap(old, ir);
            cropped.Freeze();
            var d = new Vector(-ir.X, -ir.Y);
            var anns = Annotations().ToList();

            void Do() { SetBase(cropped); foreach (var a in anns) a.MoveBy(d); Dispatcher.InvokeAsync(Fit, DispatcherPriority.Loaded); }
            void Undo() { SetBase(old); foreach (var a in anns) a.MoveBy(-d); Dispatcher.InvokeAsync(Fit, DispatcherPriority.Loaded); }

            Do();
            Push(Undo, Do);
        }

        // ------------------------------------------------------------ Annotationen verwalten

        IEnumerable<Annotation> Annotations() =>
            Layer.Children.OfType<FrameworkElement>().Select(e => e.Tag as Annotation).Where(a => a != null)!;

        void CommitAdd(Annotation a)
        {
            var el = a.El;
            int idx = Layer.Children.IndexOf(el);
            Push(() => Layer.Children.Remove(el),
                 () => { if (!Layer.Children.Contains(el)) Layer.Children.Insert(Math.Clamp(idx, 0, Layer.Children.Count), el); });
        }

        void RemoveAnnotation(Annotation a)
        {
            var el = a.El;
            int idx = Layer.Children.IndexOf(el);
            if (idx < 0) return;
            Layer.Children.RemoveAt(idx);
            if (selected == a) Select(null);
            Push(() => Layer.Children.Insert(Math.Clamp(idx, 0, Layer.Children.Count), el),
                 () => Layer.Children.Remove(el));
        }

        Annotation? HitAnnotation(Point p)
        {
            Annotation? best = null;
            int bestIdx = -1;
            VisualTreeHelper.HitTest(Layer, null, r =>
            {
                var a = FindAnnotation(r.VisualHit);
                if (a != null)
                {
                    int i = Layer.Children.IndexOf(a.El);
                    if (i > bestIdx) { bestIdx = i; best = a; }
                }
                return HitTestResultBehavior.Continue;
            }, new GeometryHitTestParameters(new EllipseGeometry(p, 5 / ZoomTf.ScaleX, 5 / ZoomTf.ScaleX)));
            return best;
        }

        Annotation? FindAnnotation(DependencyObject? d)
        {
            while (d != null && d != Layer)
            {
                if (d is FrameworkElement fe && fe.Tag is Annotation a && a.El == fe) return a;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        void Select(Annotation? a)
        {
            selected = a;
            UpdateAdorner();
        }

        void UpdateAdorner()
        {
            if (selected == null || !Layer.Children.Contains(selected.El))
            {
                selected = null;
                Overlay.Children.Remove(selAdorner);
                return;
            }
            Layer.UpdateLayout();
            var b = selected.Bounds(Layer);
            if (b.IsEmpty) return;
            double pad = 4 / ZoomTf.ScaleX;
            b.Inflate(pad, pad);
            if (!Overlay.Children.Contains(selAdorner)) Overlay.Children.Add(selAdorner);
            selAdorner.StrokeThickness = 1.5 / ZoomTf.ScaleX;
            PlaceRect(selAdorner, b);
        }

        // ------------------------------------------------------------ Undo / Redo

        void Push(Action undo, Action redo)
        {
            undoStack.Push((undo, redo));
            redoStack.Clear();
            dirty = true;
            UpdateUndoUi();
        }

        void Undo()
        {
            CommitText();
            if (undoStack.Count == 0) return;
            var s = undoStack.Pop();
            s.Undo();
            redoStack.Push(s);
            Select(null);
            dirty = true;
            UpdateUndoUi();
        }

        void Redo()
        {
            CommitText();
            if (redoStack.Count == 0) return;
            var s = redoStack.Pop();
            s.Redo();
            undoStack.Push(s);
            Select(null);
            dirty = true;
            UpdateUndoUi();
        }

        void UpdateUndoUi()
        {
            UndoBtn.IsEnabled = undoStack.Count > 0;
            RedoBtn.IsEnabled = redoStack.Count > 0;
        }

        void Undo_Click(object sender, RoutedEventArgs e) => Undo();
        void Redo_Click(object sender, RoutedEventArgs e) => Redo();

        // ------------------------------------------------------------ Export

        /// <summary>Rendert Bild + alle Markierungen in Originalauflösung.</summary>
        public BitmapSource RenderImage()
        {
            CommitText();
            int w = baseBmp.PixelWidth, h = baseBmp.PixelHeight;
            Surface.UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var vb = new VisualBrush(Surface)
                {
                    Stretch = Stretch.Fill,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, w, h),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, w, h)
                };
                dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        void Copy()
        {
            ImageUtil.CopyToClipboard(RenderImage());
            dirty = false;
            Flash("✓  In die Zwischenablage kopiert");
        }

        void Save()
        {
            try
            {
                var path = ImageUtil.SaveAs(RenderImage(), this);
                if (path != null)
                {
                    dirty = false;
                    Flash("✓  Gespeichert: " + path);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Speichern fehlgeschlagen:\n" + ex.Message, "SnipSnip");
            }
        }

        void Upload()
        {
            dirty = false;
            _ = App.Instance.UploadAsync(RenderImage());
            Flash("Wird hochgeladen …");
        }

        void Copy_Click(object sender, RoutedEventArgs e) => Copy();
        void Save_Click(object sender, RoutedEventArgs e) => Save();
        void Upload_Click(object sender, RoutedEventArgs e) => Upload();
        void NewSnip_Click(object sender, RoutedEventArgs e) => App.Instance.StartCapture(this);
        void Open_Click(object sender, RoutedEventArgs e) => App.Instance.OpenImageFile(this);

        void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files.Where(ImageUtil.IsImageFile))
            {
                try { App.Instance.OpenEditor(ImageUtil.Load(f)); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "SnipSnip"); }
            }
        }

        // ------------------------------------------------------------ Tastatur

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || editingText != null) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            bool ctrl = mods.HasFlag(ModifierKeys.Control);
            bool shift = mods.HasFlag(ModifierKeys.Shift);

            if (ctrl)
            {
                switch (key)
                {
                    case Key.Z when shift: Redo(); break;
                    case Key.Z: Undo(); break;
                    case Key.Y: Redo(); break;
                    case Key.C: Copy(); break;
                    case Key.S: Save(); break;
                    case Key.U: Upload(); break;
                    case Key.N: App.Instance.StartCapture(this); break;
                    case Key.O: App.Instance.OpenImageFile(this); break;
                    case Key.D0: case Key.NumPad0: SetZoom(1); break;
                    case Key.D1: case Key.NumPad1: Fit(); break;
                    case Key.OemPlus: case Key.Add: SetZoom(zoom * 1.25); break;
                    case Key.OemMinus: case Key.Subtract: SetZoom(zoom / 1.25); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }

            if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return;

            switch (key)
            {
                case Key.Delete:
                case Key.Back:
                    if (selected != null) RemoveAnnotation(selected);
                    break;
                case Key.Escape:
                    if (isDown) CancelGesture();
                    Select(null);
                    break;
                case Key.V: SetTool(Tool.Select); break;
                case Key.P: SetTool(Tool.Pen); break;
                case Key.M: SetTool(Tool.Marker); break;
                case Key.L: SetTool(Tool.Line); break;
                case Key.A: SetTool(Tool.Arrow); break;
                case Key.R: SetTool(Tool.Rect); break;
                case Key.E: SetTool(Tool.Ellipse); break;
                case Key.T: SetTool(Tool.Text); break;
                case Key.N: SetTool(Tool.Step); break;
                case Key.X: SetTool(Tool.Pixelate); break;
                case Key.B: SetTool(Tool.Blur); break;
                case Key.S: SetTool(Tool.Blackout); break;
                case Key.C: SetTool(Tool.Crop); break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            CommitText();
            if (dirty && undoStack.Count > 0)
            {
                var r = MessageBox.Show(this, "Die Änderungen wurden noch nicht gespeichert, kopiert oder hochgeladen.\nTrotzdem schließen?",
                    "SnipSnip", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) e.Cancel = true;
            }
            base.OnClosing(e);
        }
    }
}
