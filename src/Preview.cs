using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // Developer aid: DollarCoPos.exe /render <folder>
    //
    // Builds each window with sample data (no reader, no server), places it far
    // off-screen and fully transparent for the moment it takes to draw, and
    // saves a picture of it -- so the layout can be checked on a build machine
    // without anyone looking at a screen. Not used by the running app.
    internal static class Preview
    {
        public static int Run(string folder)
        {
            Directory.CreateDirectory(folder);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var config = new AppConfig { ApiBaseUrl = "http://localhost:5000/", DeviceName = "Counter PC" };
            var api = new ApiClient(config.ApiBaseUrl);
            var user = new SessionUser { Id = "u1", Name = "Usman", Email = "usman@dollarco.pk", CanView = true, CanPunch = true, CanEnroll = true };
            BoardData board = SampleBoard();

            using (var login = new LoginForm(api, config, "usman@dollarco.pk")) { Open(login); Snap(login, folder, "1-sign-in.png"); }
            using (var settings = new SettingsForm(config)) { Open(settings); Snap(settings, folder, "2-settings.png"); }

            using (var main = new MainForm(api, user, config, false))
            {
                main.PreviewOnly = true;
                main.WindowState = FormWindowState.Normal;
                main.ClientSize = new Size(Theme.Px(1280), Theme.Px(760));
                Open(main);
                main.PreviewState(board, true, false, null, null);
                main.PreviewReader.ShowIdle(PulseState.Ready, "Place your finger on the reader", "to clock in or clock out", Theme.TextPrimary);
                Snap(main, folder, "3-counter-ready.png");

                main.PreviewReader.ShowResult("Ali Khan", "Cashier", null, "IN", "10:12 am", "12 min late", Theme.Warning,
                    "Clocked in 10:12 am — 12 min late");
                Snap(main, folder, "4-counter-late.png");

                main.PreviewReader.ShowResult("Sana Bibi", "Floor staff", null, "OUT", "6:05 pm", "worked 7 h 35 m", Theme.Info,
                    "Clocked out 6:05 pm — worked 7 h 35 m");
                Snap(main, folder, "5-counter-out.png");

                main.PreviewState(board, true, true, null, null);
                main.PreviewReader.ShowIdle(PulseState.Ready, "Place your finger on the reader", "to clock in or clock out", Theme.TextPrimary);
                Snap(main, folder, "6-counter-offline.png");

                main.PreviewState(board, false, false, "Fingerprint reader not connected",
                    "Attendance can't be marked until it is back. Plug the reader into a USB port on this PC (not a hub) "
                    + "— this message goes away by itself as soon as the reader is connected.");
                Snap(main, folder, "7-counter-no-reader.png");
                main.Close();
            }
            Sounds.Shutdown();

            var people = new List<StaffMember>();
            foreach (string[] p in new[]
            {
                new[] { "Ali Khan", "Cashier", "right_index" },
                new[] { "Bilal Ahmed", "Cashier", "" },
                new[] { "Sana Bibi", "Floor staff", "left_index" },
                new[] { "Usman Tariq", "Manager", "" },
            })
            {
                var m = new StaffMember { Id = Guid.NewGuid().ToString("N"), Name = p[0], Designation = p[1], Code = "EMP-2026-0000" + people.Count };
                if (p[2].Length > 0)
                {
                    m.Enrolled.Add(new FingerInfo { Finger = p[2], EnrolledAt = DateTime.UtcNow.AddDays(-3), DeviceName = "Counter PC" });
                    m.FingerCount = 1;
                }
                people.Add(m);
            }
            using (var enrol = new EnrollForm(api, user, new StaffDirectory(people), TemplateSet.Empty, "Counter PC"))
            {
                Open(enrol);
                Snap(enrol, folder, "8-enrol-sign-in.png");
                enrol.PreviewEnrolPage(0, "right_thumb");
                Snap(enrol, folder, "9-enrol-pick-finger.png");
            }
            Console.WriteLine("Saved previews to " + folder);
            return 0;
        }

        // Off-screen and transparent: nothing appears on the display.
        private static void Open(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
        }

        private static void Snap(Form form, string folder, string name)
        {
            form.PerformLayout();
            Application.DoEvents();
            using (var bmp = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(Path.Combine(folder, name), ImageFormat.Png);
            }
        }

        private static BoardData SampleBoard()
        {
            string json = "{\"data\":{\"date\":\"2026-10-19\",\"isToday\":true,\"counts\":{\"inNow\":3,\"late\":1,\"notInYet\":1,\"absent\":1,\"onLeave\":1,\"out\":1},\"rows\":["
                + Row("Ali Khan", "Cashier", "in", "2026-10-19T05:12:00Z", null, 12)
                + "," + Row("Bilal Ahmed", "Cashier", "in", "2026-10-19T04:55:00Z", null, 0)
                + "," + Row("Usman Tariq", "Manager", "in", "2026-10-19T04:40:00Z", null, 0)
                + "," + Row("Hina Noor", "Floor staff", "not_in", null, null, 0)
                + "," + Row("Sana Bibi", "Floor staff", "out", "2026-10-19T04:58:00Z", "2026-10-19T13:05:00Z", 0)
                + "," + Row("Kashif Ali", "Store keeper", "absent", null, null, 0)
                + ",{\"employeeId\":\"z\",\"name\":\"Zara Malik\",\"designation\":\"Cashier\",\"shift\":{\"type\":\"leave\",\"leaveType\":\"sick\"},\"state\":\"leave\",\"status\":\"leave\"}"
                + "]}}";
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            return BoardData.From(d);
        }

        private static string Row(string name, string designation, string state, string checkIn, string checkOut, int late)
        {
            return "{\"employeeId\":\"" + name.GetHashCode() + "\",\"name\":\"" + name + "\",\"designation\":\"" + designation + "\","
                + "\"shift\":{\"type\":\"shift\",\"name\":\"Morning\",\"start\":\"2026-10-19T05:00:00Z\",\"end\":\"2026-10-19T13:00:00Z\"},"
                + "\"checkIn\":" + (checkIn != null ? "\"" + checkIn + "\"" : "null") + ",\"checkOut\":" + (checkOut != null ? "\"" + checkOut + "\"" : "null")
                + ",\"state\":\"" + state + "\",\"status\":\"" + (late > 0 ? "late" : "present") + "\",\"lateMinutes\":" + late + "}";
        }
    }
}
