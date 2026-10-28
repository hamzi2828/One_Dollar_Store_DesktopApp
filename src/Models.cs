using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DollarCoAttendance
{
    // The account signed in on the counter, as GET /api/desktop/session
    // describes it. The server decides the permissions; this only mirrors them.
    public class SessionUser
    {
        public string Id;
        public string Name;
        public string Email;
        public string RoleName;
        public bool IsSuperAdmin;
        public bool CanView;     // attendance_kiosk view: fingerprints, board
        public bool CanPunch;    // attendance_kiosk create: mark attendance
        public bool CanEnroll;   // employees edit or attendance edit

        // features.sales (desktop_pos view): show the Sales button. True when
        // the server does not send `features` (an older server) so the button
        // is never lost by an upgrade on one side only.
        public bool SalesEnabled = true;

        // What the counter needs: without both, it cannot do its job.
        public bool CanRunDesk { get { return CanView && CanPunch; } }

        public static SessionUser From(Dictionary<string, object> data)
        {
            if (data == null) return null;
            Dictionary<string, object> user = Json.Obj(data, "user");
            if (user == null) return null;
            var s = new SessionUser();
            s.Id = Json.Str(user, "id");
            s.Name = Json.Str(user, "name");
            s.Email = Json.Str(user, "email");
            s.RoleName = Json.Str(data, "roleName");
            s.IsSuperAdmin = Json.Bool(data, "isSuperAdmin");
            s.CanView = Json.Bool(data, "canView");
            s.CanPunch = Json.Bool(data, "canPunch");
            s.CanEnroll = Json.Bool(data, "canEnroll");
            Dictionary<string, object> features = Json.Obj(data, "features");
            if (features != null && features.ContainsKey("sales")) s.SalesEnabled = Json.Bool(features, "sales");
            return s.Id == null ? null : s;
        }

        public Dictionary<string, object> ToDictionary()
        {
            var user = new Dictionary<string, object>();
            user["id"] = Id;
            user["name"] = Name;
            user["email"] = Email;
            var data = new Dictionary<string, object>();
            data["user"] = user;
            data["roleName"] = RoleName;
            data["isSuperAdmin"] = IsSuperAdmin;
            data["canView"] = CanView;
            data["canPunch"] = CanPunch;
            data["canEnroll"] = CanEnroll;
            var features = new Dictionary<string, object>();
            features["sales"] = SalesEnabled;
            data["features"] = features;
            return data;
        }

        // "Signed in as Usman (usman@shop.pk)" for the header.
        public string HeaderLine()
        {
            string who = !string.IsNullOrEmpty(Name) ? Name : (Email ?? "Signed in");
            if (!string.IsNullOrEmpty(Email) && Email != who) who += " (" + Email + ")";
            return "Signed in as " + who;
        }
    }

    // The fingers the server knows about, as 'right_index' keys.
    public static class Fingers
    {
        public static readonly string[] Keys =
        {
            "left_little", "left_ring", "left_middle", "left_index", "left_thumb",
            "right_thumb", "right_index", "right_middle", "right_ring", "right_little",
        };

        // "right_index" -> "Right index"
        public static string Label(string key)
        {
            if (string.IsNullOrEmpty(key)) return "Finger";
            string s = key.Replace('_', ' ');
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // "right_index" -> "Index" (for the hand diagram buttons)
        public static string ShortLabel(string key)
        {
            int cut = (key ?? string.Empty).IndexOf('_');
            string s = cut >= 0 ? key.Substring(cut + 1) : (key ?? string.Empty);
            return s.Length == 0 ? "Finger" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }

    public class FingerInfo
    {
        public string Finger;
        public int? Quality;
        public DateTime? EnrolledAt;
        public string DeviceName;
    }

    // One employee, from GET /api/desktop/employees: what the counter shows
    // after a match and what the enrolment window lists. Nothing private.
    public class StaffMember
    {
        public string Id;
        public string Name;
        public string Code;
        public string Designation;
        public string PhotoUrl;
        public int FingerCount;
        public List<FingerInfo> Enrolled = new List<FingerInfo>();
        public string StateKind;      // not_in | in | out
        public DateTime? StateSince;
        public DateTime? StateOut;

        public string Initials
        {
            get { return InitialsOf(Name); }
        }

        public static string InitialsOf(string name)
        {
            string[] parts = (name ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            string first = parts[0].Substring(0, 1);
            string last = parts.Length > 1 ? parts[parts.Length - 1].Substring(0, 1) : string.Empty;
            return (first + last).ToUpperInvariant();
        }

        public bool HasFinger(string finger)
        {
            return Enrolled.Any(f => f.Finger == finger);
        }

        public bool Matches(string term)
        {
            if (string.IsNullOrEmpty(term)) return true;
            return Contains(Name, term) || Contains(Code, term) || Contains(Designation, term);
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static StaffMember From(Dictionary<string, object> row)
        {
            var m = new StaffMember();
            m.Id = Json.Str(row, "_id");
            m.Name = Json.Str(row, "name") ?? string.Empty;
            m.Code = Json.Str(row, "code") ?? string.Empty;
            m.Designation = Json.Str(row, "designation") ?? string.Empty;
            m.PhotoUrl = Json.Str(row, "photo");
            m.FingerCount = Json.Int(row, "fingers");
            foreach (Dictionary<string, object> f in Json.Rows(row, "enrolled"))
            {
                m.Enrolled.Add(new FingerInfo
                {
                    Finger = Json.Str(f, "finger"),
                    Quality = Json.NullableInt(f, "quality"),
                    EnrolledAt = Json.Time(f, "enrolledAt"),
                    DeviceName = Json.Str(f, "deviceName") ?? string.Empty,
                });
            }
            Dictionary<string, object> state = Json.Obj(row, "state");
            m.StateKind = Json.Str(state, "kind") ?? "not_in";
            m.StateSince = Json.Time(state, "since");
            m.StateOut = Json.Time(state, "outAt");
            return m;
        }

        public Dictionary<string, object> ToDictionary()
        {
            var row = new Dictionary<string, object>();
            row["_id"] = Id;
            row["name"] = Name;
            row["code"] = Code;
            row["designation"] = Designation;
            row["photo"] = PhotoUrl;
            row["fingers"] = FingerCount;
            var enrolled = new List<object>();
            foreach (FingerInfo f in Enrolled)
            {
                var e = new Dictionary<string, object>();
                e["finger"] = f.Finger;
                e["quality"] = f.Quality;
                e["deviceName"] = f.DeviceName;
                if (f.EnrolledAt.HasValue) e["enrolledAt"] = ShopTime.ToIso(f.EnrolledAt.Value);
                enrolled.Add(e);
            }
            row["enrolled"] = enrolled;
            return row;
        }
    }

    // Everyone the counter knows, by id. Replaced whole on every load, never
    // edited, so the reader's thread can read it without a lock.
    public class StaffDirectory
    {
        public static readonly StaffDirectory Empty = new StaffDirectory(new List<StaffMember>());

        public readonly List<StaffMember> All;
        private readonly Dictionary<string, StaffMember> byId = new Dictionary<string, StaffMember>();

        public StaffDirectory(List<StaffMember> people)
        {
            All = (people ?? new List<StaffMember>())
                .Where(p => !string.IsNullOrEmpty(p.Id) && p.Name.Length > 0)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (StaffMember p in All) byId[p.Id] = p;
        }

        public int Count { get { return All.Count; } }

        public StaffMember Find(string id)
        {
            StaffMember m;
            return id != null && byId.TryGetValue(id, out m) ? m : null;
        }

        // GET /api/desktop/employees. Null (with the answer kept in `result`)
        // when it did not work.
        public static StaffDirectory Load(ApiClient api, out Dictionary<string, object> result)
        {
            result = api.Get("api/desktop/employees");
            if (!Json.Ok(result)) return null;
            var people = new List<StaffMember>();
            foreach (Dictionary<string, object> row in Json.Rows(result, "data")) people.Add(StaffMember.From(row));
            return new StaffDirectory(people);
        }
    }

    // The server's answer to one fingerprint punch (POST /api/desktop/punch).
    public class PunchResult
    {
        public string Kind;            // in | out
        public DateTime? At;
        public string EmployeeId;
        public string Name;
        public string Designation;
        public string Status;          // present | late | half_day | ...
        public int LateMinutes;
        public int EarlyLeaveMinutes;
        public int WorkedMinutes;
        public int OvertimeMinutes;
        public bool Duplicate;
        public bool Offline;
        public bool Repeated;
        public string ShiftName;
        public string Message;

        public static PunchResult From(Dictionary<string, object> result)
        {
            Dictionary<string, object> d = Json.Data(result);
            if (d == null) return null;
            var p = new PunchResult();
            p.Kind = Json.Str(d, "kind");
            p.At = Json.Time(d, "at");
            p.EmployeeId = Json.Str(d, "employeeId");
            p.Name = Json.Str(d, "name");
            p.Designation = Json.Str(d, "designation");
            p.Status = Json.Str(d, "status");
            p.LateMinutes = Json.Int(d, "lateMinutes");
            p.EarlyLeaveMinutes = Json.Int(d, "earlyLeaveMinutes");
            p.WorkedMinutes = Json.Int(d, "workedMinutes");
            p.OvertimeMinutes = Json.Int(d, "overtimeMinutes");
            p.Duplicate = Json.Bool(d, "duplicate");
            p.Offline = Json.Bool(d, "offline");
            p.Repeated = Json.Bool(d, "repeated");
            p.ShiftName = Json.Str(d, "shiftName");
            p.Message = Json.Str(d, "message") ?? Json.Message(result);
            return p.Kind == null ? null : p;
        }

        public bool IsOut { get { return Kind == "out"; } }
        public bool IsLate { get { return Kind == "in" && LateMinutes > 0; } }

        // The big word on the result card.
        public string Headline
        {
            get
            {
                if (Duplicate) return IsOut ? "ALREADY OUT" : "ALREADY IN";
                return IsOut ? "OUT" : "IN";
            }
        }

        // The line beside the time: "on time", "12 min late", "worked 7 h 35 m".
        public string Detail
        {
            get
            {
                if (Duplicate) return "already marked a moment ago";
                if (IsOut)
                {
                    string line = "worked " + ShopTime.Duration(WorkedMinutes);
                    if (OvertimeMinutes > 0) line += " · " + ShopTime.Duration(OvertimeMinutes) + " overtime";
                    else if (EarlyLeaveMinutes > 0) line += " · left " + ShopTime.Duration(EarlyLeaveMinutes) + " early";
                    return line;
                }
                if (LateMinutes > 0) return ShopTime.Duration(LateMinutes) + " late";
                return Status == "present" && !string.IsNullOrEmpty(ShiftName) ? "on time" : "welcome";
            }
        }

        // Green in on time, amber late, blue out, grey a double tap.
        public Color Accent
        {
            get
            {
                if (Duplicate) return Theme.Neutral;
                if (IsOut) return Theme.Info;
                return IsLate ? Theme.Warning : Theme.Success;
            }
        }

        // What the counter says out loud (kept short: the queue is waiting).
        public string Speech(string firstName)
        {
            string who = string.IsNullOrEmpty(firstName) ? string.Empty : firstName + ", ";
            if (Duplicate) return who + "already marked.";
            if (IsOut) return who + "clocked out. Thank you.";
            if (LateMinutes > 0) return who + "clocked in, " + LateMinutes + (LateMinutes == 1 ? " minute" : " minutes") + " late.";
            return who + "clocked in.";
        }
    }

    // One person on the counter's "today" list (GET /api/desktop/board), or a
    // row this PC adds itself: a punch saved offline, or one the server would
    // not record once it was sent.
    public class BoardRow
    {
        public string EmployeeId;
        public string Name;
        public string Designation;
        public string ShiftType;      // shift | off | leave | holiday | none
        public string ShiftName;
        public DateTime? ShiftStart;
        public DateTime? ShiftEnd;
        public string LeaveType;
        public DateTime? CheckIn;
        public DateTime? CheckOut;
        public string State;          // in | out | not_in | absent | leave | off | holiday | missing_out | queued | problem
        public string Status;
        public int LateMinutes;
        public string LocalNote;      // for queued / problem rows

        public static BoardRow From(Dictionary<string, object> row)
        {
            Dictionary<string, object> shift = Json.Obj(row, "shift");
            return new BoardRow
            {
                EmployeeId = Json.Str(row, "employeeId"),
                Name = Json.Str(row, "name") ?? string.Empty,
                Designation = Json.Str(row, "designation") ?? string.Empty,
                ShiftType = Json.Str(shift, "type") ?? "none",
                ShiftName = Json.Str(shift, "name") ?? string.Empty,
                ShiftStart = Json.Time(shift, "start"),
                ShiftEnd = Json.Time(shift, "end"),
                LeaveType = Json.Str(shift, "leaveType"),
                CheckIn = Json.Time(row, "checkIn"),
                CheckOut = Json.Time(row, "checkOut"),
                State = Json.Str(row, "state") ?? "off",
                Status = Json.Str(row, "status") ?? string.Empty,
                LateMinutes = Json.Int(row, "lateMinutes"),
            };
        }

        // "10:00 am – 6:00 pm", or why there is no shift.
        public string ShiftText
        {
            get
            {
                if (ShiftType == "shift" && ShiftStart.HasValue && ShiftEnd.HasValue)
                    return ShopTime.Clock(ShiftStart.Value) + " – " + ShopTime.Clock(ShiftEnd.Value);
                switch (ShiftType)
                {
                    case "leave": return LeaveType == "sick" ? "Sick leave" : LeaveType == "unpaid" ? "Unpaid leave" : "On leave";
                    case "holiday": return "Holiday";
                    case "off": return "Day off";
                    default: return "No shift";
                }
            }
        }

        // The pill on the right of the row.
        public string Pill
        {
            get
            {
                switch (State)
                {
                    case "in": return LateMinutes > 0 ? "IN · " + ShopTime.Duration(LateMinutes).ToUpperInvariant() + " LATE" : "IN";
                    case "out": return "OUT";
                    case "not_in": return "NOT IN YET";
                    case "absent": return "ABSENT";
                    case "leave": return "ON LEAVE";
                    case "holiday": return "HOLIDAY";
                    case "missing_out": return "NO CHECK-OUT";
                    case "queued": return "WAITING TO SEND";
                    case "problem": return "NOT RECORDED";
                    default: return "OFF";
                }
            }
        }

        // The times under the pill.
        public string TimesText
        {
            get
            {
                if (State == "queued" || State == "problem") return LocalNote ?? string.Empty;
                if (CheckIn.HasValue && CheckOut.HasValue) return ShopTime.Clock(CheckIn.Value) + " → " + ShopTime.Clock(CheckOut.Value);
                if (CheckIn.HasValue) return "since " + ShopTime.Clock(CheckIn.Value);
                return string.Empty;
            }
        }

        public Color Accent
        {
            get
            {
                switch (State)
                {
                    case "in": return LateMinutes > 0 ? Theme.Warning : Theme.Success;
                    case "out": return Theme.Info;
                    case "not_in": return Theme.TextSecondary;
                    case "absent":
                    case "problem": return Theme.Danger;
                    case "missing_out":
                    case "queued": return Theme.Warning;
                    case "leave": return Theme.Leave;
                    default: return Theme.Neutral;
                }
            }
        }
    }

    public class BoardCounts
    {
        public int Total, Scheduled, InNow, Out, Late, NotInYet, Absent, OnLeave, MissingOut, Off;

        public static BoardCounts From(Dictionary<string, object> c)
        {
            if (c == null) return new BoardCounts();
            return new BoardCounts
            {
                Total = Json.Int(c, "total"),
                Scheduled = Json.Int(c, "scheduled"),
                InNow = Json.Int(c, "inNow"),
                Out = Json.Int(c, "out"),
                Late = Json.Int(c, "late"),
                NotInYet = Json.Int(c, "notInYet"),
                Absent = Json.Int(c, "absent"),
                OnLeave = Json.Int(c, "onLeave"),
                MissingOut = Json.Int(c, "missingOut"),
                Off = Json.Int(c, "off"),
            };
        }
    }

    public class BoardData
    {
        public string Date;
        public bool IsToday;
        public string Holiday;
        public BoardCounts Counts = new BoardCounts();
        public List<BoardRow> Rows = new List<BoardRow>();

        public static BoardData From(Dictionary<string, object> result)
        {
            Dictionary<string, object> d = Json.Data(result);
            if (d == null) return null;
            var b = new BoardData();
            b.Date = Json.Str(d, "date");
            b.IsToday = Json.Bool(d, "isToday");
            b.Holiday = Json.Str(d, "holiday");
            b.Counts = BoardCounts.From(Json.Obj(d, "counts"));
            foreach (Dictionary<string, object> row in Json.Rows(d, "rows")) b.Rows.Add(BoardRow.From(row));
            return b;
        }
    }
}
