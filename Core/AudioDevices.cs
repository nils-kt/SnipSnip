using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace SnipSnip
{
    /// <summary>Mikrofone (WASAPI-Aufnahmegeräte) auflisten und auswählen.</summary>
    internal static class AudioDevices
    {
        /// <summary>Für die README-Bilder: neutrale Geräteliste statt der echten Geräte.</summary>
        internal static List<(string Id, string Name)>? DocsOverride;

        /// <summary>Aktive Mikrofone: Geräte-ID und Anzeigename.</summary>
        public static List<(string Id, string Name)> Microphones()
        {
            if (DocsOverride != null) return DocsOverride;
            try
            {
                using var en = new MMDeviceEnumerator();
                return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .Select(d => (d.ID, d.FriendlyName))
                    .OrderBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch { return new List<(string, string)>(); }
        }

        /// <summary>Name des Windows-Standardmikrofons (oder null, wenn keins da ist).</summary>
        public static string? DefaultMicrophoneName()
        {
            if (DocsOverride != null) return DocsOverride.FirstOrDefault().Name;
            try
            {
                using var en = new MMDeviceEnumerator();
                return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).FriendlyName;
            }
            catch { return null; }
        }

        /// <summary>Das gewählte Mikrofon – fehlt es (abgesteckt), das Windows-Standardmikrofon.</summary>
        public static MMDevice Microphone(string? id)
        {
            var en = new MMDeviceEnumerator();
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    var d = en.GetDevice(id);
                    if (d.State == DeviceState.Active) return d;
                }
                catch { /* Gerät nicht mehr vorhanden */ }
            }
            return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }

        /// <summary>Anzeigename des gewählten Mikrofons für Tooltips.</summary>
        public static string Describe(string? id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                var hit = Microphones().FirstOrDefault(m => m.Id == id);
                if (hit.Id != null) return hit.Name;
            }
            return "Windows-Standard" + (DefaultMicrophoneName() is string n ? $" ({n})" : "");
        }
    }
}
