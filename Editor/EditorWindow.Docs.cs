using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SnipSnip
{
    // Hilfen für DocRenderer: Markierungen programmatisch setzen, ohne Maus-Eingaben.
    public partial class EditorWindow
    {
        /// <summary>Schließt ohne Rückfrage nach ungespeicherten Änderungen.</summary>
        internal void DocsClose()
        {
            dirty = false;
            Close();
        }

        internal void DocsSetZoom(double scale)
        {
            ZoomTf.ScaleX = ZoomTf.ScaleY = scale;
            ZoomLabel.Content = "100 %";
            RenderOptions.SetBitmapScalingMode(BaseImage, BitmapScalingMode.HighQuality);
        }

        internal void DocsRect(Rect r, Color c, double thickness)
        {
            var sh = new Rectangle { Stroke = new SolidColorBrush(c), StrokeThickness = thickness, RadiusX = 3, RadiusY = 3 };
            PlaceRect(sh, r);
            Layer.Children.Add(sh);
            CommitAdd(new Annotation(sh));
        }

        internal void DocsArrow(Point a, Point b, Color c, double thickness)
        {
            var br = new SolidColorBrush(c);
            var p = new Path
            {
                Stroke = br,
                Fill = br,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = ArrowGeometry(a, b, thickness, true)
            };
            Layer.Children.Add(p);
            CommitAdd(new Annotation(p));
        }

        internal void DocsMarker(Point a, Point b, Color c, double thickness)
        {
            var pl = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B)),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Square,
                StrokeEndLineCap = PenLineCap.Square,
                Points = new PointCollection { a, b }
            };
            Layer.Children.Add(pl);
            CommitAdd(new Annotation(pl));
        }

        internal void DocsCensor(Rect r, Tool kind, int strength)
        {
            if (kind == Tool.Blackout)
            {
                var sh = new Rectangle { Fill = Brushes.Black };
                PlaceRect(sh, r);
                Layer.Children.Add(sh);
                CommitAdd(new Annotation(sh));
            }
            else AddCensor(r, kind == Tool.Blur, strength);
        }

        internal void DocsStep(Point center, Color c, double size)
        {
            var prev = tool;
            tool = Tool.Step;
            colors[Tool.Step] = c;
            sizes[Tool.Step] = size;
            AddStep(center);
            tool = prev;
        }

        internal void DocsText(Point topLeft, string text, Color c, double fontSize, bool withBackground)
        {
            var b = MakeText(text, fontSize, c, withBackground);
            Canvas.SetLeft(b, topLeft.X);
            Canvas.SetTop(b, topLeft.Y);
            Layer.Children.Add(b);
            CommitAdd(new Annotation(b));
        }
    }
}
