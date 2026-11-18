using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DollarCoAttendance
{
    // SecuGen readers (Hamster Pro 20 / HU20 and the rest of the U20 family),
    // through the FDx SDK Pro for Windows' C library, sgfplib.dll (64-bit),
    // which sits beside the exe with the SDK's other DLLs. The reader's driver
    // is SecuGen's own (secugen.com/drivers).
    //
    // Called straight from C (no SecuGen .NET wrapper), so the app builds
    // without the SDK; without its DLLs the counter says what is missing.
    internal static class SgNative
    {
        private const string Dll = "sgfplib.dll";

        public const uint ErrorNone = 0;
        public const uint ErrorFunctionFailed = 2;
        public const uint ErrorDllLoadFailed = 5;
        public const uint ErrorDllLoadFailedDriver = 6;
        public const uint ErrorDllLoadFailedAlgo = 7;
        public const uint ErrorSysLoadFailed = 51;
        public const uint ErrorInitializeFailed = 52;
        public const uint ErrorLineDropped = 53;
        public const uint ErrorTimeOut = 54;
        public const uint ErrorDeviceNotFound = 55;
        public const uint ErrorDriverLoadFailed = 56;
        public const uint ErrorWrongImage = 57;
        public const uint ErrorDeviceAlreadyOpen = 59;
        public const uint ErrorFeatureNumber = 101;   // too little of the finger for a template
        public const uint ErrorExtractFail = 105;

        public const uint DevAuto = 0xFF;
        public const uint DevFdu05 = 6;               // U20: Hamster Pro 20 (HU20)
        public const uint UsbAutoDetect = 0x3BD;
        public const ushort TemplateFormatSg400 = 0x0200;

        // SGFDxSecurityLevel. SecuGen's published false-accept rates per comparison.
        public const uint SlNormal = 5;               // 1 in 100,000
        public const uint SlAboveNormal = 6;          // 1 in 500,000
        public const uint SlHigh = 7;                 // 1 in 1,000,000

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInfo
        {
            public uint DeviceID;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] DeviceSN;
            public uint ComPort;
            public uint ComSpeed;
            public uint ImageWidth;
            public uint ImageHeight;
            public uint Contrast;
            public uint Brightness;
            public uint Gain;
            public uint ImageDPI;
            public uint FWVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FingerInfo
        {
            public ushort FingerNumber;
            public ushort ViewNumber;
            public ushort ImpressionType;
            public ushort ImageQuality;
        }

        [DllImport(Dll)] public static extern uint SGFPM_Create(out IntPtr hFpm);
        [DllImport(Dll)] public static extern uint SGFPM_Terminate(IntPtr hFpm);
        [DllImport(Dll)] public static extern uint SGFPM_Init(IntPtr hFpm, uint devName);
        [DllImport(Dll)] public static extern uint SGFPM_InitEx(IntPtr hFpm, uint width, uint height, uint dpi);
        [DllImport(Dll)] public static extern uint SGFPM_SetTemplateFormat(IntPtr hFpm, ushort format);
        [DllImport(Dll)] public static extern uint SGFPM_OpenDevice(IntPtr hFpm, uint devId);
        [DllImport(Dll)] public static extern uint SGFPM_CloseDevice(IntPtr hFpm);
        [DllImport(Dll)] public static extern uint SGFPM_GetDeviceInfo(IntPtr hFpm, ref DeviceInfo info);
        [DllImport(Dll)] public static extern uint SGFPM_GetImage(IntPtr hFpm, byte[] buffer);
        [DllImport(Dll)] public static extern uint SGFPM_GetImageEx(IntPtr hFpm, byte[] buffer, uint timeMs, IntPtr dispWnd, uint quality);
        [DllImport(Dll)] public static extern uint SGFPM_GetImageQuality(IntPtr hFpm, uint width, uint height, byte[] image, out uint quality);
        [DllImport(Dll)] public static extern uint SGFPM_GetMaxTemplateSize(IntPtr hFpm, out uint size);
        [DllImport(Dll)] public static extern uint SGFPM_CreateTemplate(IntPtr hFpm, ref FingerInfo info, byte[] image, byte[] template);
        [DllImport(Dll)] public static extern uint SGFPM_GetTemplateSize(IntPtr hFpm, byte[] template, out uint size);
        [DllImport(Dll)] public static extern uint SGFPM_MatchTemplate(IntPtr hFpm, byte[] template1, byte[] template2, uint securityLevel, out int matched);
        [DllImport(Dll)] public static extern uint SGFPM_GetMatchingScore(IntPtr hFpm, byte[] template1, byte[] template2, out uint score);

        public static bool IsDriverError(uint rc)
        {
            return rc == ErrorDllLoadFailed || rc == ErrorDllLoadFailedDriver || rc == ErrorSysLoadFailed || rc == ErrorDriverLoadFailed;
        }
    }

    // ---------------------------------------------------------------- templates on the server

    // What a SecuGen finger looks like on the server: several SG400 templates
    // of the same finger (one per enrolment scan) in one blob, so a scan is
    // matched against each of them.
    //
    //   "SGFP"  version(1)  count(n)  n x [ length (2 bytes, little-endian)  template ]
    //
    // The "SGFP" start is how both engines tell a SecuGen finger from a
    // DigitalPersona one on the same server.
    public static class SecuGenTemplates
    {
        private static readonly byte[] Magic = { (byte)'S', (byte)'G', (byte)'F', (byte)'P' };
        private const byte Version = 1;

        // Base64 of "SGFP" + any next byte always starts like this.
        private const string Base64Start = "U0dGU";

        public static bool IsOurs(string base64)
        {
            return base64 != null && base64.StartsWith(Base64Start, StringComparison.Ordinal);
        }

        public static byte[] Pack(List<byte[]> templates)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(Magic, 0, Magic.Length);
                ms.WriteByte(Version);
                ms.WriteByte((byte)templates.Count);
                foreach (byte[] t in templates)
                {
                    ms.WriteByte((byte)(t.Length & 0xFF));
                    ms.WriteByte((byte)(t.Length >> 8));
                    ms.Write(t, 0, t.Length);
                }
                return ms.ToArray();
            }
        }

        // Null when it is not a well-formed SecuGen blob.
        public static List<byte[]> Unpack(string base64)
        {
            if (!IsOurs(base64)) return null;
            byte[] data;
            try { data = Convert.FromBase64String(base64); }
            catch { return null; }
            if (data.Length < 6 || data[4] != Version) return null;
            for (int i = 0; i < Magic.Length; i++) if (data[i] != Magic[i]) return null;

            int count = data[5], at = 6;
            var list = new List<byte[]>();
            for (int i = 0; i < count; i++)
            {
                if (at + 2 > data.Length) return null;
                int length = data[at] | (data[at + 1] << 8);
                at += 2;
                if (length <= 0 || at + length > data.Length) return null;
                var t = new byte[length];
                Buffer.BlockCopy(data, at, t, 0, length);
                list.Add(t);
                at += length;
            }
            return list.Count > 0 ? list : null;
        }
    }

    // One SecuGen scan turned into a template.
    public class SecuGenProbe
    {
        public byte[] Template;
        public int Quality;
    }

    // ---------------------------------------------------------------- matching

    public class SecuGenEngine : FingerEngine
    {
        public static readonly SecuGenEngine Instance = new SecuGenEngine();

        // Image quality (SecuGen's 0..100) a scan needs.
        public const int VerifyQuality = 40;
        public const int EnrollQuality = 50;

        private readonly object gate = new object();
        private IntPtr handle = IntPtr.Zero;   // algorithm only (no device), see Ready
        private int initWidth, initHeight, initDpi;
        private int maxTemplateSize = 400;

        private SecuGenEngine() { }

        public override ReaderBrand Brand { get { return ReaderBrand.SecuGen; } }
        public override int ScansToEnroll { get { return 3; } }

        public override string SelfTest()
        {
            try
            {
                lock (gate)
                {
                    uint rc = Ready(300, 400, 500);
                    return rc == SgNative.ErrorNone ? null : "the SecuGen engine would not start (error " + rc + ")";
                }
            }
            catch (DllNotFoundException)
            {
                return "sgfplib.dll not found";
            }
            catch (BadImageFormatException)
            {
                return "sgfplib.dll is the 32-bit version; the 64-bit one (bin\\x64 in the SDK) is needed";
            }
            catch (EntryPointNotFoundException ex)
            {
                return "sgfplib.dll is not the FDx SDK Pro library (" + ex.Message + ")";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        // The algorithm handle, set up for images of this size. Under `gate`.
        private uint Ready(int width, int height, int dpi)
        {
            if (handle != IntPtr.Zero && width == initWidth && height == initHeight && dpi == initDpi) return SgNative.ErrorNone;
            if (handle != IntPtr.Zero)
            {
                SgNative.SGFPM_Terminate(handle);
                handle = IntPtr.Zero;
            }
            IntPtr h;
            uint rc = SgNative.SGFPM_Create(out h);
            if (rc != SgNative.ErrorNone) return rc;
            // InitEx (no device) needs a paid SecuGen licence in SDK 4.x and
            // answers "no longer supported" without one; Init for the reader's
            // device type is free and sets the same image size.
            rc = SgNative.SGFPM_InitEx(h, (uint)width, (uint)height, (uint)dpi);
            if (rc != SgNative.ErrorNone) rc = SgNative.SGFPM_Init(h, SgNative.DevAuto);
            if (rc != SgNative.ErrorNone) rc = SgNative.SGFPM_Init(h, SgNative.DevFdu05);
            if (rc == SgNative.ErrorNone) rc = SgNative.SGFPM_SetTemplateFormat(h, SgNative.TemplateFormatSg400);
            if (rc != SgNative.ErrorNone)
            {
                SgNative.SGFPM_Terminate(h);
                return rc;
            }
            uint size;
            if (SgNative.SGFPM_GetMaxTemplateSize(h, out size) == SgNative.ErrorNone && size > 0 && size < 65536) maxTemplateSize = (int)size;
            handle = h;
            initWidth = width;
            initHeight = height;
            initDpi = dpi;
            return SgNative.ErrorNone;
        }

        private byte[] CreateTemplate(FingerScan scan, out string error)
        {
            error = null;
            if (scan == null || scan.Image == null || scan.Width <= 0 || scan.Height <= 0 || scan.Image.Length < scan.Width * scan.Height)
            {
                error = "Not a scanner image.";
                return null;
            }
            lock (gate)
            {
                uint rc = Ready(scan.Width, scan.Height, scan.Dpi > 0 ? scan.Dpi : 500);
                if (rc != SgNative.ErrorNone)
                {
                    error = "The fingerprint engine would not start (error " + rc + ").";
                    return null;
                }
                var info = new SgNative.FingerInfo { ImageQuality = (ushort)Math.Max(0, Math.Min(100, scan.Quality)) };
                var buffer = new byte[maxTemplateSize];
                rc = SgNative.SGFPM_CreateTemplate(handle, ref info, scan.Image, buffer);
                if (rc == SgNative.ErrorFeatureNumber || rc == SgNative.ErrorExtractFail)
                {
                    error = "Not enough of the fingerprint was seen. Press the finger flat and firmly, then try again.";
                    return null;
                }
                if (rc != SgNative.ErrorNone)
                {
                    error = "The scan could not be read (error " + rc + "). Please try again.";
                    return null;
                }
                uint size;
                if (SgNative.SGFPM_GetTemplateSize(handle, buffer, out size) == SgNative.ErrorNone && size > 0 && size < buffer.Length)
                {
                    var exact = new byte[size];
                    Buffer.BlockCopy(buffer, 0, exact, 0, (int)size);
                    return exact;
                }
                return buffer;
            }
        }

        public override object Probe(FingerScan scan, out string error)
        {
            if (scan != null && scan.Quality > 0 && scan.Quality < VerifyQuality / 2)
            {
                error = "Could not read that. Clean the reader, press your finger flat and try again.";
                return null;
            }
            byte[] t = CreateTemplate(scan, out error);
            return t == null ? null : new SecuGenProbe { Template = t, Quality = scan.Quality };
        }

        public override object EnrollPart(FingerScan scan, out string error)
        {
            if (scan != null && scan.Quality < EnrollQuality)
            {
                error = "That scan was not clear enough (quality " + scan.Quality + " of 100) — press the finger flat and firmly, then try again.";
                return null;
            }
            byte[] t = CreateTemplate(scan, out error);
            if (t == null && error != null && !error.StartsWith("Not a scanner"))
                error = "That scan was not clear enough — press the finger flat and firmly, then try again.";
            return t == null ? null : new SecuGenProbe { Template = t, Quality = scan.Quality };
        }

        // Every enrolment scan must match every other one (the same finger,
        // each time); all of them are kept.
        public override EnrollmentResult Fuse(List<object> parts)
        {
            var result = new EnrollmentResult();
            var templates = new List<byte[]>();
            if (parts != null) foreach (object p in parts) if (p is SecuGenProbe) templates.Add(((SecuGenProbe)p).Template);
            if (templates.Count < ScansToEnroll)
            {
                result.Error = templates.Count == 0
                    ? "No usable scans. Press the finger flat and firmly."
                    : "Need " + (ScansToEnroll - templates.Count) + " more good scan(s) of the same finger.";
                return result;
            }

            lock (gate)
            {
                if (handle == IntPtr.Zero && Ready(300, 400, 500) != SgNative.ErrorNone)
                {
                    result.Error = "The fingerprint engine would not start.";
                    return result;
                }
                for (int i = 0; i < templates.Count; i++)
                {
                    for (int j = i + 1; j < templates.Count; j++)
                    {
                        int matched;
                        uint rc = SgNative.SGFPM_MatchTemplate(handle, templates[i], templates[j], SgNative.SlNormal, out matched);
                        if (rc != SgNative.ErrorNone || matched == 0)
                        {
                            result.Error = "The scans did not match each other. Start again and use the same finger every time.";
                            return result;
                        }
                    }
                }
            }

            result.Ready = true;
            result.TemplateBytes = SecuGenTemplates.Pack(templates);
            return result;
        }

        public override bool CanUse(TemplateCandidate candidate)
        {
            return candidate != null && SecuGenTemplates.IsOurs(candidate.TemplateBase64);
        }

        // Same two rules as the DigitalPersona matcher: the stricter the more
        // fingers are on file (a 1:N search gives N chances to false-accept),
        // and more than one DIFFERENT person matching is reported as ambiguous.
        public override MatchResult Identify(object probe, List<TemplateCandidate> candidates)
        {
            var result = new MatchResult();
            var p = probe as SecuGenProbe;
            var usable = new List<KeyValuePair<TemplateCandidate, List<byte[]>>>();
            if (candidates != null)
            {
                foreach (TemplateCandidate c in candidates)
                {
                    List<byte[]> list = CanUse(c) ? SecuGenTemplates.Unpack(c.TemplateBase64) : null;
                    if (list != null) usable.Add(new KeyValuePair<TemplateCandidate, List<byte[]>>(c, list));
                }
            }
            if (p == null || usable.Count == 0)
            {
                result.Error = "No fingerprints are enrolled yet.";
                return result;
            }

            uint level = SecurityLevelFor(usable.Count);
            TemplateCandidate best = null;
            uint bestScore = 0;
            var matchedPeople = new HashSet<string>();

            lock (gate)
            {
                if (handle == IntPtr.Zero && Ready(300, 400, 500) != SgNative.ErrorNone)
                {
                    result.Error = "The fingerprint engine would not start.";
                    return result;
                }
                foreach (KeyValuePair<TemplateCandidate, List<byte[]>> entry in usable)
                {
                    bool any = false;
                    uint score = 0;
                    foreach (byte[] stored in entry.Value)
                    {
                        int matched;
                        if (SgNative.SGFPM_MatchTemplate(handle, p.Template, stored, level, out matched) != SgNative.ErrorNone || matched == 0) continue;
                        any = true;
                        uint s;
                        if (SgNative.SGFPM_GetMatchingScore(handle, p.Template, stored, out s) == SgNative.ErrorNone && s > score) score = s;
                    }
                    if (!any) continue;
                    matchedPeople.Add(entry.Key.EmployeeId);
                    if (best == null || score > bestScore)
                    {
                        best = entry.Key;
                        bestScore = score;
                    }
                }
            }

            if (matchedPeople.Count == 0)
            {
                result.Error = "Fingerprint not recognised.";
                return result;
            }
            if (matchedPeople.Count > 1)
            {
                result.Ambiguous = true;
                result.Error = "More than one person matched. Please scan again.";
                return result;
            }
            result.Matched = true;
            result.EmployeeId = best.EmployeeId;
            result.Finger = best.Finger;
            result.FarAchieved = (int)bestScore;
            return result;
        }

        // About 1 in 100,000 per finger on file, capped at HIGH so genuine
        // fingers are not turned away (the ambiguity rule is the backstop).
        private static uint SecurityLevelFor(int fingers)
        {
            if (fingers <= 1) return SgNative.SlNormal;
            if (fingers <= 5) return SgNative.SlAboveNormal;
            return SgNative.SlHigh;
        }
    }

    // ---------------------------------------------------------------- the reader

    // Capture on a thread of its own: wait for a finger (GetImageEx with a
    // timeout, so Stop is honoured within a second), hand the scan over, wait
    // for the finger to lift, repeat. The device is opened once and shared by
    // every SecuGenReader in the app; only the one started last receives scans.
    public class SecuGenReader : FingerReader
    {
        private const uint WaitMs = 1000;
        private const int RetryOpenMs = 2000;
        private const int LiftTimeoutMs = 10000;

        private static readonly object DeviceGate = new object();   // every call on the device
        private static readonly object StartGate = new object();    // `active` and each reader's thread
        private static SecuGenReader active;
        private static IntPtr device = IntPtr.Zero;
        private static bool deviceOpen;
        private static int width = 300, height = 400, dpi = 500;

        private readonly IReaderEvents events;
        private readonly int minQuality;
        private Thread thread;
        private volatile bool stopRequested;

        public SecuGenReader(IReaderEvents events, int minQuality)
        {
            this.events = events;
            this.minQuality = Math.Max(1, Math.Min(100, minQuality));
        }

        public override ReaderBrand Brand { get { return ReaderBrand.SecuGen; } }

        private bool Running { get { return !stopRequested && active == this; } }

        public override void Start()
        {
            lock (StartGate)
            {
                stopRequested = false;
                active = this;
                if (thread != null) return;   // still alive: it carries on
                thread = new Thread(Loop) { IsBackground = true, Name = "SecuGen reader" };
                thread.Start();
            }
        }

        public override void Stop()
        {
            Thread t;
            lock (StartGate)
            {
                stopRequested = true;
                if (active == this) active = null;
                t = thread;
            }
            // Wait for the device to be let go (at most one capture wait),
            // unless Stop came from the reader's own thread (a scan handler).
            if (t != null && t != Thread.CurrentThread) t.Join((int)WaitMs + 1500);
        }

        private void Loop()
        {
            bool connected = false;
            string problem = null;
            int failures = 0;
            DateTime nextUsbCheck = DateTime.MinValue;
            ReaderSetup.SecuGenUsb usb = new ReaderSetup.SecuGenUsb();

            while (true)
            {
                lock (StartGate)
                {
                    if (!Running)
                    {
                        thread = null;
                        return;
                    }
                }

                try
                {
                    uint openRc;
                    if (!EnsureOpen(out openRc))
                    {
                        failures++;
                        if (connected)
                        {
                            connected = false;
                            Safe(events.OnReaderDisconnect);
                        }
                        if (DateTime.UtcNow >= nextUsbCheck)
                        {
                            usb = ReaderSetup.FindSecuGenUsb();
                            nextUsbCheck = DateTime.UtcNow.AddSeconds(10);
                        }
                        string now = OpenProblem(openRc, usb, failures);
                        if (now != problem)
                        {
                            problem = now;
                            if (now != null) Log.Info("SecuGen reader: " + now + " (error " + openRc + ")");
                            string p = now;
                            Safe(delegate { events.OnReaderProblem(p); });
                        }
                        Pause(RetryOpenMs);
                        continue;
                    }

                    failures = 0;
                    if (problem != null)
                    {
                        problem = null;
                        Safe(delegate { events.OnReaderProblem(null); });
                    }
                    if (!connected)
                    {
                        connected = true;
                        Log.Info("SecuGen reader open: " + width + "x" + height + " at " + dpi + " dpi");
                        Safe(events.OnReaderConnect);
                    }

                    var image = new byte[width * height];
                    uint rc;
                    lock (DeviceGate)
                    {
                        rc = deviceOpen ? SgNative.SGFPM_GetImageEx(device, image, WaitMs, IntPtr.Zero, (uint)minQuality) : SgNative.ErrorLineDropped;
                    }

                    if (rc == SgNative.ErrorTimeOut || rc == SgNative.ErrorWrongImage) continue;
                    if (rc != SgNative.ErrorNone)
                    {
                        Log.Info("SecuGen reader lost (error " + rc + ")");
                        CloseDevice();
                        connected = false;
                        Safe(events.OnReaderDisconnect);
                        Pause(1000);
                        continue;
                    }
                    if (!Running) continue;

                    uint quality = 0;
                    lock (DeviceGate)
                    {
                        if (deviceOpen) SgNative.SGFPM_GetImageQuality(device, (uint)width, (uint)height, image, out quality);
                    }
                    var scan = new FingerScan { Image = image, Width = width, Height = height, Dpi = dpi, Quality = (int)quality };
                    Safe(events.OnFingerTouch);
                    Safe(delegate { events.OnScan(scan); });
                    WaitForLift();
                    Safe(events.OnFingerGone);
                }
                catch (DllNotFoundException)
                {
                    ReportOnce(ref problem, ReaderSetup.EngineMissingBody(ReaderBrand.SecuGen, "sgfplib.dll not found"));
                    Pause(10000);
                }
                catch (Exception ex)
                {
                    Log.Error("SecuGen reader", ex);
                    Pause(RetryOpenMs);
                }
            }
        }

        private void ReportOnce(ref string problem, string text)
        {
            if (problem == text) return;
            problem = text;
            Safe(delegate { events.OnReaderProblem(text); });
        }

        // What to tell staff about an open that failed, or null for "not plugged in".
        private static string OpenProblem(uint rc, ReaderSetup.SecuGenUsb usb, int failures)
        {
            if (usb.Present && usb.DriverMissing)
                return "Windows has no working driver for the SecuGen reader (Device Manager code " + usb.ProblemCode + "). "
                    + ReaderSetup.DriverMissingBody(ReaderBrand.SecuGen);
            if (SgNative.IsDriverError(rc))
                return ReaderSetup.DriverMissingBody(ReaderBrand.SecuGen);
            if (usb.Present && failures >= 3)
                return "The SecuGen reader is plugged in but could not be opened (error " + rc + "). Unplug it, plug it back in, "
                    + "and if this stays, install the SecuGen reader driver again (secugen.com/drivers).";
            return null;
        }

        // Under DeviceGate: open the device once for the whole app.
        private static bool EnsureOpen(out uint rc)
        {
            lock (DeviceGate)
            {
                rc = SgNative.ErrorNone;
                if (deviceOpen) return true;
                if (device == IntPtr.Zero)
                {
                    IntPtr h;
                    rc = SgNative.SGFPM_Create(out h);
                    if (rc != SgNative.ErrorNone) return false;
                    device = h;
                }
                rc = SgNative.SGFPM_Init(device, SgNative.DevAuto);
                if (rc != SgNative.ErrorNone && !SgNative.IsDriverError(rc)) rc = SgNative.SGFPM_Init(device, SgNative.DevFdu05);
                if (rc == SgNative.ErrorNone) rc = SgNative.SGFPM_OpenDevice(device, SgNative.UsbAutoDetect);
                if (rc != SgNative.ErrorNone)
                {
                    SgNative.SGFPM_Terminate(device);
                    device = IntPtr.Zero;
                    return false;
                }

                var info = new SgNative.DeviceInfo { DeviceSN = new byte[16] };
                if (SgNative.SGFPM_GetDeviceInfo(device, ref info) == SgNative.ErrorNone
                    && info.ImageWidth >= 100 && info.ImageWidth <= 2000 && info.ImageHeight >= 100 && info.ImageHeight <= 2000)
                {
                    width = (int)info.ImageWidth;
                    height = (int)info.ImageHeight;
                    dpi = info.ImageDPI >= 250 && info.ImageDPI <= 2000 ? (int)info.ImageDPI : 500;
                }
                deviceOpen = true;
                return true;
            }
        }

        private static void CloseDevice()
        {
            lock (DeviceGate)
            {
                if (device == IntPtr.Zero) return;
                try
                {
                    if (deviceOpen) SgNative.SGFPM_CloseDevice(device);
                    SgNative.SGFPM_Terminate(device);
                }
                catch { }
                device = IntPtr.Zero;
                deviceOpen = false;
            }
        }

        // Until the finger is off the sensor: a near-empty image (low quality,
        // or almost no contrast). Gives up after LiftTimeoutMs so a dirty
        // sensor cannot stop the reader for good.
        private void WaitForLift()
        {
            var image = new byte[width * height];
            DateTime until = DateTime.UtcNow.AddMilliseconds(LiftTimeoutMs);
            int clear = 0;
            while (Running && DateTime.UtcNow < until)
            {
                uint rc, quality = 0;
                lock (DeviceGate)
                {
                    if (!deviceOpen || image.Length != width * height) return;
                    rc = SgNative.SGFPM_GetImage(device, image);
                    if (rc == SgNative.ErrorNone) SgNative.SGFPM_GetImageQuality(device, (uint)width, (uint)height, image, out quality);
                }
                if (rc != SgNative.ErrorNone) return;
                if (quality < 15 || LooksEmpty(image)) clear++;
                else clear = 0;
                if (clear >= 2) return;
                Thread.Sleep(120);
            }
        }

        private static bool LooksEmpty(byte[] image)
        {
            long sum = 0, sumSq = 0;
            int n = 0;
            for (int i = 0; i < image.Length; i += 7)
            {
                int v = image[i];
                sum += v;
                sumSq += v * v;
                n++;
            }
            if (n == 0) return true;
            double mean = (double)sum / n;
            double variance = (double)sumSq / n - mean * mean;
            return variance < 150; // standard deviation under ~12 grey levels
        }

        private void Pause(int ms)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
            while (Running && DateTime.UtcNow < until) Thread.Sleep(100);
        }

        private static void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Reader event handler failed", ex); }
        }
    }
}
