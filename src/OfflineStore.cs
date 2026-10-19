using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DollarCoAttendance
{
    // A fingerprint punch taken while the server could not be reached. ClientId
    // is the id it goes out under on EVERY try: the server records a clientId
    // once, so a queue sent twice (an answer lost on the way back) never
    // counts a punch twice.
    public class QueuedPunch
    {
        public string ClientId;
        public string EmployeeId;
        public string EmployeeName;
        public DateTime ScannedAtUtc;
    }

    // What the counter keeps on disk so it carries on when the internet drops:
    //
    //   session.dat     the signed-in account (open straight into the main window offline)
    //   templates.dat   every enrolled finger (the reader still matches offline)
    //   staff.dat       names, photos and designations
    //   queue.dat       punches taken offline, oldest first, until they are sent
    //
    // One folder per server (offline-<hash>), encrypted with Windows DPAPI for
    // the Windows account the app runs as (CurrentUser): fingerprint templates
    // are biometric data, so no other account or program on the PC can read
    // them. Nothing here throws: a copy that cannot be read is no copy.
    public static class OfflineStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DollarCo.Attendance.Offline.v1");

        private const string SessionFile = "session.dat";
        private const string TemplatesFile = "templates.dat";
        private const string StaffFile = "staff.dat";
        private const string QueueFile = "queue.dat";

        private static string folderName = "offline";
        private static readonly object gate = new object();
        private static List<QueuedPunch> queue;

        public static void UseScope(string apiBaseUrl)
        {
            lock (gate)
            {
                folderName = "offline-" + TokenStore.ScopeHash(apiBaseUrl);
                queue = null;
            }
        }

        public static string Folder
        {
            get { return Path.Combine(AppInfo.DataFolder, folderName); }
        }

        // ---- the signed-in account ----

        public static void SaveSession(SessionUser user)
        {
            if (user != null) Write(SessionFile, user.ToDictionary());
        }

        public static SessionUser LoadSession()
        {
            return SessionUser.From(Read(SessionFile));
        }

        // ---- fingerprints ----

        public static void SaveTemplates(TemplateSet set, string serverTime)
        {
            if (set == null) return;
            var rows = new List<object>();
            foreach (TemplateCandidate c in set.Candidates)
            {
                var row = new Dictionary<string, object>();
                row["employeeId"] = c.EmployeeId;
                row["finger"] = c.Finger;
                row["template"] = c.TemplateBase64;
                rows.Add(row);
            }
            var data = new Dictionary<string, object>();
            data["serverTime"] = serverTime;
            data["templates"] = rows;
            Write(TemplatesFile, data);
        }

        // Null when there is no usable copy on this PC.
        public static TemplateSet LoadTemplates(out string serverTime)
        {
            serverTime = null;
            Dictionary<string, object> data = Read(TemplatesFile);
            if (data == null) return null;
            serverTime = Json.Str(data, "serverTime");
            var loaded = new List<TemplateCandidate>();
            foreach (Dictionary<string, object> row in Json.Rows(data, "templates"))
            {
                var c = new TemplateCandidate
                {
                    EmployeeId = Json.Str(row, "employeeId"),
                    Finger = Json.Str(row, "finger"),
                    TemplateBase64 = Json.Str(row, "template"),
                };
                if (c.EmployeeId != null && c.Finger != null && c.TemplateBase64 != null) loaded.Add(c);
            }
            return new TemplateSet(loaded);
        }

        // ---- names and photos ----

        public static void SaveStaff(StaffDirectory directory)
        {
            if (directory == null) return;
            var rows = new List<object>();
            foreach (StaffMember m in directory.All) rows.Add(m.ToDictionary());
            var data = new Dictionary<string, object>();
            data["staff"] = rows;
            Write(StaffFile, data);
        }

        public static StaffDirectory LoadStaff()
        {
            Dictionary<string, object> data = Read(StaffFile);
            if (data == null) return null;
            var people = new List<StaffMember>();
            foreach (Dictionary<string, object> row in Json.Rows(data, "staff")) people.Add(StaffMember.From(row));
            return new StaffDirectory(people);
        }

        // Signing out, or the server refusing the account: the copies go. The
        // queue stays -- those punches happened -- and is sent at the next
        // sign-in to this server.
        public static void ClearCache()
        {
            lock (gate)
            {
                foreach (string name in new[] { SessionFile, TemplatesFile, StaffFile })
                {
                    try
                    {
                        string path = Path.Combine(Folder, name);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not delete the offline copy " + name, ex);
                    }
                }
            }
        }

        // ---- punches waiting to be sent ----

        public static int QueueCount
        {
            get { lock (gate) { return Queue().Count; } }
        }

        public static List<QueuedPunch> QueueSnapshot()
        {
            lock (gate) { return new List<QueuedPunch>(Queue()); }
        }

        public static QueuedPunch Peek()
        {
            lock (gate)
            {
                List<QueuedPunch> q = Queue();
                return q.Count > 0 ? q[0] : null;
            }
        }

        // False when a punch with this clientId is already waiting.
        public static bool Enqueue(QueuedPunch punch)
        {
            lock (gate)
            {
                List<QueuedPunch> q = Queue();
                foreach (QueuedPunch waiting in q)
                    if (waiting.ClientId == punch.ClientId) return false;
                q.Add(punch);
                q.Sort((a, b) => a.ScannedAtUtc.CompareTo(b.ScannedAtUtc));
                SaveQueue(q);
                return true;
            }
        }

        public static void Remove(string clientId)
        {
            lock (gate)
            {
                List<QueuedPunch> q = Queue();
                if (q.RemoveAll(p => p.ClientId == clientId) > 0) SaveQueue(q);
            }
        }

        // Loaded from disk once per scope, then kept in memory; every change is
        // written straight back, so a power cut loses nothing shown as saved.
        private static List<QueuedPunch> Queue()
        {
            if (queue != null) return queue;
            queue = new List<QueuedPunch>();
            foreach (Dictionary<string, object> row in Json.Rows(Read(QueueFile), "punches"))
            {
                DateTime? at = ShopTime.ParseUtc(Json.Str(row, "scannedAt"));
                var punch = new QueuedPunch
                {
                    ClientId = Json.Str(row, "clientId"),
                    EmployeeId = Json.Str(row, "employeeId"),
                    EmployeeName = Json.Str(row, "employeeName"),
                    ScannedAtUtc = at ?? DateTime.UtcNow,
                };
                if (punch.ClientId != null && punch.EmployeeId != null && at.HasValue) queue.Add(punch);
            }
            queue.Sort((a, b) => a.ScannedAtUtc.CompareTo(b.ScannedAtUtc));
            if (queue.Count > 0) Log.Info(queue.Count + " punch(es) saved offline are waiting to be sent");
            return queue;
        }

        private static void SaveQueue(List<QueuedPunch> q)
        {
            // No file = nothing waiting (the uninstaller looks for it).
            if (q.Count == 0)
            {
                try
                {
                    string path = Path.Combine(Folder, QueueFile);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not clear the offline queue file", ex);
                }
                return;
            }

            var rows = new List<object>();
            foreach (QueuedPunch p in q)
            {
                var row = new Dictionary<string, object>();
                row["clientId"] = p.ClientId;
                row["employeeId"] = p.EmployeeId;
                row["employeeName"] = p.EmployeeName;
                row["scannedAt"] = ShopTime.ToIso(p.ScannedAtUtc);
                rows.Add(row);
            }
            var data = new Dictionary<string, object>();
            data["punches"] = rows;
            Write(QueueFile, data);
        }

        // ---- files ----

        private static JavaScriptSerializer Serializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 64 * 1024 * 1024;
            return serializer;
        }

        // Written to a side file and swapped in, so a crash mid-write leaves
        // the previous copy rather than half a file.
        private static void Write(string name, Dictionary<string, object> data)
        {
            lock (gate)
            {
                try
                {
                    string dir = Folder;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    byte[] plain = Encoding.UTF8.GetBytes(Serializer().Serialize(data));
                    byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                    string path = Path.Combine(dir, name);
                    string temp = path + ".tmp";
                    File.WriteAllBytes(temp, cipher);
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                catch (Exception ex)
                {
                    Log.Error("Could not save the offline copy " + name, ex);
                }
            }
        }

        private static Dictionary<string, object> Read(string name)
        {
            lock (gate)
            {
                try
                {
                    string path = Path.Combine(Folder, name);
                    if (!File.Exists(path)) return null;
                    byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                    return Serializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(plain));
                }
                catch (Exception ex)
                {
                    Log.Error("Could not read the offline copy " + name, ex);
                    return null;
                }
            }
        }

        // For the self-test only.
        internal static string CurrentQueuePath { get { return Path.Combine(Folder, QueueFile); } }
        internal static void ForgetQueueInMemory() { lock (gate) { queue = null; } }
    }
}
