using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Text;

namespace Dispatch
{
    internal enum Cue
    {
        /// <summary>A followed host's server appeared: a short radio "dispatch" chirp.</summary>
        Alert,

        Join,

        Copy,

        Pull,

        /// <summary>Something didn't work: a soft, low "denied" tone.</summary>
        Error,

        Follow,
    }

    /// <summary>
    /// A few quiet interface sounds, synthesised at start-up so no audio files are shipped or licensed.
    /// Only meaningful events have a sound; hovering and ordinary clicks never do.
    /// </summary>
    internal sealed class Sounds
    {
        private const int Rate = 22050;
        private readonly Dictionary<Cue, SoundPlayer> players = new Dictionary<Cue, SoundPlayer>
        {
            [Cue.Alert] = Make(Chirp()),
            [Cue.Join] = Make(Concat(Tone(740, 0.06, 0.28), Silence(0.015), Tone(1110, 0.09, 0.28))),
            [Cue.Copy] = Make(Tick(1900, 0.03, 0.22)),
            [Cue.Pull] = Make(Concat(Tick(1300, 0.018, 0.18), Tick(950, 0.022, 0.16))),
            [Cue.Error] = Make(Concat(Tone(520, 0.07, 0.2, harmonic: 0.25), Silence(0.02), Tone(390, 0.12, 0.2, harmonic: 0.25))),
            [Cue.Follow] = Make(Concat(Tick(1250, 0.02, 0.17), Silence(0.01), Tick(1700, 0.03, 0.17))),
        };

        public bool Enabled { get; set; } = true;

        public void Play(Cue cue)
        {
            if (!Enabled || !players.TryGetValue(cue, out var player))
            {
                return;
            }

            try
            {
                player.Play();
            }
            catch (InvalidOperationException)
            {
                // No audio device: stay silent.
            }
        }
        /// <summary>Squelch, two radio beeps, squelch tail.</summary>
        private static float[] Chirp() => Concat(
            Noise(0.035, 0.22),
            Tone(1050, 0.07, 0.38, harmonic: 0.18),
            Silence(0.025),
            Tone(1400, 0.11, 0.38, harmonic: 0.18),
            Noise(0.045, 0.12));

        private static float[] Tone(double hz, double seconds, double peak, double harmonic = 0)
        {
            var n = (int)(seconds * Rate);
            var s = new float[n];
            for (var i = 0; i < n; i++)
            {
                var t = (double)i / Rate;
                var wave = Math.Sin(2 * Math.PI * hz * t) + (harmonic * Math.Sin(2 * Math.PI * hz * 3 * t));
                s[i] = (float)(peak * Envelope(i, n, 0.005, 0.03) * wave / (1 + harmonic));
            }

            return s;
        }

        private static float[] Tick(double hz, double seconds, double peak)
        {
            var n = (int)(seconds * Rate);
            var s = new float[n];
            for (var i = 0; i < n; i++)
            {
                var t = (double)i / Rate;
                s[i] = (float)(peak * Math.Exp(-t / (seconds / 4)) * Math.Sin(2 * Math.PI * hz * t) * Envelope(i, n, 0.001, 0.002));
            }

            return s;
        }

        private static float[] Noise(double seconds, double peak)
        {
            var n = (int)(seconds * Rate);
            var s = new float[n];
            var random = new Random(7);
            double last = 0;
            for (var i = 0; i < n; i++)
            {
                // Gently low-passed white noise, like radio squelch.
                last = (last * 0.55) + ((random.NextDouble() * 2) - 1) * 0.45;
                s[i] = (float)(peak * Envelope(i, n, 0.003, 0.015) * last);
            }

            return s;
        }

        private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

        private static double Envelope(int i, int n, double attack, double release)
        {
            var t = (double)i / Rate;
            var left = (double)(n - i) / Rate;
            return Math.Min(1, Math.Min(t / attack, left / release));
        }

        private static float[] Concat(params float[][] parts)
        {
            var total = 0;
            foreach (var p in parts)
            {
                total += p.Length;
            }

            var all = new float[total];
            var at = 0;
            foreach (var p in parts)
            {
                Array.Copy(p, 0, all, at, p.Length);
                at += p.Length;
            }

            return all;
        }

        /// <summary>16-bit mono PCM WAV in memory.</summary>
        private static SoundPlayer Make(float[] samples)
        {
            var data = new MemoryStream();
            using (var w = new BinaryWriter(data, Encoding.ASCII, leaveOpen: true))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + (samples.Length * 2));
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(Rate);
                w.Write(Rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(samples.Length * 2);
                foreach (var v in samples)
                {
                    w.Write((short)(Math.Max(-1, Math.Min(1, v)) * short.MaxValue));
                }
            }

            data.Position = 0;
            var player = new SoundPlayer(data);
            player.Load();
            return player;
        }
    }
}
