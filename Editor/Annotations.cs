using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnipSnip
{
    public enum Tool { Select, Pen, Marker, Line, Arrow, Rect, Ellipse, Text, Step, Pixelate, Blur, Blackout, Crop }

    /// <summary>Ein Element auf der Zeichenebene (Strich, Pfeil, Text, …). Verschoben wird per TranslateTransform.</summary>
    public class Annotation
    {
        public FrameworkElement El { get; }
        public int Step { get; init; }
        protected readonly TranslateTransform Shift = new();

        public Annotation(FrameworkElement el)
        {
            El = el;
            el.Tag = this;
            el.RenderTransform = Shift;
        }

        public virtual void MoveBy(Vector d)
        {
            Shift.X += d.X;
            Shift.Y += d.Y;
        }

        /// <summary>Live-Verschieben während des Ziehens (für teure Elemente ggf. nur visuell).</summary>
        public virtual void DragBy(Vector d) => MoveBy(d);

        public virtual void EndDrag() { }

        public Rect Bounds(Visual layer)
        {
            var b = VisualTreeHelper.GetDescendantBounds(El);
            if (b.IsEmpty) return Rect.Empty;
            return El.TransformToAncestor(layer).TransformBounds(b);
        }
    }

    /// <summary>Verpixelter oder weichgezeichneter Bereich – wird aus dem Originalbild neu berechnet.</summary>
    public sealed class CensorAnnotation : Annotation
    {
        Rect region;
        readonly bool blur;
        readonly int strength;
        readonly Func<BitmapSource> getBase;

        Image Img => (Image)El;

        public CensorAnnotation(Rect region, bool blur, int strength, Func<BitmapSource> getBase)
            : base(new Image { Stretch = Stretch.Fill })
        {
            this.region = region;
            this.blur = blur;
            this.strength = strength;
            this.getBase = getBase;
            RenderOptions.SetBitmapScalingMode(El, BitmapScalingMode.NearestNeighbor);
        }

        public override void MoveBy(Vector d)
        {
            region.Offset(d);
            Regenerate();
        }

        public override void DragBy(Vector d)
        {
            Shift.X += d.X;
            Shift.Y += d.Y;
        }

        public override void EndDrag()
        {
            var d = new Vector(Shift.X, Shift.Y);
            Shift.X = Shift.Y = 0;
            MoveBy(d);
        }

        public void Regenerate()
        {
            var b = getBase();
            var r = Rect.Intersect(region, new Rect(0, 0, b.PixelWidth, b.PixelHeight));
            if (r.IsEmpty || r.Width < 1 || r.Height < 1)
            {
                Img.Source = null;
                Img.Width = Img.Height = 0;
                return;
            }
            int x0 = (int)Math.Floor(r.X), y0 = (int)Math.Floor(r.Y);
            int x1 = (int)Math.Ceiling(r.Right), y1 = (int)Math.Ceiling(r.Bottom);
            var ir = new Int32Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));

            Img.Source = blur ? ImageEffects.Blur(b, ir, strength) : ImageEffects.Pixelate(b, ir, strength);
            Canvas.SetLeft(Img, ir.X);
            Canvas.SetTop(Img, ir.Y);
            Img.Width = ir.Width;
            Img.Height = ir.Height;
        }
    }
}
