using System;
using System.Collections.Generic;
using System.Linq;

namespace SnipSnip
{
    /// <summary>
    /// Synthetisches Mausklick-Geräusch: Ein echter Klick sind zwei sehr kurze Schnapp-Geräusche des Mikroschalters
    /// (Drücken laut, Loslassen leiser und heller) – ein harter Anschlag mit hellen Resonanzen des Gehäuses,
    /// nach wenigen Millisekunden verklungen. Jeder Klick variiert minimal, damit es nicht maschinell klingt.
    /// </summary>
    internal static class ClickSound
    {
        public const int Rate = 48000;

        /// <summary>(Frequenz Hz, Abklingzeit s, Lautstärke) – Schalter, Gehäuse, Tisch.</summary>
        static readonly (double F, double Tau, double A)[] Modes =
        {
            (3150, 0.0011, 0.55),
            (4900, 0.0007, 0.38),
            (7200, 0.00045, 0.22),
            (1350, 0.0022, 0.30),
            (240, 0.0035, 0.10)
        };

        /// <summary>Ein einzelnes Schnapp-Geräusch (Mono, 48 kHz).</summary>
        public static float[] Snap(bool release, int seed)
        {
            var rng = new Random(seed * 7919 + (release ? 1 : 0));
            double pitch = (release ? 1.09 : 1.0) * (0.97 + rng.NextDouble() * 0.06);
            double level = (release ? 0.5 : 1.0) * (0.9 + rng.NextDouble() * 0.2);
            int n = Rate * 14 / 1000;
            var s = new float[n];
            double hpPrev = 0, hpOut = 0;
            const double hp = 0.82; // Hochpass ~1,5 kHz für den knackigen Anschlag
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                // Anschlag: kurzer Rauschimpuls
                double noise = (rng.NextDouble() * 2 - 1) * Math.Exp(-t / 0.00035);
                hpOut = hp * (hpOut + noise - hpPrev);
                hpPrev = noise;
                double v = hpOut * 0.9;
                foreach (var (f, tau, a) in Modes)
                    v += Math.Sin(2 * Math.PI * f * pitch * t) * Math.Exp(-t / (release ? tau * 0.8 : tau)) * a;
                // letzte Millisekunde ausblenden (kein Knacken am Ende)
                double fade = Math.Min(1, (n - i) / (Rate * 0.001));
                s[i] = (float)(v * fade);
            }
            float peak = s.Max(Math.Abs);
            float k = (float)(0.85 * level / Math.Max(1e-6, peak));
            for (int i = 0; i < n; i++) s[i] *= k;
            return s;
        }

        /// <summary>Alle Klick-Geräusche einer Aufnahme: (Quellzeit, Samples) für Drücken und Loslassen.</summary>
        public static List<(double T, float[] Samples)> Events(InputData input)
        {
            var list = new List<(double, float[])>();
            var buttons = input.Buttons.OrderBy(b => b.T).ToList();
            int seed = 0;
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                if (!b.Down) continue;
                seed++;
                list.Add((b.T, Snap(false, seed)));
                // passendes Loslassen derselben Taste; fehlt es (oder ist es lang gehalten), kurz danach
                double up = b.T + 0.085;
                for (int j = i + 1; j < buttons.Count; j++)
                {
                    if (buttons[j].Button == b.Button && !buttons[j].Down)
                    {
                        up = Math.Max(b.T + 0.03, buttons[j].T);
                        break;
                    }
                    if (buttons[j].Button == b.Button && buttons[j].Down) break;
                }
                list.Add((up, Snap(true, seed)));
            }
            return list;
        }
    }
}
