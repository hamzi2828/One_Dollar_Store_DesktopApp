using System.Collections.Generic;

namespace DollarCoAttendance
{
    public class EnrollmentResult
    {
        public bool Ready;
        public string Error;
        public byte[] TemplateBytes;
    }

    // Fuses several scans of one finger into a single enrolment template, on
    // this PC. Only the finished template goes to the server; single scans
    // never leave the machine.
    public static class FingerprintEnroller
    {
        // Enrolment and verification feature sets are NOT interchangeable in
        // this SDK: mixing them silently produces a template that never matches.
        public static DPFP.FeatureSet ExtractEnrollmentFeatures(byte[] rawSample, out string error)
        {
            DPFP.FeatureSet features = FingerprintMatcher.Extract(rawSample, DPFP.Processing.DataPurpose.Enrollment, out error);
            if (features == null && error != null && !error.StartsWith("Not a scanner"))
                error = "That scan was not clear enough — press the finger flat and firmly, then try again.";
            return features;
        }

        // Feeds the good scans to the engine until it reports Ready (it needs
        // several good scans of the SAME finger).
        public static EnrollmentResult Fuse(List<DPFP.FeatureSet> accepted)
        {
            var result = new EnrollmentResult();
            if (accepted == null || accepted.Count == 0)
            {
                result.Error = "No usable scans. Press the finger flat and firmly.";
                return result;
            }

            var enrollment = new DPFP.Processing.Enrollment();
            foreach (DPFP.FeatureSet fs in accepted)
            {
                if (enrollment.TemplateStatus == DPFP.Processing.Enrollment.Status.Ready) break;
                enrollment.AddFeatures(fs);
            }

            if (enrollment.TemplateStatus != DPFP.Processing.Enrollment.Status.Ready || enrollment.Template == null)
            {
                result.Error = enrollment.TemplateStatus == DPFP.Processing.Enrollment.Status.Insufficient
                    ? "Need " + enrollment.FeaturesNeeded + " more good scan(s) of the same finger."
                    : "The scans did not match each other. Start again and use the same finger every time.";
                return result;
            }

            result.Ready = true;
            result.TemplateBytes = enrollment.Template.Bytes;
            return result;
        }
    }
}
