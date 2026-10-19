using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Media;

namespace DollarCoAttendance
{
    // A sound for every scan, so whoever is at the counter knows what happened
    // without reading the screen -- then a few spoken words (Voice).
    //
    //   clock in        sounds\clock-in.mp3
    //   clock out       sounds\clock-out.mp3
    //   late            two level notes (built in)        sounds\late.mp3 / .wav to replace
    //   not recognised  three short low pulses (built in) sounds\not-recognised.mp3 / .wav
    //   problem         two falling notes (built in)      sounds\problem.mp3 / .wav
    //
    // The chimes play through WPF's MediaPlayer (MP3, Media Foundation; no
    // installs). MediaPlayer belongs to the UI thread, so every call marshals
    // there first. Sound is a nicety on top of a punch that is already
    // recorded: every failure here is swallowed.
    public static class Sounds
    {
        private static Control uiContext;
        private static Cue clockIn, clockOut, late, notRecognised, problem;

        // Settings: sound on / off.
        public static volatile bool Enabled = true;

        public static void Initialize(Control owner, bool soundOn, bool voiceOn)
        {
            uiContext = owner;
            Enabled = soundOn;
            Voice.Enabled = voiceOn;
            clockIn = new Cue(Load("clock-in.mp3"), null);
            clockOut = new Cue(Load("clock-out.mp3"), null);
            late = new Cue(LoadCustom("late"), Tone.Tune(new[] { 660, 660 }, 160, 70));
            notRecognised = new Cue(LoadCustom("not-recognised"), Tone.Tune(new[] { 196, 196, 196 }, 80, 60));
            problem = new Cue(LoadCustom("problem"), Tone.Tune(new[] { 330, 220 }, 200, 40));
            Voice.Warm();
        }

        public static void PlayIn(string speech) { Play(clockIn, speech, Voice.Interrupt()); }
        public static void PlayOut(string speech) { Play(clockOut, speech, Voice.Interrupt()); }
        public static void PlayLate(string speech) { Play(late, speech, Voice.Interrupt()); }
        public static void PlayNotRecognised(string speech) { Play(notRecognised, speech, Voice.Interrupt()); }
        public static void PlayProblem(string speech) { Play(problem, speech, Voice.Interrupt()); }

        private static MediaPlayer LoadCustom(string name)
        {
            return Load(name + ".mp3") ?? Load(name + ".wav");
        }

        private static MediaPlayer Load(string fileName)
        {
            try
            {
                string path = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sounds"), fileName);
                if (!File.Exists(path)) return null;
                var player = new MediaPlayer();
                player.Open(new Uri(path));
                return player;
            }
            catch
            {
                return null;
            }
        }

        private static void Play(Cue cue, string speech, int generation)
        {
            if (!Enabled || cue == null)
            {
                Voice.Say(speech, generation);
                return;
            }
            if (cue.File != null)
            {
                PlayFile(cue, speech, generation);
                return;
            }
            if (cue.BuiltIn != null)
            {
                // Off the calling thread: PlaySync lets the words wait for the
                // tone without holding the UI or the reader's thread.
                byte[] wave = cue.BuiltIn;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        using (var stream = new MemoryStream(wave))
                        using (var player = new System.Media.SoundPlayer(stream))
                            player.PlaySync();
                    }
                    catch { }
                    Voice.Say(speech, generation);
                });
                return;
            }
            Voice.Say(speech, generation);
        }

        private static void PlayFile(Cue cue, string speech, int generation)
        {
            try
            {
                if (uiContext != null && !uiContext.IsDisposed && uiContext.InvokeRequired)
                {
                    uiContext.BeginInvoke(new Action<Cue, string, int>(PlayFile), cue, speech, generation);
                    return;
                }
                cue.PendingSpeech = speech;
                cue.PendingGeneration = generation;
                cue.File.Position = TimeSpan.Zero;
                cue.File.Play();
            }
            catch
            {
                Voice.Say(speech, generation);
            }
        }

        public static void Shutdown()
        {
            foreach (Cue cue in new[] { clockIn, clockOut, late, notRecognised, problem })
            {
                try { if (cue != null && cue.File != null) cue.File.Close(); }
                catch { }
            }
            Voice.Shutdown();
        }

        // A file from sounds\ when there is one, otherwise the built-in tone
        // (none for the chimes).
        private sealed class Cue
        {
            public readonly MediaPlayer File;
            public readonly byte[] BuiltIn;
            public string PendingSpeech;
            public int PendingGeneration;

            public Cue(MediaPlayer file, byte[] builtIn)
            {
                File = file;
                BuiltIn = builtIn;
                if (file == null) return;
                file.MediaEnded += delegate { SayPending(); };
                file.MediaFailed += delegate { SayPending(); };
            }

            private void SayPending()
            {
                string speech = PendingSpeech;
                PendingSpeech = null;
                Voice.Say(speech, PendingGeneration);
            }
        }
    }

    // The built-in tones: a few sine notes as an in-memory WAV, so there is
    // nothing to ship and nothing to go missing.
    internal static class Tone
    {
        private const int SampleRate = 22050;
        private const double Volume = 0.35;
        private const int FadeMs = 8;

        public static byte[] Tune(int[] frequencies, int noteMs, int gapMs)
        {
            int noteSamples = SampleRate * noteMs / 1000;
            int gapSamples = SampleRate * gapMs / 1000;
            int fadeSamples = SampleRate * FadeMs / 1000;
            int total = frequencies.Length * noteSamples + (frequencies.Length - 1) * gapSamples;

            var samples = new short[total];
            int at = 0;
            for (int n = 0; n < frequencies.Length; n++)
            {
                for (int i = 0; i < noteSamples; i++)
                {
                    double envelope = Math.Min(1.0, Math.Min(i, noteSamples - 1 - i) / (double)fadeSamples);
                    double value = Math.Sin(2 * Math.PI * frequencies[n] * i / SampleRate) * envelope * Volume;
                    samples[at++] = (short)(value * short.MaxValue);
                }
                if (n < frequencies.Length - 1) at += gapSamples;
            }
            return Wave(samples);
        }

        // 16-bit mono PCM in a RIFF container: what SoundPlayer plays.
        internal static byte[] Wave(short[] samples)
        {
            int dataBytes = samples.Length * 2;
            using (var stream = new MemoryStream(44 + dataBytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(SampleRate);
                writer.Write(SampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                writer.Write(dataBytes);
                foreach (short sample in samples) writer.Write(sample);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
