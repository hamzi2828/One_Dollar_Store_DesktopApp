using System;
using System.Collections.Generic;
using System.IO;
using System.Management;

namespace DollarCoAttendance
{
    // The two fingerprint reader makes this app works with. Each has its own
    // driver, SDK and template format: a finger enrolled on one cannot be
    // matched by the other.
    public enum ReaderBrand
    {
        DigitalPersona,
        SecuGen,
    }

    // One capture from the reader, whichever make it is.
    public class FingerScan
    {
        public byte[] DpSample;   // DigitalPersona: the serialized DPFP.Sample
        public byte[] Image;      // SecuGen: 8-bit grey image, Width x Height
        public int Width;
        public int Height;
        public int Dpi;
        public int Quality;       // SecuGen: 0..100
    }

    // What the reader tells the window. Called on the reader's own thread:
    // nothing in here may throw, and nothing may wait on the UI thread.
    public interface IReaderEvents
    {
        void OnReaderConnect();
        void OnReaderDisconnect();
        // The driver or SDK is missing or broken (null = the problem is gone).
        void OnReaderProblem(string problem);
        void OnFingerTouch();
        void OnFingerGone();
        void OnScan(FingerScan scan);
    }

    // A capture session on the reader. Start/Stop may be called more than once.
    public abstract class FingerReader
    {
        public abstract ReaderBrand Brand { get; }
        public abstract void Start();
        public abstract void Stop();

        // `minQuality`: SecuGen only -- the image quality (0..100) a capture waits for.
        public static FingerReader Create(ReaderBrand brand, IReaderEvents events, bool lowPriority, int minQuality)
        {
            if (brand == ReaderBrand.SecuGen) return new SecuGenReader(events, minQuality);
            return new DigitalPersonaReader(events, lowPriority);
        }
    }

    // Which reader this PC uses, and what to tell staff when it is not working.
    public static class ReaderSetup
    {
        public const string SecuGenVendorId = "VID_1162";

        // config "reader": "secugen" or "digitalpersona" forces one; "auto"
        // (the default) picks a plugged-in SecuGen, then an installed
        // DigitalPersona runtime, then whichever SDK ships beside the exe.
        public static ReaderBrand Choose(AppConfig config)
        {
            string wanted = (config != null ? config.Reader : null) ?? "auto";
            if (wanted == "secugen") return ReaderBrand.SecuGen;
            if (wanted == "digitalpersona") return ReaderBrand.DigitalPersona;

            SecuGenUsb usb = FindSecuGenUsb();
            if (usb.Present) return ReaderBrand.SecuGen;
            if (DigitalPersonaRuntimeInstalled) return ReaderBrand.DigitalPersona;
            if (SecuGenSdkBesideExe) return ReaderBrand.SecuGen;
            return ReaderBrand.DigitalPersona;
        }

        public static bool DigitalPersonaRuntimeInstalled
        {
            get
            {
                try { return File.Exists(Path.Combine(Environment.SystemDirectory, "DPFPApi.dll")); }
                catch { return false; }
            }
        }

