using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DollarCoAttendance
{
    // Developer checks that need no window and no fingerprint reader:
    //
    //   DollarCoPos.exe /selftest [results.txt]
    //       shop-time wording, JSON parsing of server answers, template sync
    //       merging, the https rule, DPAPI offline queue + saved sign-in on
    //       this Windows account, and that the fingerprint engine DLLs load.
    //
    //   DollarCoPos.exe /apitest <server> <email> <password> [results.txt]
    //       the app's own HTTP code against a TEST server (never the shop's
    //       live one -- it records punches): sign-in, staff list, template
    //       sync, live + offline punches, clientId retry, board, renew,
    //       enrol + remove.
    //
    // Exit code 0 = all passed. Results go to the file (and stdout when redirected).
    internal static class SelfTest
    {
        private static readonly StringBuilder output = new StringBuilder();
        private static int failures;
        private static int passes;

        public static int Run(string[] args)
        {
            string outFile = null;
            try
            {
                if (args[0] == "/apitest")
                {
                    if (args.Length < 4) { Say("usage: /apitest <server> <email> <password> [results.txt]"); return 2; }
                    outFile = args.Length > 4 ? args[4] : null;
                    ApiTest(args[1], args[2], args[3]);
                }
                else
                {
                    outFile = args.Length > 1 ? args[1] : null;
                    OfflineTests();
                }
            }
            catch (Exception ex)
            {
                Check(false, "unexpected error: " + ex);
            }
            Say(failures == 0 ? "ALL PASSED (" + passes + ")" : failures + " FAILED, " + passes + " passed");
            try { if (outFile != null) File.WriteAllText(outFile, output.ToString(), Encoding.UTF8); } catch { }
            return failures == 0 ? 0 : 1;
        }

        private static void Say(string line)
        {
            output.AppendLine(line);
            try { Console.WriteLine(line); } catch { }
        }

        private static void Check(bool ok, string what)
        {
            if (ok) passes++; else failures++;
            Say((ok ? "  ok   - " : "  FAIL - ") + what);
        }

        private static Dictionary<string, object> Parse(string json)
        {
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            d["http_status"] = 200;
            return d;
        }

        private static DateTime Utc(string iso) { return ShopTime.ParseUtc(iso).Value; }

        // ------------------------------------------------------------ no server

        private static void OfflineTests()
        {
            Say(AppInfo.ClientId + " self-test");

            // Shop time (PKT = UTC+5), whatever this PC's zone.
            Check(ShopTime.Clock(Utc("2026-10-19T04:58:00Z")) == "9:58 am", "9:58 am PKT");
            Check(ShopTime.Clock(Utc("2026-10-19T13:05:00Z")) == "6:05 pm", "6:05 pm PKT");
            Check(ShopTime.Clock(Utc("2026-10-18T19:00:00Z")) == "12:00 am", "midnight is 12:00 am");
            Check(ShopTime.DayKey(Utc("2026-10-18T19:30:00Z")) == "2026-10-19", "00:30 PKT belongs to the next day");
            Check(ShopTime.Duration(455) == "7 h 35 m" && ShopTime.Duration(12) == "12 min" && ShopTime.Duration(60) == "1 h", "durations");
            Check(ShopTime.ToIso(Utc("2026-10-19T04:58:00.000Z")) == "2026-10-19T04:58:00.000Z", "ISO round trip");
            Check(ShopTime.DayLabel("2026-10-19") == "Mon 19 Oct", "day label");

            // A punch answer.
            PunchResult late = PunchResult.From(Parse("{\"success\":true,\"data\":{\"kind\":\"in\",\"at\":\"2026-10-19T05:12:00.000Z\","
                + "\"name\":\"Ali Khan\",\"status\":\"late\",\"lateMinutes\":12,\"workedMinutes\":0,\"duplicate\":false,\"shiftName\":\"Morning\","
                + "\"message\":\"Clocked in 10:12 am — 12 min late\"}}"));
            Check(late != null && late.Headline == "IN" && late.Detail == "12 min late" && late.Accent == Theme.Warning
                && ShopTime.Clock(late.At.Value) == "10:12 am", "late punch: IN 10:12 am · 12 min late (amber)");
            PunchResult onTime = PunchResult.From(Parse("{\"data\":{\"kind\":\"in\",\"at\":\"2026-10-19T04:58:00Z\",\"status\":\"present\","
                + "\"lateMinutes\":0,\"shiftName\":\"Morning\"}}"));
            Check(onTime.Detail == "on time" && onTime.Accent == Theme.Success, "on-time punch is green");
            PunchResult outP = PunchResult.From(Parse("{\"data\":{\"kind\":\"out\",\"at\":\"2026-10-19T13:05:00Z\",\"workedMinutes\":455,\"status\":\"present\"}}"));
            Check(outP.Headline == "OUT" && outP.Detail == "worked 7 h 35 m" && outP.Accent == Theme.Info, "out punch: OUT · worked 7 h 35 m (blue)");
            PunchResult dup = PunchResult.From(Parse("{\"data\":{\"kind\":\"in\",\"duplicate\":true}}"));
            Check(dup.Headline == "ALREADY IN" && dup.Speech("Ali") == "Ali, already marked.", "double tap");
            Check(late.Speech("Ali") == "Ali, clocked in, 12 minutes late.", "spoken words");

            // The board.
            BoardData b = BoardData.From(Parse("{\"data\":{\"date\":\"2026-10-19\",\"isToday\":true,\"counts\":{\"inNow\":1,\"late\":1,\"notInYet\":1,"
                + "\"absent\":0,\"onLeave\":1,\"out\":1},\"rows\":[{\"employeeId\":\"a\",\"name\":\"Ali\",\"designation\":\"Cashier\","
                + "\"shift\":{\"type\":\"shift\",\"name\":\"Morning\",\"start\":\"2026-10-19T05:00:00Z\",\"end\":\"2026-10-19T13:00:00Z\"},"
                + "\"checkIn\":\"2026-10-19T05:12:00Z\",\"checkOut\":null,\"state\":\"in\",\"status\":\"late\",\"lateMinutes\":12},"
                + "{\"employeeId\":\"b\",\"name\":\"Sana\",\"shift\":{\"type\":\"leave\",\"leaveType\":\"sick\"},\"state\":\"leave\",\"status\":\"leave\"}]}}"));
            Check(b != null && b.Rows.Count == 2 && b.Counts.InNow == 1 && b.Counts.OnLeave == 1, "board rows and counts");
            Check(b.Rows[0].Pill == "IN · 12 MIN LATE" && b.Rows[0].TimesText == "since 10:12 am" && b.Rows[0].ShiftText == "10:00 am – 6:00 pm",
                "board row: pill, since, shift (" + b.Rows[0].Pill + " / " + b.Rows[0].TimesText + " / " + b.Rows[0].ShiftText + ")");
            Check(b.Rows[1].ShiftText == "Sick leave" && b.Rows[1].Pill == "ON LEAVE", "leave row");

            // Template sync merge.
            var before = new TemplateSet(new List<TemplateCandidate>
            {
                new TemplateCandidate { EmployeeId = "A", Finger = "right_index", TemplateBase64 = "AAAA" },
                new TemplateCandidate { EmployeeId = "B", Finger = "right_index", TemplateBase64 = "BBBB" },
                new TemplateCandidate { EmployeeId = "E", Finger = "left_index", TemplateBase64 = "EEEE" },
            });
            bool needFull;
            TemplateSet after = TemplateStore.Merge(before, Parse("{\"templates\":[{\"employeeId\":\"C\",\"finger\":\"left_thumb\",\"template\":\"CCCC\"},"
                + "{\"employeeId\":\"A\",\"finger\":\"right_index\",\"template\":\"AAA2\"}],\"removed\":[{\"employeeId\":\"B\",\"finger\":\"right_index\"}],"
                + "\"employeeIds\":[\"A\",\"C\"]}"), out needFull);
            var keys = new List<string>();
            foreach (TemplateCandidate c in after.Candidates) keys.Add(c.Key() + "=" + c.TemplateBase64);
            Check(string.Join(",", keys.ToArray()) == "A|right_index=AAA2,C|left_thumb=CCCC" && !needFull,
                "incremental merge: re-scan, new, removed, deactivated (" + string.Join(",", keys.ToArray()) + ")");
            TemplateStore.Merge(before, Parse("{\"templates\":[],\"removed\":[],\"employeeIds\":[\"A\",\"B\",\"E\",\"D\"]}"), out needFull);
            Check(needFull, "an employee with fingers this PC never had asks for a full download");

            // Addresses.
            Check(AppConfig.InsecureAddressProblem("http://example.com/") != null, "http:// to another machine is refused");
            Check(AppConfig.InsecureAddressProblem("http://localhost:5000/") == null && AppConfig.InsecureAddressProblem(AppConfig.LiveServer) == null,
                "localhost and the live https server are allowed");

            // Sales button: the POS screen address and the server's feature switch.
            Check(AppConfig.PosUrlProblem(AppConfig.LivePosUrl) == null && AppConfig.PosUrlProblem("http://localhost:3000/admin/pos") == null
                && AppConfig.PosUrlProblem("http://example.com/admin/pos") != null && AppConfig.PosUrlProblem("not a url") != null,
                "POS screen address: live https and localhost allowed, plain http elsewhere and junk refused");
            Check(new AppConfig().PosUrl == AppConfig.LivePosUrl, "a config without posUrl opens the live POS screen");
            SessionUser oldServer = SessionUser.From(Json.Data(Parse("{\"data\":{\"user\":{\"id\":\"u\"},\"canView\":true}}")));
            SessionUser noSales = SessionUser.From(Json.Data(Parse("{\"data\":{\"user\":{\"id\":\"u\"},\"features\":{\"attendance\":true,\"sales\":false}}}")));
            SessionUser sales = SessionUser.From(Json.Data(Parse("{\"data\":{\"user\":{\"id\":\"u\"},\"features\":{\"attendance\":true,\"sales\":true}}}")));
            Check(oldServer.SalesEnabled && !noSales.SalesEnabled && sales.SalesEnabled,
                "Sales button: shown without features (older server), follows features.sales otherwise");
            Check(!SessionUser.From(noSales.ToDictionary()).SalesEnabled, "the offline copy of the sign-in keeps features.sales");

            // JWT expiry claim.
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"id\":\"x\",\"exp\":1792800000}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            DateTime? exp = SignInOutcome.TokenExpiry("eyJhbGciOiJIUzI1NiJ9." + payload + ".sig");
            Check(exp.HasValue && exp.Value == new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc), "JWT exp read");

            // DPAPI files on this Windows account, in a throwaway scope.
            string scope = "https://selftest-" + Guid.NewGuid().ToString("N") + ".invalid/";
            OfflineStore.UseScope(scope);
            TokenStore.UseScope(scope);
            try
            {
                OfflineStore.Enqueue(new QueuedPunch { ClientId = "c2", EmployeeId = "e2", EmployeeName = "Bilal", ScannedAtUtc = Utc("2026-10-19T05:10:00Z") });
                OfflineStore.Enqueue(new QueuedPunch { ClientId = "c1", EmployeeId = "e1", EmployeeName = "Ali", ScannedAtUtc = Utc("2026-10-19T05:00:00Z") });
                bool again = OfflineStore.Enqueue(new QueuedPunch { ClientId = "c1", EmployeeId = "e1", ScannedAtUtc = DateTime.UtcNow });
                Check(!again && OfflineStore.QueueCount == 2, "queue: one entry per clientId");
                byte[] onDisk = File.ReadAllBytes(OfflineStore.CurrentQueuePath);
                Check(Encoding.UTF8.GetString(onDisk).IndexOf("Bilal", StringComparison.Ordinal) < 0, "queue file is encrypted (DPAPI)");
                OfflineStore.ForgetQueueInMemory();
                QueuedPunch first = OfflineStore.Peek();
                Check(OfflineStore.QueueCount == 2 && first.ClientId == "c1" && first.ScannedAtUtc == Utc("2026-10-19T05:00:00Z"),
                    "queue survives a restart, oldest first");
                OfflineStore.Remove("c1");
                OfflineStore.Remove("c2");
                Check(OfflineStore.QueueCount == 0 && !File.Exists(OfflineStore.CurrentQueuePath), "empty queue leaves no file");

                var set = new TemplateSet(new List<TemplateCandidate> { new TemplateCandidate { EmployeeId = "A", Finger = "right_index", TemplateBase64 = "QUJD" } });
                OfflineStore.SaveTemplates(set, "2026-10-19T05:00:00.000Z");
                string time;
                TemplateSet loaded = OfflineStore.LoadTemplates(out time);
                Check(loaded != null && loaded.Count == 1 && time == "2026-10-19T05:00:00.000Z", "fingerprint copy round trip");

                TokenStore.Save("token-abc", DateTime.UtcNow.AddDays(7));
                StoredToken token = TokenStore.Load();
                Check(token != null && token.Token == "token-abc" && !token.DueForRenewal, "saved sign-in round trip");
                TokenStore.Clear();
                Check(TokenStore.Load() == null, "sign-out forgets the token");
            }
            finally
            {
                try { Directory.Delete(OfflineStore.Folder, true); } catch { }
            }

            // Sounds.
            byte[] wave = Tone.Tune(new[] { 440 }, 50, 0);
            Check(wave.Length > 44 && wave[0] == 'R' && wave[8] == 'W', "built-in tone is a WAV");

            // The fingerprint engine (DLLs beside the exe). The reader itself is
            // not needed and not tested here.
            string engine = FingerprintMatcher.SelfTest();
            Check(engine == null, "fingerprint engine loads" + (engine != null ? " (" + engine + ")" : string.Empty));
            try
            {
                var capture = new DPFP.Capture.Capture();
                GC.KeepAlive(capture);
                Say("  info - reader driver (DigitalPersona runtime) is installed on this PC");
            }
            catch (Exception ex)
            {
                Say("  info - reader driver not installed on this PC (" + ex.GetType().Name + ") -- the counter would show \"reader software not installed\"");
            }
        }

        // ------------------------------------------------------------ against a test server

        private static void ApiTest(string server, string email, string password)
        {
            Say(AppInfo.ClientId + " API test against " + server);
            var api = new ApiClient(server);

            SignInOutcome bad = SignInOutcome.Attempt(api, email, password + "x", false);
            Check(bad.User == null && bad.Message == "Wrong email or password.", "wrong password refused");

            SignInOutcome signIn = SignInOutcome.Attempt(api, email, password, false);
            Check(signIn.User != null && signIn.User.CanRunDesk, "desk sign-in (" + (signIn.Message ?? "ok") + ")");
            if (signIn.User == null) return;
            Check(signIn.ExpiresAtUtc > DateTime.UtcNow.AddDays(6), "token expiry read from the JWT (" + ShopTime.ToIso(signIn.ExpiresAtUtc) + ")");
            api.Token = signIn.Token;
            Check(ShopTime.OffsetKnown && Math.Abs(ShopTime.ServerOffset.TotalSeconds) < 5, "server clock read from the Date header");

            Dictionary<string, object> result;
            StaffDirectory staff = StaffDirectory.Load(api, out result);
            Check(staff != null && staff.Count >= 3, "staff list (" + (staff != null ? staff.Count : -1) + ")");
            if (staff == null) return;
            StaffMember ali = null, bilal = null, zara = null;
            foreach (StaffMember m in staff.All)
            {
                if (m.Name.StartsWith("Ali")) ali = m;
                if (m.Name.StartsWith("Bilal")) bilal = m;
                if (m.Name.StartsWith("Zara")) zara = m;
            }
            Check(ali != null && ali.FingerCount == 1 && ali.Enrolled[0].Finger == "right_index" && zara != null && zara.FingerCount == 0,
                "fingers on file per employee");

            var store = new TemplateStore();
            bool changed;
            Dictionary<string, object> problem = store.Sync(api, true, out changed);
            Check(problem == null && changed && store.Current.Count == 2 && store.Current.PeopleCount == 2, "full template download (" + store.Current.Count + ")");
            problem = store.Sync(api, false, out changed);
            Check(problem == null && !changed && store.Current.Count == 2, "incremental sync with nothing new");

            // Live punch, then the same clientId again (a lost answer, sent twice).
            string clientId = PunchApi.NewClientId();
            PunchResult p = PunchResult.From(PunchApi.SendLive(api, ali.Id, clientId, "Self-test PC"));
            Check(p != null && p.Kind == "in" && !p.Repeated && p.Message.StartsWith("Clocked in "), "live punch in: " + (p != null ? p.Message : "none"));
            PunchResult again = PunchResult.From(PunchApi.SendLive(api, ali.Id, clientId, "Self-test PC"));
            Check(again != null && again.Repeated && again.Kind == "in" && again.Message == p.Message, "same clientId returns the first answer");

            // Offline punch, 40 minutes ago on this PC's clock.
            var queued = new QueuedPunch { ClientId = PunchApi.NewClientId(), EmployeeId = bilal.Id, EmployeeName = bilal.Name, ScannedAtUtc = DateTime.UtcNow.AddMinutes(-40) };
            result = PunchApi.SendQueued(api, queued, "Self-test PC");
            PunchResult q = PunchResult.From(result);
            Check(PunchApi.QueuedOutcome(result) == true && q.Offline && Math.Abs((q.At.Value - queued.ScannedAtUtc).TotalSeconds) < 5,
                "offline punch filed at its scan time (" + (q != null ? q.Message : Json.Message(result)) + ")");
            result = PunchApi.SendQueued(api, queued, "Self-test PC");
            Check(PunchApi.QueuedOutcome(result) == true && PunchResult.From(result).Repeated, "queue sent twice records once");
            var old = new QueuedPunch { ClientId = PunchApi.NewClientId(), EmployeeId = bilal.Id, ScannedAtUtc = DateTime.UtcNow.AddHours(-49) };
            result = PunchApi.SendQueued(api, old, "Self-test PC");
            Check(PunchApi.QueuedOutcome(result) == false && Json.Code(result) == "PUNCH_TOO_OLD", "offline punch over 48 h is refused (dropped from the queue)");
            result = PunchApi.SendLive(api, zara.Id, PunchApi.NewClientId(), "Self-test PC");
            Check(!Json.Ok(result) && Json.Code(result) == "NO_FINGERPRINT", "employee without a finger cannot be punched");

            BoardData board = BoardData.From(api.Get("api/desktop/board"));
            BoardRow aliRow = null;
            if (board != null) foreach (BoardRow r in board.Rows) if (r.EmployeeId == ali.Id) aliRow = r;
            Check(board != null && board.IsToday && aliRow != null && aliRow.State == "in" && board.Counts.InNow >= 1, "board shows Ali in");

            // Renew the sign-in.
            Dictionary<string, object> renew = api.Post("api/desktop/session/renew", new Dictionary<string, object>());
            string fresh = Json.Str(Json.Data(renew), "token");
            Check(Json.Ok(renew) && !string.IsNullOrEmpty(fresh) && Json.Ok(api.WithToken(fresh).Get("api/desktop/session")), "token renewed");

            // Enrolment needs a manager sign-in; then add and remove a finger for Zara.
            SignInOutcome mgr = SignInOutcome.Attempt(api, email, password, true);
            Check(mgr.User != null && mgr.User.CanEnroll, "manager sign-in for enrolment");
            if (mgr.User == null) return;
            ApiClient managerApi = api.WithToken(mgr.Token);
            var body = new Dictionary<string, object>();
            body["finger"] = "left_index";
            body["template"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('z', 300)));
            body["quality"] = 100;
            body["deviceName"] = "Self-test PC";
            result = managerApi.Post("api/desktop/fingerprints/" + zara.Id, body);
            Check(Json.HttpStatus(result) == 201, "finger enrolled (" + Json.Message(result) + ")");
            problem = store.Sync(api, false, out changed);
            Check(problem == null && changed && store.Current.Count == 3, "incremental sync picks up the new finger");
            result = managerApi.Delete("api/desktop/fingerprints/" + zara.Id + "/left_index");
            Check(Json.Ok(result), "finger removed (" + Json.Message(result) + ")");
            problem = store.Sync(api, false, out changed);
            Check(problem == null && changed && store.Current.Count == 2, "incremental sync drops the removed finger");
        }
    }
}
