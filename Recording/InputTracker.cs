using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Input;
using SD = System.Drawing;

namespace SnipSnip
{
    /// <summary>Form eines aufgezeichneten Mauszeigers.</summary>
    public sealed class CursorShape
    {
        public int Id { get; set; }
        /// <summary>arrow, ibeam, hand, wait, cross, sizeall, sizewe, sizens, sizenwse, sizenesw, no, appstarting, custom</summary>
        public string Kind { get; set; } = "custom";
        public int HotX { get; set; }
        public int HotY { get; set; }
        public string File { get; set; } = "";
    }

    /// <summary>
    /// Zeichnet Mausbahn, Cursorformen, Klicks und Tasten getrennt vom Video auf (events.jsonl).
    /// Zeitstempel kommen von der gemeinsamen Aufnahme-Uhr; Pausen werden ausgelassen.
    /// Bei normalem Tippen wird nur der Zeitpunkt gespeichert, nicht die Taste (Datenschutz) –
    /// außer die Einstellung "alle Tasten anzeigen" ist aktiv.
    /// </summary>
    internal sealed class InputTracker
    {
        readonly RecordingClock clock;
        readonly int ox, oy;
        readonly string dir;
        readonly bool allKeys;
        readonly StreamWriter events;
        readonly ConcurrentQueue<string> keyQueue = new();
        readonly Dictionary<IntPtr, int> shapeIds = new();
        readonly List<CursorShape> shapes = new();
        readonly Dictionary<IntPtr, string> knownKinds;
        Thread? poller, hookThread;
        uint hookThreadId;
        volatile bool running;
        IntPtr hook, mouseHook;
        Hooks.LowLevelProc? hookProc, mouseProc; // gegen GC schützen
        readonly int stopVk = App.Settings.HotkeyVk;

        public InputTracker(RecordingClock clock, int regionX, int regionY, string projectDir, bool allKeys)
        {
            this.clock = clock;
            ox = regionX;
            oy = regionY;
            dir = projectDir;
            this.allKeys = allKeys;
            Directory.CreateDirectory(Path.Combine(dir, "cursors"));
            events = new StreamWriter(Path.Combine(dir, "events.jsonl"), false, new UTF8Encoding(false));
            knownKinds = new Dictionary<IntPtr, string>
            {
                [Hooks.LoadCursor(IntPtr.Zero, 32512)] = "arrow",
                [Hooks.LoadCursor(IntPtr.Zero, 32513)] = "ibeam",
                [Hooks.LoadCursor(IntPtr.Zero, 32514)] = "wait",
                [Hooks.LoadCursor(IntPtr.Zero, 32515)] = "cross",
                [Hooks.LoadCursor(IntPtr.Zero, 32642)] = "sizenwse",
                [Hooks.LoadCursor(IntPtr.Zero, 32643)] = "sizenesw",
                [Hooks.LoadCursor(IntPtr.Zero, 32644)] = "sizewe",
                [Hooks.LoadCursor(IntPtr.Zero, 32645)] = "sizens",
                [Hooks.LoadCursor(IntPtr.Zero, 32646)] = "sizeall",
                [Hooks.LoadCursor(IntPtr.Zero, 32648)] = "no",
                [Hooks.LoadCursor(IntPtr.Zero, 32649)] = "hand",
                [Hooks.LoadCursor(IntPtr.Zero, 32650)] = "appstarting"
            };
        }

        public void Start()
        {
            running = true;
            poller = new Thread(Poll) { IsBackground = true, Name = "SnipSnip.Input", Priority = ThreadPriority.AboveNormal };
            poller.Start();
            hookThread = new Thread(HookLoop) { IsBackground = true, Name = "SnipSnip.KeyHook" };
            hookThread.Start();
        }

