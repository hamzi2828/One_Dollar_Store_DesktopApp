using System;
using System.Speech.Synthesis;
using System.Threading;

namespace DollarCoAttendance
{
    // A few words after the sound -- "Ali, clocked in, 12 minutes late" -- so the
    // person at the reader hears the result without reading the screen.
    //
    // Windows' own speech engine (System.Speech). Where it is missing or broken
    // the first failure switches it off for the session: the sound has already
    // played, and a punch must never become an error because it could not be
    // spoken. Nothing here runs on the UI thread.
    public static class Voice
    {
        private static readonly object gate = new object();
        private static SpeechSynthesizer synth;
        private static volatile bool unavailable;
        // Bumped by every scan: words are only said while their scan is still
        // the newest, so three people in a row never hear the first one's name.
        private static int latest;

        // Settings: voice on / off.
        public static volatile bool Enabled = true;

        public static void Warm()
        {
            if (!Enabled) return;
            ThreadPool.QueueUserWorkItem(delegate { lock (gate) { Ensure(); } });
        }

        public static int Interrupt()
        {
            int generation = Interlocked.Increment(ref latest);
            if (synth != null)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    lock (gate)
                    {
                        try { if (synth != null) synth.SpeakAsyncCancelAll(); }
                        catch (Exception ex) { Fail(ex); }
                    }
                });
            }
            return generation;
        }

        public static void Say(string text, int generation)
        {
            if (!Enabled || unavailable || string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (gate)
                {
                    if (generation != Thread.VolatileRead(ref latest)) return;
                    if (!Ensure()) return;
                    try
                    {
                        synth.SpeakAsyncCancelAll();
                        synth.SpeakAsync(text.Trim());
                    }
                    catch (Exception ex)
                    {
                        Fail(ex);
                    }
                }
            });
        }

        public static void Shutdown()
        {
            Interlocked.Increment(ref latest);
            lock (gate)
            {
                try
                {
                    if (synth != null)
                    {
                        synth.SpeakAsyncCancelAll();
                        synth.Dispose();
                    }
                }
                catch { }
                synth = null;
            }
        }

        // Under the lock.
        private static bool Ensure()
        {
            if (synth != null) return true;
            if (unavailable) return false;
            try
            {
                var engine = new SpeechSynthesizer();
                engine.SetOutputToDefaultAudioDevice();
                engine.Rate = 0;
                synth = engine;
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }

        // Under the lock.
        private static void Fail(Exception ex)
        {
            if (!unavailable) Log.Info("Speech unavailable, the counter will only play sounds: " + ex.Message);
            unavailable = true;
            try { if (synth != null) synth.Dispose(); } catch { }
            synth = null;
        }
    }
}
