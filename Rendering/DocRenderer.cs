using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace SnipSnip
{
    /// <summary>
    /// Rendert die README-Bilder aus den echten UI-Komponenten über einen künstlichen Demo-Desktop.
    /// Aufruf: SnipSnip.exe --render-docs &lt;ordner&gt;
    /// </summary>
    internal static class DocRenderer
    {
        const double W = 1280, H = 720;   // Layout in DIP
        const double Scale = 1.5;        // Ausgabe in 1,5-facher Auflösung (1920 × 1080)

        static readonly Color Red = Color.FromRgb(0xE5, 0x39, 0x35);
        static readonly Color Yellow = Color.FromRgb(0xFD, 0xD8, 0x35);

        // Merkpunkte im Demo-Desktop (DIP), werden nach dem Layout ermittelt
        sealed class Demo
        {
            public FrameworkElement Root = null!, AppWindow = null!, Content = null!, Header = null!,
                CustomerCard = null!, ChartCard = null!, Email = null!, Iban = null!, ApiKey = null!,
                Total = null!, MarkRow = null!, HeadRow = null!, Table = null!, Pdf = null!;
        }

        public static void Run(string outDir)
        {
            Directory.CreateDirectory(outDir);

            var wallpaper = Rasterize(BuildWallpaper(), W, H);
            var demo = BuildDesktop();
            var desktop = Rasterize(demo.Root, W, H);
            Rect B(FrameworkElement e) => e.TransformToAncestor(demo.Root).TransformBounds(new Rect(e.RenderSize));
            Rect Phys(Rect r) => new(r.X * Scale, r.Y * Scale, r.Width * Scale, r.Height * Scale);

            // 1) Hero: Bereichsauswahl mit Leiste, Größenanzeige und Lupe
            // Von unten links nach oben rechts aufgezogen: Mauszeiger + Lupe auf der Ecke des blauen Buttons
            var cust = B(demo.CustomerCard);
            var chartB = B(demo.ChartCard);
            var pdfB = B(demo.Pdf);
            var corner = new Point(pdfB.Right - 1, pdfB.Top + 1);
            var sel = new Rect(new Point(cust.X - 14, chartB.Bottom + 14), corner);
            var cursor = new Point(corner.X * Scale, corner.Y * Scale);
            Save(RenderOverlay(desktop, CaptureMode.Rectangle, AfterCapture.Editor, Phys(sel), cursor), outDir, "hero.png");

            // 2) Fenster-Modus
            var win = B(demo.AppWindow);
            var winCursor = new Point((win.X + 520) * Scale, (win.Y + 300) * Scale);
            Save(RenderOverlay(desktop, CaptureMode.Window, AfterCapture.Upload, Phys(win), winCursor), outDir, "capture-window.png");

            // 3) Editor mit Markierungen und Zensur
            var crop = Rect.Union(B(demo.HeadRow), B(demo.Table));
            crop.Inflate(18, 16);
            var editor = RenderEditor(desktop, wallpaper, crop, demo, B, out var annotated);
            Save(editor, outDir, "editor.png");

            // 4) Einstellungen
            Save(RenderSettings(wallpaper), outDir, "settings.png");

            // 5) Logo für das README (GitHub zeigt .ico nicht zuverlässig an)
            Save(AppIcon(256), outDir, "logo.png");

            // 6) Upload-Benachrichtigung
            Save(RenderToast(wallpaper, annotated), outDir, "toast.png");
        }

        // ================================================================== Szenen

        static BitmapSource RenderOverlay(BitmapSource desktop, CaptureMode mode, AfterCapture action, Rect selection, Point cursor)
        {
            var mon = new Rect(0, 0, desktop.PixelWidth, desktop.PixelHeight);
            var session = CaptureSession.CreateForDocs(desktop, mon, mode, action, selection, cursor);
            var ow = new OverlayWindow(session, mon, desktop);
            ow.AttachToolbar(new CaptureToolbar(session));
            var root = Detach(ow);
            return Rasterize(root, W, H, ow.Render);
        }

        static BitmapSource RenderEditor(BitmapSource desktop, BitmapSource wallpaper, Rect cropDip, Demo demo,
            Func<FrameworkElement, Rect> bounds, out BitmapSource annotated)
        {
            var ir = new Int32Rect((int)(cropDip.X * Scale), (int)(cropDip.Y * Scale), (int)(cropDip.Width * Scale), (int)(cropDip.Height * Scale));
            var img = ImageUtil.ToBgra(new CroppedBitmap(desktop, ir));

            // Desktop-DIP -> Bildpixel
            Rect R(FrameworkElement e, double inflate = 0)
            {
                var r = bounds(e);
                r.Inflate(inflate, inflate);
                return new Rect((r.X - cropDip.X) * Scale, (r.Y - cropDip.Y) * Scale, r.Width * Scale, r.Height * Scale);
            }

            var ed = new EditorWindow(img);
            var root = Detach(ed, close: false);
            ed.DocsSetZoom(1 / Scale);

            double s = Scale;
            var header = R(demo.Header, 8);
            var email = R(demo.Email, 3);
            var iban = R(demo.Iban, 3);
            var api = R(demo.ApiKey, 3);
            var total = R(demo.Total, 4);
            var row = R(demo.MarkRow);
            var chart = R(demo.ChartCard);

            ed.DocsRect(header, Red, 3 * s);
            ed.DocsCensor(email, Tool.Pixelate, (int)(7 * s));
            ed.DocsCensor(iban, Tool.Blur, (int)(6 * s));
            ed.DocsCensor(api, Tool.Blackout, 0);
            ed.DocsMarker(new Point(row.X + 4 * s, row.Y + row.Height / 2), new Point(row.Right - 4 * s, row.Y + row.Height / 2), Yellow, row.Height * 0.85);
            var tail = new Point(total.X - 150 * s, total.Y - 55 * s);
            ed.DocsArrow(tail, new Point(total.X - 6 * s, total.Y + total.Height / 2), Red, 3.5 * s);
            ed.DocsStep(new Point(header.X + 2 * s, header.Y + 2 * s), Red, 26 * s);
            ed.DocsStep(new Point(chart.Right - 22 * s, chart.Y + 22 * s), Red, 26 * s);
            ed.DocsStep(new Point(tail.X - 16 * s, tail.Y - 10 * s), Red, 26 * s);
            ed.DocsText(new Point(chart.X + 14 * s, chart.Y + 40 * s), "Bitte prüfen!", Red, 15 * s, true);

            BitmapSource? export = null;
            var shot = Rasterize(Framed(root, "SnipSnip", 1200, 760, wallpaper), 1280, 840, () => export = ed.RenderImage());
            annotated = export!;
            ed.DocsClose();
            return shot;
        }

        static BitmapSource RenderSettings(BitmapSource wallpaper)
        {
            var root = Detach(new SettingsWindow());
            var framed = Framed(root, "SnipSnip – Einstellungen", 580, 1150, wallpaper, 40);
            return Rasterize(framed, 660, 1230);
        }

        static BitmapSource RenderToast(BitmapSource wallpaper, BitmapSource thumb)
        {
            var card = Toast.CreateDocsCard("Hochgeladen ✓", "https://i.imgur.com/SnIpSnP.png\nLink wurde kopiert.", thumb,
                ("Öffnen", () => { }), ("Link kopieren", () => { }));
            var g = new Grid { Width = 560, Height = 220, Background = WallpaperBrush(wallpaper, new Rect(0.55, 0.62, 0.45, 0.32)) };
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(card);
            return Rasterize(g, 560, 220);
        }

        // ================================================================== Rahmen & Hilfen

        /// <summary>Fenster-Rahmen mit Titelleiste und Schatten auf Wallpaper-Hintergrund.</summary>
        static FrameworkElement Framed(FrameworkElement content, string title, double w, double h,
            BitmapSource? wallpaper = null, double pad = 40)
        {
            var bar = new DockPanel { Height = 34, Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x25)) };
            var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var buttons = new TextBlock
            {
                Text = "—      ☐      ✕",
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            caption.Children.Add(new Image { Source = AppIcon(), Width = 16, Height = 16, Margin = new Thickness(12, 0, 8, 0) });
            caption.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)),
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.FindResource("UiFont")
            });
            bar.Children.Add(caption);

            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(content);

            var window = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Application.Current.FindResource("WindowBrush"),
                Child = dock,
                Clip = new RectangleGeometry(new Rect(0, 0, w, h), 10, 10)
            };
            var shadow = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Black,
                Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.55 }
            };
            var outline = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };

            var g = new Grid { Width = w + 2 * pad, Height = h + 2 * pad };
            g.Background = wallpaper != null
                ? new ImageBrush(wallpaper) { Stretch = Stretch.UniformToFill }
                : new LinearGradientBrush(Color.FromRgb(0x14, 0x1E, 0x52), Color.FromRgb(0x9A, 0x40, 0x7C), 35);
            g.Children.Add(shadow);
            g.Children.Add(window);
            g.Children.Add(outline);
            return g;
        }

        /// <summary>Nimmt den Inhalt aus dem (nie angezeigten) Fenster und übernimmt die vererbten Schrift-Eigenschaften.</summary>
        static FrameworkElement Detach(Window w, bool close = true)
        {
            var root = (FrameworkElement)w.Content;
            w.Content = null;
            TextElement.SetForeground(root, w.Foreground);
            TextElement.SetFontFamily(root, w.FontFamily);
            TextElement.SetFontSize(root, w.FontSize);
            if (close) w.Close();
            return root;
        }

        static Brush WallpaperBrush(BitmapSource wallpaper, Rect relativeViewbox) =>
            new ImageBrush(wallpaper) { Viewbox = relativeViewbox, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.UniformToFill };

        static BitmapSource AppIcon(int size = 32)
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!;
            using var s = res.Stream;
            var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return dec.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
        }

        static BitmapSource Rasterize(FrameworkElement el, double w, double h, Action? afterLayout = null)
        {
            el.Measure(new Size(w, h));
            el.Arrange(new Rect(0, 0, w, h));
            el.UpdateLayout();
            afterLayout?.Invoke();
            el.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Round(w * Scale), (int)Math.Round(h * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            rtb.Render(el);
            return ImageUtil.ToBgra(rtb); // 96 DPI -> 1 Pixel = 1 DIP, wie eine echte Aufnahme
        }

        static void Save(BitmapSource b, string dir, string name) =>
            File.WriteAllBytes(IOPath.Combine(dir, name), ImageUtil.Encode(b, "png"));

        // ================================================================== Demo-Desktop

        static FrameworkElement BuildWallpaper()
        {
            var c = new Canvas { Width = W, Height = H, ClipToBounds = true };
            c.Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0x0E, 0x16, 0x45), 0),
                    new(Color.FromRgb(0x33, 0x27, 0x78), 0.55),
                    new(Color.FromRgb(0xB0, 0x44, 0x78), 1)
                }, new Point(0, 0), new Point(1, 1));

            void Blob(double x, double y, double d, Color col)
            {
                var e = new Ellipse
                {
                    Width = d,
                    Height = d,
                    Fill = new RadialGradientBrush(col, Color.FromArgb(0, col.R, col.G, col.B))
                };
                Canvas.SetLeft(e, x);
                Canvas.SetTop(e, y);
                c.Children.Add(e);
            }
            Blob(-200, -260, 760, Color.FromArgb(0x70, 0x4F, 0x8C, 0xFF));
            Blob(820, 280, 640, Color.FromArgb(0x70, 0xFF, 0x7A, 0x59));
            Blob(380, 360, 560, Color.FromArgb(0x50, 0x33, 0xD6, 0xC4));
            return c;
        }

        static Demo BuildDesktop()
        {
            var d = new Demo();
            var root = new Grid { Width = W, Height = H };
            root.Children.Add(BuildWallpaper());
            d.Root = root;

            var font = new FontFamily("Segoe UI");
            var ink = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x24));
            var dim = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            var accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x5B, 0xEA));
            var line = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEE));

            TextBlock T(string text, double size, Brush? fg = null, FontWeight? w = null) =>
                new() { Text = text, FontSize = size, Foreground = fg ?? ink, FontWeight = w ?? FontWeights.Normal, FontFamily = font };

            // Desktop-Symbole
            var icons = new StackPanel { Margin = new Thickness(22, 24, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            foreach (var (label, col) in new[] { ("Projekte", "#FFF4B740"), ("Papierkorb", "#FF9AA4B8"), ("Notizen", "#FF5BC88A") })
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 18), Width = 72 };
                sp.Children.Add(new Border { Width = 40, Height = 34, CornerRadius = new CornerRadius(6), Background = (Brush)new BrushConverter().ConvertFromString(col)! });
                sp.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 11, FontFamily = font, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
                icons.Children.Add(sp);
            }
            root.Children.Add(icons);

            // Taskleiste
            var taskbar = new Border
            {
                Height = 44,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x1A, 0x1A, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            var tbIcons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var col in new[] { "#FF4F8CFF", "#FFF4B740", "#FF2F5BEA", "#FF5BC88A", "#FFE5536F", "#FF8C5CFF" })
                tbIcons.Children.Add(new Border { Width = 24, Height = 24, Margin = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(6), Background = (Brush)new BrushConverter().ConvertFromString(col)! });
            var clock = new TextBlock { Text = "14:32\n23.10.2026", Foreground = Brushes.White, FontSize = 10.5, FontFamily = font, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var tbGrid = new Grid();
            tbGrid.Children.Add(tbIcons);
            tbGrid.Children.Add(clock);
            taskbar.Child = tbGrid;
            root.Children.Add(taskbar);

            // App-Fenster "Kundenportal"
            var app = new Border
            {
                Width = 1000,
                Height = 560,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(104, 52, 0, 0),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFC)),
                Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.45 },
                ClipToBounds = false
            };
            d.AppWindow = app;
            var appGrid = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, 1000, 560), 10, 10) };
            appGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            appGrid.RowDefinitions.Add(new RowDefinition());
            appGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            appGrid.ColumnDefinitions.Add(new ColumnDefinition());
            app.Child = appGrid;

            var title = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF4)) };
            Grid.SetColumnSpan(title, 2);
            var winBtns = T("—      ☐      ✕", 11, dim);
            winBtns.Margin = new Thickness(0, 0, 16, 0);
            winBtns.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(winBtns, Dock.Right);
            title.Children.Add(winBtns);
            var tt = T("Kundenportal – Rechnungen", 12, dim);
            tt.VerticalAlignment = VerticalAlignment.Center;
            tt.Margin = new Thickness(16, 0, 0, 0);
            title.Children.Add(tt);
            appGrid.Children.Add(title);

            // Seitenleiste
            var side = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)) };
            Grid.SetRow(side, 1);
            var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 20, 0, 22) };
            logo.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = accent });
            var ln = T("Acme Cloud", 15, ink, FontWeights.SemiBold);
            ln.Margin = new Thickness(10, 0, 0, 0);
            logo.Children.Add(ln);
            side.Children.Add(logo);
            foreach (var (item, active) in new[] { ("Übersicht", false), ("Rechnungen", true), ("Kunden", false), ("Berichte", false), ("Einstellungen", false) })
            {
                var row = new Border
                {
                    Margin = new Thickness(10, 1, 10, 1),
                    Padding = new Thickness(12, 8, 12, 8),
                    CornerRadius = new CornerRadius(7),
                    Background = active ? new SolidColorBrush(Color.FromRgb(0xE3, 0xEB, 0xFF)) : Brushes.Transparent
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = active ? accent : new SolidColorBrush(Color.FromRgb(0xC5, 0xCB, 0xD6)) });
                var it = T(item, 13, active ? accent : ink, active ? FontWeights.SemiBold : FontWeights.Normal);
                it.Margin = new Thickness(10, 0, 0, 0);
                sp.Children.Add(it);
                row.Child = sp;
                side.Children.Add(row);
            }
            appGrid.Children.Add(side);

            // Inhalt
            var content = new StackPanel { Margin = new Thickness(28, 22, 28, 0) };
            Grid.SetRow(content, 1);
            Grid.SetColumn(content, 1);
            d.Content = content;
            appGrid.Children.Add(content);

            var headRow = new DockPanel();
            var pdf = new Border { Background = accent, CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center };
            pdf.Child = T("PDF herunterladen", 12.5, Brushes.White, FontWeights.SemiBold);
            DockPanel.SetDock(pdf, Dock.Right);
            d.Pdf = pdf;
            headRow.Children.Add(pdf);
            var headLeft = new StackPanel { Orientation = Orientation.Horizontal };
            var head = T("Rechnung #2026-0114", 24, ink, FontWeights.SemiBold);
            d.Header = head;
            headLeft.Children.Add(head);
            var pill = new Border { Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xDB)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            pill.Child = T("Offen", 12, new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00)), FontWeights.SemiBold);
            headLeft.Children.Add(pill);
            headRow.Children.Add(headLeft);
            content.Children.Add(headRow);
            d.HeadRow = headRow;
            var sub = T("Erstellt am 23.10.2026  ·  fällig am 06.11.2026", 13, dim);
            sub.Margin = new Thickness(0, 4, 0, 18);
            content.Children.Add(sub);

            Border Card(UIElement child) => new()
            {
                Background = Brushes.White,
                BorderBrush = line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 18, 14),
                Child = child
            };

            var cards = new Grid();
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cust = new StackPanel();
            cust.Children.Add(T("Kunde", 13, dim, FontWeights.SemiBold));
            var fields = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            fields.ColumnDefinitions.Add(new ColumnDefinition());
            string[] labels = { "Name", "E-Mail", "IBAN", "API-Key" };
            string[] values = { "Erika Musterfrau", "erika.musterfrau@example.com", "DE89 3704 0044 0532 0130 00", "sk_live_4f9aT2kQ8mXz71bR" };
            for (int i = 0; i < labels.Length; i++)
            {
                fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
                var l = T(labels[i], 13, dim);
                l.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(l, i);
                fields.Children.Add(l);
                var v = T(values[i], 13.5, ink, i == 0 ? FontWeights.SemiBold : FontWeights.Normal);
                if (i == 3) v.FontFamily = new FontFamily("Consolas");
                v.VerticalAlignment = VerticalAlignment.Center;
                v.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetRow(v, i);
                Grid.SetColumn(v, 1);
                fields.Children.Add(v);
                if (i == 1) d.Email = v;
                if (i == 2) d.Iban = v;
                if (i == 3) d.ApiKey = v;
            }
            cust.Children.Add(fields);
            var custCard = Card(cust);
            d.CustomerCard = custCard;
            cards.Children.Add(custCard);

            var chart = new StackPanel();
            chart.Children.Add(T("Umsatz – letzte 6 Monate", 13, dim, FontWeights.SemiBold));
            var bars = new Grid { Height = 104, Margin = new Thickness(0, 12, 0, 0) };
            double[] vals = { 0.42, 0.55, 0.48, 0.7, 0.62, 0.92 };
            string[] months = { "Mai", "Jun", "Jul", "Aug", "Sep", "Okt" };
            for (int i = 0; i < vals.Length; i++)
            {
                bars.ColumnDefinitions.Add(new ColumnDefinition());
                var col = new DockPanel { Margin = new Thickness(6, 0, 6, 0) };
                var m = T(months[i], 11, dim);
                m.HorizontalAlignment = HorizontalAlignment.Center;
                DockPanel.SetDock(m, Dock.Bottom);
                col.Children.Add(m);
                col.Children.Add(new Border
                {
                    Height = 82 * vals[i],
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(4, 4, 0, 0),
                    Margin = new Thickness(0, 0, 0, 4),
                    Background = new LinearGradientBrush(Color.FromRgb(0x6A, 0x9B, 0xFF), Color.FromRgb(0x2F, 0x5B, 0xEA), 90)
                });
                Grid.SetColumn(col, i);
                bars.Children.Add(col);
            }
            chart.Children.Add(bars);
            var chartCard = Card(chart);
            d.ChartCard = chartCard;
            Grid.SetColumn(chartCard, 2);
            cards.Children.Add(chartCard);
            content.Children.Add(cards);

            // Positionen
            var table = new StackPanel();
            table.Children.Add(T("Positionen", 13, dim, FontWeights.SemiBold));
            var items = new[] { ("Cloud Hosting Pro", "1 ×", "89,00 €"), ("Support Premium", "1 ×", "49,00 €"), ("Zusätzlicher Speicher (500 GB)", "2 ×", "12,50 €") };
            foreach (var (name, qty, price) in items)
            {
                var r = new Grid { Height = 30 };
                r.ColumnDefinitions.Add(new ColumnDefinition());
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                var n = T(name, 13.5); n.VerticalAlignment = VerticalAlignment.Center;
                var q = T(qty, 13.5, dim); q.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(q, 1);
                var p = T(price, 13.5); p.VerticalAlignment = VerticalAlignment.Center; p.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(p, 2);
                r.Children.Add(n); r.Children.Add(q); r.Children.Add(p);
                table.Children.Add(r);
                if (name.StartsWith("Support")) d.MarkRow = r;
            }
            table.Children.Add(new Border { Height = 1, Background = line, Margin = new Thickness(0, 6, 0, 8) });
            var totalRow = new DockPanel();
            var totalVal = T("163,00 €", 16, ink, FontWeights.Bold);
            DockPanel.SetDock(totalVal, Dock.Right);
            totalRow.Children.Add(totalVal);
            totalRow.Children.Add(T("Gesamt", 15, ink, FontWeights.SemiBold));
            table.Children.Add(totalRow);
            d.Total = totalVal;
            var tableCard = Card(table);
            tableCard.Margin = new Thickness(0, 16, 0, 0);
            content.Children.Add(tableCard);
            d.Table = tableCard;

            root.Children.Add(app);
            return d;
        }
    }
}
