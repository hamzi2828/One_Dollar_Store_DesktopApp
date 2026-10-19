using System;
using System.Collections.Generic;

namespace DollarCoAttendance
{
    // One enrolled finger, as GET /api/desktop/fingerprints hands it over.
    public class TemplateCandidate
    {
        public string EmployeeId;
        public string Finger;          // right_index ...
        public string TemplateBase64;

        public string Key() { return EmployeeId + "|" + Finger; }
    }

    public class MatchResult
    {
        public bool Matched;
        public bool Ambiguous;
        public string EmployeeId;
        public string Finger;
        public int FarAchieved;
        public string Error;
    }

    // Every template the reader matches against, as ONE immutable object. A
    // refresh builds a new set and swaps the reference (atomic in .NET); each
    // scan takes the reference once, so a refresh landing mid-match never
    // pulls the list out from under it.
    public class TemplateSet
    {
        // False-accept rate asked of the engine: 1 in 100,000 per comparison
        // (tightened further by the number of candidates, see FarRequested).
        public const int FarDenominator = 100000;

        public static readonly TemplateSet Empty = new TemplateSet(null);

        public readonly List<TemplateCandidate> Candidates;

        public TemplateSet(List<TemplateCandidate> candidates)
        {
            Candidates = candidates ?? new List<TemplateCandidate>();
        }

        public int Count { get { return Candidates.Count; } }

        public int PeopleCount
        {
            get
            {
                var people = new HashSet<string>();
                foreach (TemplateCandidate c in Candidates) people.Add(c.EmployeeId);
                return people.Count;
            }
        }
    }

    // Local 1:N matcher with the DigitalPersona engine (dpHFtrEx.dll /
    // dpHMatch.dll) on this PC. The POS server is Node on Linux, where these
    // Windows-only libraries cannot run, so the counter matches the finger
    // itself and tells the server only WHO it was.
    public static class FingerprintMatcher
    {
        // DP treats int.MaxValue as FAR = 1.0, so "1 in N" is MaxValue / N.
        private const long FarScale = int.MaxValue;

        // Null when the engine DLLs load; otherwise the reason.
        public static string SelfTest()
        {
            try
            {
                GC.KeepAlive(new DPFP.Processing.FeatureExtraction());
                GC.KeepAlive(new DPFP.Verification.Verification());
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        // A verification feature set from one raw scan, or null with `error`
        // when the scan was unreadable or the engine's feedback was not Good.
        public static DPFP.FeatureSet ExtractVerificationFeatures(byte[] rawSample, out string error)
        {
            return Extract(rawSample, DPFP.Processing.DataPurpose.Verification, out error);
        }

        internal static DPFP.FeatureSet Extract(byte[] rawSample, DPFP.Processing.DataPurpose purpose, out string error)
        {
            error = null;
            var sample = new DPFP.Sample();
            try
            {
                sample.DeSerialize(rawSample);
            }
            catch (Exception ex)
            {
                error = "Not a scanner sample: " + ex.Message;
                return null;
            }

            var extractor = new DPFP.Processing.FeatureExtraction();
            var features = new DPFP.FeatureSet();
            DPFP.Capture.CaptureFeedback feedback = DPFP.Capture.CaptureFeedback.None;
            try
            {
                extractor.CreateFeatureSet(sample, purpose, ref feedback, ref features);
            }
            catch (Exception ex)
            {
                error = "The scan could not be read: " + ex.Message;
                return null;
            }

            if (feedback != DPFP.Capture.CaptureFeedback.Good)
            {
                error = FeedbackMessage(feedback);
                return null;
            }
            if (features == null || features.Bytes == null || features.Bytes.Length == 0)
            {
                error = "No fingerprint found in that scan. Please try again.";
                return null;
            }
            return features;
        }

        public static MatchResult Identify(DPFP.FeatureSet features, TemplateSet templates)
        {
            return Identify(features, (templates ?? TemplateSet.Empty).Candidates);
        }

        // The strongest match among the candidates. Two rules: the requested
        // FAR is divided by the number of candidates (a 1:N search gives N
        // chances to false-accept), and more than one DIFFERENT person matching
        // is reported as ambiguous rather than picking one -- marking the wrong
        // person present is worse than asking for another scan.
        public static MatchResult Identify(DPFP.FeatureSet features, List<TemplateCandidate> candidates)
        {
            var result = new MatchResult();
            if (candidates == null || candidates.Count == 0)
            {
                result.Error = "No fingerprints are enrolled yet.";
                return result;
            }

            int farRequested = FarRequested(candidates.Count);
            TemplateCandidate best = null;
            int bestFar = int.MaxValue;
            var matchedPeople = new HashSet<string>();

            foreach (TemplateCandidate candidate in candidates)
            {
                DPFP.Template template;
                try
                {
                    template = new DPFP.Template();
                    template.DeSerialize(Convert.FromBase64String(candidate.TemplateBase64));
                }
                catch (Exception)
                {
                    continue; // an unreadable row is skipped, not fatal
                }

                DPFP.Verification.Verification.Result verify;
                try
                {
                    verify = DPFP.Verification.Verification.Verify(features, template, farRequested);
                }
                catch (Exception)
                {
                    continue;
                }
                if (verify == null || !verify.Verified) continue;

                matchedPeople.Add(candidate.EmployeeId);
                if (verify.FARAchieved < bestFar)
                {
                    bestFar = verify.FARAchieved;
                    best = candidate;
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
            result.FarAchieved = bestFar;
            return result;
        }

        private static int FarRequested(int candidateCount)
        {
            long denominator = TemplateSet.FarDenominator;
            if (candidateCount > 1)
            {
                long scaled = denominator * candidateCount;
                denominator = scaled > int.MaxValue ? int.MaxValue : scaled;
            }
            long far = FarScale / denominator;
            return far < 1 ? 1 : (int)far;
        }

        private static string FeedbackMessage(DPFP.Capture.CaptureFeedback feedback)
        {
            switch (feedback)
            {
                case DPFP.Capture.CaptureFeedback.TooHigh: return "Move your finger down a little and try again.";
                case DPFP.Capture.CaptureFeedback.TooLow: return "Move your finger up a little and try again.";
                case DPFP.Capture.CaptureFeedback.TooLeft: return "Move your finger right a little and try again.";
                case DPFP.Capture.CaptureFeedback.TooRight: return "Move your finger left a little and try again.";
                case DPFP.Capture.CaptureFeedback.TooFast: return "Press more slowly and try again.";
                case DPFP.Capture.CaptureFeedback.TooSkewed: return "Press your finger flat and try again.";
                case DPFP.Capture.CaptureFeedback.TooShort: return "Press your finger fully onto the reader.";
                case DPFP.Capture.CaptureFeedback.TooSmall: return "Press more firmly and try again.";
                case DPFP.Capture.CaptureFeedback.NoFinger: return "No finger found. Please try again.";
                default: return "Could not read that. Clean the reader, press your finger flat and try again.";
            }
        }
    }
}
