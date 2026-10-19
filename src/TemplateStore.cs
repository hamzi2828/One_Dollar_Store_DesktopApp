using System;
using System.Collections.Generic;

namespace DollarCoAttendance
{
    // The fingerprints this counter matches against, kept current from
    // GET /api/desktop/fingerprints:
    //
    //   * a FULL download at start-up and every hour;
    //   * in between, only what changed (?updatedSince=<the server's time at
    //     the last sync>): new or re-scanned fingers, fingers removed, and the
    //     list of employees who still have fingers (anyone not on it -- left,
    //     deactivated -- is dropped here too).
    //
    // The set is replaced whole (see TemplateSet), and a copy is kept on disk
    // (OfflineStore, DPAPI) so the reader still works after a restart with no
    // internet.
    public class TemplateStore
    {
        private static readonly TimeSpan FullSyncEvery = TimeSpan.FromHours(1);

        private volatile TemplateSet current = TemplateSet.Empty;
        private volatile bool loaded;
        private string serverTime;           // the server's clock at the last sync (ISO)
        private DateTime lastFullUtc = DateTime.MinValue;
        private readonly object gate = new object();

        public TemplateSet Current { get { return current; } }

        // At least one list (downloaded or this PC's copy) is in use.
        public bool Loaded { get { return loaded; } }

        public void LoadCached()
        {
            string time;
            TemplateSet cached = OfflineStore.LoadTemplates(out time);
            if (cached == null) return;
            lock (gate)
            {
                current = cached;
                serverTime = time;
                loaded = true;
            }
            Log.Info("Fingerprints loaded from this PC's offline copy: " + cached.Count);
        }

        // Null on success; otherwise the server's answer (for the caller to
        // tell "no internet" from "signed out"). `changed` says whether the set
        // in use was replaced.
        public Dictionary<string, object> Sync(ApiClient api, bool forceFull, out bool changed)
        {
            changed = false;
            bool full;
            string since;
            lock (gate)
            {
                full = forceFull || !loaded || serverTime == null || DateTime.UtcNow - lastFullUtc > FullSyncEvery;
                since = full ? null : serverTime;
            }

            Dictionary<string, object> result = api.Get(
                "api/desktop/fingerprints" + (since != null ? "?updatedSince=" + Uri.EscapeDataString(since) : string.Empty), 60000);
            if (!Json.Ok(result)) return result;
            Dictionary<string, object> data = Json.Data(result) ?? new Dictionary<string, object>();

            bool needFull;
            TemplateSet next = Merge(full ? null : current, data, out needFull);
            if (needFull && !full)
            {
                // Someone has fingers on file that this PC has never had (an
                // employee made active again): the change list cannot give
                // them back, a full download can.
                Log.Info("Fingerprint sync: an employee's fingers are missing here, downloading the full list");
                return Sync(api, true, out changed);
            }

            string time = Json.Str(data, "serverTime");
            lock (gate)
            {
                changed = full || !SameSet(current, next);
                current = next;
                loaded = true;
                serverTime = time ?? serverTime;
                if (full) lastFullUtc = DateTime.UtcNow;
            }
            if (changed) OfflineStore.SaveTemplates(next, time);
            if (changed) Log.Info("Fingerprints " + (full ? "downloaded" : "updated") + ": " + next.Count + " finger(s) of " + next.PeopleCount + " employee(s)");
            return null;
        }

        // `previous` null = a full list. Pure: no I/O, so the self-test can run it.
        public static TemplateSet Merge(TemplateSet previous, Dictionary<string, object> data, out bool needFull)
        {
            needFull = false;
            var byKey = new Dictionary<string, TemplateCandidate>();
            if (previous != null)
                foreach (TemplateCandidate c in previous.Candidates) byKey[c.Key()] = c;

            foreach (Dictionary<string, object> row in Json.Rows(data, "removed"))
                byKey.Remove(Json.Str(row, "employeeId") + "|" + Json.Str(row, "finger"));

            foreach (Dictionary<string, object> row in Json.Rows(data, "templates"))
            {
                var c = new TemplateCandidate
                {
                    EmployeeId = Json.Str(row, "employeeId"),
                    Finger = Json.Str(row, "finger"),
                    TemplateBase64 = Json.Str(row, "template"),
                };
                if (c.EmployeeId != null && c.Finger != null && !string.IsNullOrEmpty(c.TemplateBase64)) byKey[c.Key()] = c;
            }

            // Only employees who are still active and still have fingers.
            var stillThere = new HashSet<string>(Json.Strings(data, "employeeIds"));
            var kept = new List<TemplateCandidate>();
            var present = new HashSet<string>();
            foreach (TemplateCandidate c in byKey.Values)
            {
                if (!stillThere.Contains(c.EmployeeId)) continue;
                kept.Add(c);
                present.Add(c.EmployeeId);
            }
            foreach (string id in stillThere)
                if (!present.Contains(id)) needFull = true;

            kept.Sort((a, b) => string.CompareOrdinal(a.Key(), b.Key()));
            return new TemplateSet(kept);
        }

        private static bool SameSet(TemplateSet a, TemplateSet b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a.Candidates[i].Key() != b.Candidates[i].Key()) return false;
                if (a.Candidates[i].TemplateBase64 != b.Candidates[i].TemplateBase64) return false;
            }
            return true;
        }
    }
}