        public static bool SecuGenSdkBesideExe
        {
            get
            {
                try { return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sgfplib.dll")); }
                catch { return false; }
            }
        }

        public static string Label(ReaderBrand brand)
        {
            return brand == ReaderBrand.SecuGen ? "SecuGen" : "DigitalPersona";
        }

        public static string EngineMissingBody(ReaderBrand brand, string detail)
        {
            if (brand == ReaderBrand.SecuGen)
                return "Attendance can't be marked. The SecuGen fingerprint files (sgfplib.dll and the other DLLs from the "
                    + "\"FDx SDK Pro for Windows\", 64-bit) are missing next to the app (" + detail + "). Run the "
                    + AppInfo.Name + " installer again.";
            return "Attendance can't be marked. Files the reader needs are missing next to the app ("
                + detail + "). Run the " + AppInfo.Name + " installer again.";
        }

        public static string DriverMissingBody(ReaderBrand brand)
        {
            if (brand == ReaderBrand.SecuGen)
                return "Attendance can't be marked. Install the SecuGen reader driver (\"SecuGen Windows Driver Installer\" "
                    + "from secugen.com/drivers), then unplug the reader and plug it back in.";
            return "Attendance can't be marked. Install the DigitalPersona One Touch for Windows runtime (64-bit), "
                + "restart the PC, then plug the reader in.";
        }

        // ---- the SecuGen reader as Windows sees it (Device Manager) ----

        public struct SecuGenUsb
        {
            public bool Present;
            public bool DriverMissing;   // plugged in, but Windows has no working driver for it
            public int ProblemCode;      // Device Manager's problem code (28 = no driver)
            public string Name;
        }

        public static SecuGenUsb FindSecuGenUsb()
        {
            var found = new SecuGenUsb();
            try
            {
                // WQL: the backslash is escaped once more inside the string.
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\" + SecuGenVendorId + "%'"))
                using (ManagementObjectCollection rows = searcher.Get())
                {
                    foreach (ManagementBaseObject row in rows)
                    {
                        found.Present = true;
                        if (found.Name == null) found.Name = Convert.ToString(row["Name"]);
                        int code = 0;
                        try { code = Convert.ToInt32(row["ConfigManagerErrorCode"]); } catch { }
                        if (code != 0)
                        {
                            found.DriverMissing = true;
                            found.ProblemCode = code;
                        }
                        row.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not ask Windows for USB devices", ex);
            }
            return found;
        }
    }

    // ---------------------------------------------------------------- DigitalPersona

    // The DigitalPersona One Touch capture, forwarded to IReaderEvents.
    public class DigitalPersonaReader : FingerReader, DPFP.Capture.EventHandler
    {
        private readonly IReaderEvents events;
        private readonly bool lowPriority;
        private DPFP.Capture.Capture capture;

        public DigitalPersonaReader(IReaderEvents events, bool lowPriority)
        {
            this.events = events;
            this.lowPriority = lowPriority;
        }

        public override ReaderBrand Brand { get { return ReaderBrand.DigitalPersona; } }

        // Throws when the One Touch runtime (the driver) is missing.
        public override void Start()
        {
            if (capture == null)
            {
                try
                {
                    capture = lowPriority ? new DPFP.Capture.Capture(DPFP.Capture.Priority.Low) : new DPFP.Capture.Capture();
                    if (lowPriority) Log.Info("Fingerprint reader opened with readerPriority \"low\"");
                }
                catch (Exception ex)
                {
                    if (!lowPriority) throw;
                    Log.Error("The reader could not be opened with readerPriority \"low\"; opening it the usual way", ex);
                    capture = new DPFP.Capture.Capture();
                }
                capture.EventHandler = this;
            }
            capture.StartCapture();
        }

        public override void Stop()
        {
            try { if (capture != null) capture.StopCapture(); }
            catch { }
        }

        public void OnComplete(object Capture, string ReaderSerialNumber, DPFP.Sample Sample)
        {
            events.OnScan(new FingerScan { DpSample = Sample != null ? Sample.Bytes : null });
        }

        public void OnFingerGone(object Capture, string ReaderSerialNumber) { events.OnFingerGone(); }
        public void OnFingerTouch(object Capture, string ReaderSerialNumber) { events.OnFingerTouch(); }
        public void OnReaderConnect(object Capture, string ReaderSerialNumber) { events.OnReaderConnect(); }
        public void OnReaderDisconnect(object Capture, string ReaderSerialNumber) { events.OnReaderDisconnect(); }
        public void OnSampleQuality(object Capture, string ReaderSerialNumber, DPFP.Capture.CaptureFeedback CaptureFeedback) { }
    }

    // ---------------------------------------------------------------- matching, either make

    // Turns scans into something to match, fuses enrolment scans and does the
    // 1:N search -- with the engine that belongs to the reader in use.
    public abstract class FingerEngine
    {
        public static FingerEngine For(ReaderBrand brand)
        {
            if (brand == ReaderBrand.SecuGen) return SecuGenEngine.Instance;
            return DigitalPersonaEngine.Instance;
        }

        public abstract ReaderBrand Brand { get; }

        // Good scans of the same finger needed to enrol it.
        public abstract int ScansToEnroll { get; }

        // Null when the engine's DLLs load; otherwise the reason.
        public abstract string SelfTest();

        // A probe to match one scan against the templates, or null with `error`.
        public abstract object Probe(FingerScan scan, out string error);

        // One enrolment scan, or null with `error`.
        public abstract object EnrollPart(FingerScan scan, out string error);

        public abstract EnrollmentResult Fuse(List<object> parts);

        // Templates of the other make (enrolled on a different reader) are skipped.
        public abstract MatchResult Identify(object probe, List<TemplateCandidate> candidates);

        public abstract bool CanUse(TemplateCandidate candidate);

        public MatchResult Identify(object probe, TemplateSet templates)
        {
            return Identify(probe, (templates ?? TemplateSet.Empty).Candidates);
        }

        // Fingers on file that this reader can match.
        public int UsableCount(TemplateSet templates)
        {
            int n = 0;
            foreach (TemplateCandidate c in (templates ?? TemplateSet.Empty).Candidates)
                if (CanUse(c)) n++;
            return n;
        }
    }

    public class DigitalPersonaEngine : FingerEngine
    {
        public static readonly DigitalPersonaEngine Instance = new DigitalPersonaEngine();

        public override ReaderBrand Brand { get { return ReaderBrand.DigitalPersona; } }
        public override int ScansToEnroll { get { return 4; } }

        public override string SelfTest() { return FingerprintMatcher.SelfTest(); }

        public override object Probe(FingerScan scan, out string error)
        {
            return FingerprintMatcher.ExtractVerificationFeatures(scan.DpSample, out error);
        }

        public override object EnrollPart(FingerScan scan, out string error)
        {
            return FingerprintEnroller.ExtractEnrollmentFeatures(scan.DpSample, out error);
        }

        public override EnrollmentResult Fuse(List<object> parts)
        {
            var sets = new List<DPFP.FeatureSet>();
            foreach (object p in parts) if (p is DPFP.FeatureSet) sets.Add((DPFP.FeatureSet)p);
            return FingerprintEnroller.Fuse(sets);
        }

        public override MatchResult Identify(object probe, List<TemplateCandidate> candidates)
        {
            var usable = new List<TemplateCandidate>();
            if (candidates != null) foreach (TemplateCandidate c in candidates) if (CanUse(c)) usable.Add(c);
            return FingerprintMatcher.Identify((DPFP.FeatureSet)probe, usable);
        }

        public override bool CanUse(TemplateCandidate candidate)
        {
            return candidate != null && !SecuGenTemplates.IsOurs(candidate.TemplateBase64);
        }
    }
}
