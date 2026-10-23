using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SnipSnip
{
    /// <summary>Globales Tastenkürzel über RegisterHotKey auf einem Message-Only-Fenster.</summary>
    internal sealed class HotkeyManager : IDisposable
    {
        const int Id = 0xB00F;
        readonly HwndSource source;
        bool registered;

        public event Action? Pressed;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("SnipSnipHotkeySink")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                ParentWindow = new IntPtr(-3) // HWND_MESSAGE
            };
            source = new HwndSource(p);
            source.AddHook(Hook);
        }

        public bool Register(int vk, int mods)
        {
            Unregister();
            registered = Native.RegisterHotKey(source.Handle, Id, (uint)mods | Native.MOD_NOREPEAT, (uint)vk);
            return registered;
        }

        public void Unregister()
        {
            if (registered) Native.UnregisterHotKey(source.Handle, Id);
            registered = false;
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
            {
                handled = true;
                Pressed?.Invoke();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
            source.Dispose();
        }
    }

    internal static class HotkeyText
    {
        public static string Describe(int vk, int mods)
        {
            var parts = new List<string>();
            if ((mods & (int)Native.MOD_CONTROL) != 0) parts.Add("Strg");
            if ((mods & (int)Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & (int)Native.MOD_SHIFT) != 0) parts.Add("Umschalt");
            if ((mods & (int)Native.MOD_WIN) != 0) parts.Add("Win");

            var key = KeyInterop.KeyFromVirtualKey(vk);
            string name = key switch
            {
                Key.Pause => "Pause",
                Key.Snapshot => "Druck",
                Key.Scroll => "Rollen",
                Key.Insert => "Einfg",
                Key.Delete => "Entf",
                Key.Home => "Pos1",
                Key.End => "Ende",
                Key.PageUp => "Bild ↑",
                Key.PageDown => "Bild ↓",
                Key.Space => "Leertaste",
                >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
                _ => key.ToString()
            };
            parts.Add(name);
            return string.Join(" + ", parts);
        }

        public static int ModsFromKeyboard(ModifierKeys m)
        {
            int r = 0;
            if (m.HasFlag(ModifierKeys.Control)) r |= (int)Native.MOD_CONTROL;
            if (m.HasFlag(ModifierKeys.Alt)) r |= (int)Native.MOD_ALT;
            if (m.HasFlag(ModifierKeys.Shift)) r |= (int)Native.MOD_SHIFT;
            if (m.HasFlag(ModifierKeys.Windows)) r |= (int)Native.MOD_WIN;
            return r;
        }
    }

    internal static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return;
                if (enabled) key.SetValue("SnipSnip", $"\"{Environment.ProcessPath}\" --tray");
                else key.DeleteValue("SnipSnip", false);
            }
            catch { }
        }
    }
}
