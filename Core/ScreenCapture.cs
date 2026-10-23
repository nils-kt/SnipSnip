using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace SnipSnip
{
    internal static class ScreenCapture
    {
        /// <summary>Fotografiert den gesamten virtuellen Bildschirm (alle Monitore) in physischen Pixeln.</summary>
        public static (BitmapSource Image, Rect Bounds) CaptureVirtualScreen()
        {
            var vb = WinForms.SystemInformation.VirtualScreen;
            IntPtr screenDc = Gdi.GetDC(IntPtr.Zero);
            IntPtr memDc = Gdi.CreateCompatibleDC(screenDc);
            IntPtr hbmp = Gdi.CreateCompatibleBitmap(screenDc, vb.Width, vb.Height);
            IntPtr old = Gdi.SelectObject(memDc, hbmp);
            try
            {
                // CAPTUREBLT: auch Layered-/transparente Fenster mit aufnehmen
                Gdi.BitBlt(memDc, 0, 0, vb.Width, vb.Height, screenDc, vb.X, vb.Y, Gdi.SRCCOPY | Gdi.CAPTUREBLT);
                Gdi.SelectObject(memDc, old);

                var raw = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                // Alpha des GDI-Bitmaps ist undefiniert -> als Bgr32 behandeln, 96 DPI erzwingen
                int stride = vb.Width * 4;
                var buf = new byte[stride * vb.Height];
                new FormatConvertedBitmap(raw, PixelFormats.Bgr32, null, 0).CopyPixels(buf, stride, 0);
                var src = BitmapSource.Create(vb.Width, vb.Height, 96, 96, PixelFormats.Bgr32, null, buf, stride);
                src.Freeze();
                return (src, new Rect(vb.X, vb.Y, vb.Width, vb.Height));
            }
            finally
            {
                Gdi.DeleteObject(hbmp);
                Gdi.DeleteDC(memDc);
                Gdi.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public static List<Rect> GetMonitors() =>
            WinForms.Screen.AllScreens
                .Select(s => new Rect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height))
                .ToList();

        public static Rect PrimaryMonitor()
        {
            var b = WinForms.Screen.PrimaryScreen!.Bounds;
            return new Rect(b.X, b.Y, b.Width, b.Height);
        }
    }

    internal static class Gdi
    {
        public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDc);
    }

    internal static class WindowFinder
    {
        static readonly HashSet<string> IgnoredClasses = new()
        {
            "Progman", "WorkerW", "Shell_TrayWnd_Ghost"
        };

        /// <summary>Sichtbare Top-Level-Fenster in Z-Reihenfolge (oberstes zuerst), physische Koordinaten.</summary>
        public static List<Rect> GetWindows()
        {
            var list = new List<Rect>();
            var shell = Native.GetShellWindow();
            var cls = new StringBuilder(256);

            Native.EnumWindows((h, _) =>
            {
                if (h == shell || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;

                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true;
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && Native.GetWindowTextLength(h) == 0) return true;

                cls.Clear();
                Native.GetClassName(h, cls, cls.Capacity);
                if (IgnoredClasses.Contains(cls.ToString())) return true;

                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, 16) != 0)
                    Native.GetWindowRect(h, out r);

                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (w > 4 && ht > 4) list.Add(new Rect(r.Left, r.Top, w, ht));
                return true;
            }, IntPtr.Zero);

            return list;
        }
    }
}