        public void Stop()
        {
            running = false;
            if (hookThreadId != 0) Hooks.PostThreadMessage(hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            poller?.Join(1000);
            hookThread?.Join(1000);
            lock (events)
            {
                DrainKeys();
                events.Flush();
                events.Dispose();
            }
            File.WriteAllText(Path.Combine(dir, "cursors.json"), JsonSerializer.Serialize(shapes, AppSettings.Json));
        }

        static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ Maus (120 Hz)

        void Poll()
        {
            Win.timeBeginPeriod(1);
            int lastX = int.MinValue, lastY = int.MinValue, lastShape = -1;

            var flush = Stopwatch.StartNew();
            double lastWrite = -1;
            try
            {
                while (running)
                {
                    Thread.Sleep(8);
                    if (clock.IsPaused) continue;
                    double t = clock.Elapsed.TotalSeconds;

                    var ci = new Win.CURSORINFO { cbSize = Marshal.SizeOf<Win.CURSORINFO>() };
                    if (Win.GetCursorInfo(ref ci))
                    {
                        int x = ci.ptScreenPos.X - ox, y = ci.ptScreenPos.Y - oy;
                        int shape = (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero ? -1 : ShapeFor(ci.hCursor);
                        if (x != lastX || y != lastY || shape != lastShape || t - lastWrite > 0.25)
                        {
                            lock (events) events.WriteLine($"{{\"t\":{F(t)},\"x\":{x},\"y\":{y},\"c\":{shape}}}");
                            lastX = x; lastY = y; lastShape = shape; lastWrite = t;
                        }
                    }


                    if (flush.ElapsedMilliseconds > 2000)
                    {
                        lock (events) { DrainKeys(); events.Flush(); }
                        flush.Restart();
                    }
                    else if (!keyQueue.IsEmpty)
                    {
                        lock (events) DrainKeys();
                    }
                }
            }
            finally
            {
                Win.timeEndPeriod(1);
            }
        }

        void DrainKeys()
        {
            while (keyQueue.TryDequeue(out var line)) events.WriteLine(line);
        }

        int ShapeFor(IntPtr h)
        {
            if (shapeIds.TryGetValue(h, out int id)) return id;
            id = shapes.Count;
            shapeIds[h] = id;
            var shape = new CursorShape { Id = id, Kind = knownKinds.TryGetValue(h, out var k) ? k : "custom", File = $"cursors/{id}.png" };
            try { SaveCursorImage(h, Path.Combine(dir, shape.File), shape); }
            catch (Exception ex) { Debug.WriteLine("Cursorbild: " + ex.Message); }
            shapes.Add(shape);
            return id;
        }

        /// <summary>Speichert den Cursor als PNG mit Alpha (Zeichnen auf Schwarz und Weiß → Alpha herleiten).</summary>
        static void SaveCursorImage(IntPtr hCursor, string path, CursorShape shape)
        {
            if (Win.GetIconInfo(hCursor, out var ii))
            {
                shape.HotX = ii.xHotspot;
                shape.HotY = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) Gdi.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Gdi.DeleteObject(ii.hbmColor);
            }
            const int S = 128;
            using var black = Render(hCursor, S, SD.Color.Black);
            using var white = Render(hCursor, S, SD.Color.White);
            using var result = new SD.Bitmap(S, S, SD.Imaging.PixelFormat.Format32bppArgb);
            int maxX = 0, maxY = 0;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    var b = black.GetPixel(x, y);
                    var w = white.GetPixel(x, y);
                    int a = 255 - Math.Clamp((w.R - b.R + w.G - b.G + w.B - b.B) / 3, 0, 255);
                    if (a <= 0) continue;
                    int r = Math.Clamp(b.R * 255 / a, 0, 255), g = Math.Clamp(b.G * 255 / a, 0, 255), bl = Math.Clamp(b.B * 255 / a, 0, 255);
                    result.SetPixel(x, y, SD.Color.FromArgb(a, r, g, bl));
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
            int cw = Math.Max(maxX + 1, shape.HotX + 1), ch = Math.Max(maxY + 1, shape.HotY + 1);
            using var crop = result.Clone(new SD.Rectangle(0, 0, Math.Min(S, cw), Math.Min(S, ch)), SD.Imaging.PixelFormat.Format32bppArgb);
            crop.Save(path, SD.Imaging.ImageFormat.Png);
        }

        static SD.Bitmap Render(IntPtr hCursor, int size, SD.Color bg)
        {
            var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppRgb);
            using var g = SD.Graphics.FromImage(bmp);
            g.Clear(bg);
            IntPtr hdc = g.GetHdc();
            try { Win.DrawIconEx(hdc, 0, 0, hCursor, 0, 0, 0, IntPtr.Zero, 3); }
            finally { g.ReleaseHdc(hdc); }
            return bmp;
        }

