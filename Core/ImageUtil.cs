using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SnipSnip
{
    internal static class ImageUtil
    {
        /// <summary>Unabhängige Kopie als Bgra32 mit 96 DPI (1 Pixel = 1 DIP).</summary>
        public static BitmapSource ToBgra(BitmapSource s)
        {
            BitmapSource conv = s.Format == PixelFormats.Bgra32 ? s : new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
            int w = s.PixelWidth, h = s.PixelHeight, stride = w * 4;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);
            var r = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
            r.Freeze();
            return r;
        }

        /// <summary>Legt das Bild auf einen weißen Hintergrund (für JPG / DIB ohne Alpha).</summary>
        public static BitmapSource Flatten(BitmapSource b)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var r = new Rect(0, 0, b.PixelWidth, b.PixelHeight);
                dc.DrawRectangle(Brushes.White, null, r);
                dc.DrawImage(b, r);
            }
            var rtb = new RenderTargetBitmap(b.PixelWidth, b.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var bgr = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
            bgr.Freeze();
            return bgr;
        }

        public static byte[] Encode(BitmapSource b, string format)
        {
            BitmapEncoder enc;
            BitmapSource src = b;
            switch (format.ToLowerInvariant())
            {
                case "jpg":
                case "jpeg":
                    enc = new JpegBitmapEncoder { QualityLevel = 92 };
                    src = Flatten(b);
                    break;
                case "bmp":
                    enc = new BmpBitmapEncoder();
                    src = Flatten(b);
                    break;
                default:
                    enc = new PngBitmapEncoder();
                    break;
            }
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        public static void CopyToClipboard(BitmapSource b)
        {
            var data = new DataObject();
            data.SetImage(Flatten(b));
            data.SetData("PNG", new MemoryStream(Encode(b, "png")), false);
            SetClipboard(() => Clipboard.SetDataObject(data, true));
        }

        public static void CopyText(string text) => SetClipboard(() => Clipboard.SetText(text));

        static void SetClipboard(Action a)
        {
            for (int i = 0; i < 10; i++)
            {
                try { a(); return; }
                catch (COMException) { Thread.Sleep(60); }
                catch (ExternalException) { Thread.Sleep(60); }
            }
        }

        public static string DefaultFileName(string ext) => $"SnipSnip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}";

        /// <summary>Speichert ohne Dialog im konfigurierten Ordner.</summary>
        public static string SaveToFolder(BitmapSource b)
        {
            var s = App.Settings;
            string folder = string.IsNullOrWhiteSpace(s.SaveFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnipSnip")
                : s.SaveFolder;
            Directory.CreateDirectory(folder);
            string ext = s.ImageFormat == "jpg" ? "jpg" : "png";
            string path = Path.Combine(folder, DefaultFileName(ext));
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(DefaultFileName(ext))}_{i}.{ext}");
            File.WriteAllBytes(path, Encode(b, ext));
            return path;
        }

        public static string? SaveAs(BitmapSource b, Window? owner)
        {
            var s = App.Settings;
            var dlg = new SaveFileDialog
            {
                Title = "Bild speichern",
                Filter = "PNG-Bild (*.png)|*.png|JPEG-Bild (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp",
                FilterIndex = s.ImageFormat == "jpg" ? 2 : 1,
                FileName = DefaultFileName(s.ImageFormat == "jpg" ? "jpg" : "png"),
                AddExtension = true
            };
            try
            {
                Directory.CreateDirectory(s.SaveFolder);
                dlg.InitialDirectory = s.SaveFolder;
            }
            catch { }

            if (dlg.ShowDialog(owner) != true) return null;
            string ext = Path.GetExtension(dlg.FileName).TrimStart('.').ToLowerInvariant();
            File.WriteAllBytes(dlg.FileName, Encode(b, ext));
            return dlg.FileName;
        }

        public static BitmapSource Load(string path)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(Path.GetFullPath(path));
            bi.EndInit();
            bi.Freeze();
            return ToBgra(bi);
        }

        public static bool IsImageFile(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            return e is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" or ".ico";
        }
    }

    /// <summary>Software-Effekte für die Zensur (deterministisch, nicht rückrechenbar wie ein schwacher Blur).</summary>
    internal static class ImageEffects
    {
        static byte[] Read(BitmapSource src, Int32Rect r)
        {
            BitmapSource s = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var buf = new byte[r.Width * r.Height * 4];
            s.CopyPixels(r, buf, r.Width * 4, 0);
            return buf;
        }

        static BitmapSource Create(int w, int h, byte[] buf)
        {
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
            b.Freeze();
            return b;
        }

        public static BitmapSource Pixelate(BitmapSource src, Int32Rect r, int block)
        {
            block = Math.Max(2, block);
            var buf = Read(src, r);
            int w = r.Width, h = r.Height, stride = w * 4;
            for (int by = 0; by < h; by += block)
            {
                for (int bx = 0; bx < w; bx += block)
                {
                    int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by), n = bw * bh;
                    long sb = 0, sg = 0, sr = 0, sa = 0;
                    for (int y = by; y < by + bh; y++)
                    {
                        int o = y * stride + bx * 4;
                        for (int x = 0; x < bw; x++, o += 4)
                        {
                            sb += buf[o]; sg += buf[o + 1]; sr += buf[o + 2]; sa += buf[o + 3];
                        }
                    }
                    byte B = (byte)(sb / n), G = (byte)(sg / n), R = (byte)(sr / n), A = (byte)(sa / n);
                    for (int y = by; y < by + bh; y++)
                    {
                        int o = y * stride + bx * 4;
                        for (int x = 0; x < bw; x++, o += 4)
                        {
                            buf[o] = B; buf[o + 1] = G; buf[o + 2] = R; buf[o + 3] = A;
                        }
                    }
                }
            }
            return Create(w, h, buf);
        }

        public static BitmapSource Blur(BitmapSource src, Int32Rect r, int radius)
        {
            radius = Math.Max(1, radius);
            int pad = radius * 2;
            int ex0 = Math.Max(0, r.X - pad), ey0 = Math.Max(0, r.Y - pad);
            int ex1 = Math.Min(src.PixelWidth, r.X + r.Width + pad), ey1 = Math.Min(src.PixelHeight, r.Y + r.Height + pad);
            var ext = new Int32Rect(ex0, ey0, ex1 - ex0, ey1 - ey0);

            var buf = Read(src, ext);
            var tmp = new byte[buf.Length];
            int w = ext.Width, h = ext.Height;
            for (int pass = 0; pass < 3; pass++)
            {
                BoxH(buf, tmp, w, h, radius);
                BoxV(tmp, buf, w, h, radius);
            }

            int ox = r.X - ext.X, oy = r.Y - ext.Y;
            var outBuf = new byte[r.Width * r.Height * 4];
            for (int y = 0; y < r.Height; y++)
                Buffer.BlockCopy(buf, ((oy + y) * w + ox) * 4, outBuf, y * r.Width * 4, r.Width * 4);
            return Create(r.Width, r.Height, outBuf);
        }

        static void BoxH(byte[] src, byte[] dst, int w, int h, int r)
        {
            int div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += src[row + Math.Clamp(i, 0, w - 1) * 4 + c];
                    for (int x = 0; x < w; x++)
                    {
                        dst[row + x * 4 + c] = (byte)(sum / div);
                        int add = Math.Min(x + r + 1, w - 1), rem = Math.Max(x - r, 0);
                        sum += src[row + add * 4 + c] - src[row + rem * 4 + c];
                    }
                }
            }
        }

        static void BoxV(byte[] src, byte[] dst, int w, int h, int r)
        {
            int div = 2 * r + 1, stride = w * 4;
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int col = x * 4 + c, sum = 0;
                    for (int i = -r; i <= r; i++) sum += src[Math.Clamp(i, 0, h - 1) * stride + col];
                    for (int y = 0; y < h; y++)
                    {
                        dst[y * stride + col] = (byte)(sum / div);
                        int add = Math.Min(y + r + 1, h - 1), rem = Math.Max(y - r, 0);
                        sum += src[add * stride + col] - src[rem * stride + col];
                    }
                }
            }
        }
    }
}