        // ------------------------------------------------------------------ Tastatur (Low-Level-Hook)

        void HookLoop()
        {
            hookThreadId = Hooks.GetCurrentThreadId();
            hookProc = KeyProc;
            hook = Hooks.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, hookProc, Hooks.GetModuleHandle(null), 0);
            mouseProc = MouseProc;
            mouseHook = Hooks.SetWindowsHookEx(14 /* WH_MOUSE_LL */, mouseProc, Hooks.GetModuleHandle(null), 0);
            while (running && Hooks.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Hooks.TranslateMessage(ref msg);
                Hooks.DispatchMessage(ref msg);
            }
            if (hook != IntPtr.Zero) Hooks.UnhookWindowsHookEx(hook);
            if (mouseHook != IntPtr.Zero) Hooks.UnhookWindowsHookEx(mouseHook);
        }

        /// <summary>Klicks über einen Low-Level-Hook – erfasst auch sehr kurze Klicks zuverlässig.</summary>
        IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !clock.IsPaused)
            {
                int msg = wParam.ToInt32();
                (int Button, bool Down)? ev = msg switch
                {
                    0x0201 => (0, true), 0x0202 => (0, false),   // links
                    0x0204 => (1, true), 0x0205 => (1, false),   // rechts
                    0x0207 => (2, true), 0x0208 => (2, false),   // Mitte
                    _ => null
                };
                if (ev != null)
                    keyQueue.Enqueue($"{{\"t\":{F(clock.Elapsed.TotalSeconds)},\"{(ev.Value.Down ? "d" : "u")}\":{ev.Value.Button}}}");
            }
            return Hooks.CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
        {
            int msg = wParam.ToInt32();
            if (code >= 0 && (msg == 0x0100 || msg == 0x0104) && !clock.IsPaused) // WM_KEYDOWN / WM_SYSKEYDOWN
            {
                int vk = Marshal.ReadInt32(lParam);
                string? text = Describe(vk);
                if (text != null)
                    keyQueue.Enqueue($"{{\"t\":{F(clock.Elapsed.TotalSeconds)},\"k\":{JsonSerializer.Serialize(text)}}}");
            }
            return Hooks.CallNextHookEx(hook, code, wParam, lParam);
        }

        /// <summary>Text für die Einblendung – "" = normales Tippen (ohne Taste), null = ignorieren.</summary>
        string? Describe(int vk)
        {
            if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x14) return null;
            if (vk == stopVk || vk == 0x13) return null; // Aufnahme-Kürzel / Pause
            bool ctrl = (Win.GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool alt = (Win.GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool shift = (Win.GetAsyncKeyState(0x10) & 0x8000) != 0;
            bool win = (Win.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Win.GetAsyncKeyState(0x5C) & 0x8000) != 0;
            bool special = vk is 0x1B or 0x09 or 0x0D or 0x08 or 0x2E or (>= 0x70 and <= 0x87); // Esc, Tab, Enter, Rück, Entf, F-Tasten
            if (!ctrl && !alt && !win && !(allKeys || special)) return "";

            int mods = (ctrl ? (int)Native.MOD_CONTROL : 0) | (alt ? (int)Native.MOD_ALT : 0) |
                       (shift && (ctrl || alt || win) ? (int)Native.MOD_SHIFT : 0) | (win ? (int)Native.MOD_WIN : 0);
            var key = KeyInterop.KeyFromVirtualKey(vk);
            string name = key switch
            {
                Key.Enter => "Enter",
                Key.Escape => "Esc",
                Key.Back => "Rück",
                Key.Tab => "Tab",
                Key.Space => "Leertaste",
                Key.Delete => "Entf",
                Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
                Key.OemPlus => "+", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".",
                _ => null!
            };
            if (name == null) return HotkeyText.Describe(vk, mods);
            return mods == 0 ? name : HotkeyText.Describe(vk, mods).Replace(key.ToString(), name);
        }
    }

    internal static class Hooks
    {
        public delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr hInstance, int id);
    }
}
