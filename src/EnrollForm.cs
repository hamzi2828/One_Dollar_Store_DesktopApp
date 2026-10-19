using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // Enrolling fingerprints, for a manager only.
    //
    //   1. A manager signs in (email + password) -- even on a counter that is
    //      already signed in, so a cashier cannot put their own finger on
    //      somebody else's name. The account needs Employees "edit" or
    //      Attendance "edit" (the server checks again on every save).
    //   2. Pick the employee (the list comes from the POS; employees are added
    //      in the admin panel).
    //   3. Pick the finger (up to 2 per employee), then four scans of it on the
    //      reader. The scans are fused into one template HERE; only the finished
    //      template goes to the server.
    //
    // Before saving, the new finger is matched against every finger already on
    // file: one finger can belong to only one person, or the reader would refuse
    // both of them for good.
    public class EnrollForm : Form, DPFP.Capture.EventHandler
    {
        private const int RequiredScans = 4;

        private readonly ApiClient deskApi;
        private readonly TemplateSet knownTemplates;
        private readonly string deviceName;
        private ApiClient managerApi;
        private SessionUser manager;

        private StaffDirectory directory;
        private StaffMember selected;
        private string selectedFinger;
        private readonly List<TemplateCandidate> linkedHere = new List<TemplateCandidate>();
        private readonly HashSet<string> removedHere = new HashSet<string>();

        private DPFP.Capture.Capture capturer;
        private List<DPFP.FeatureSet> acceptedScans = new List<DPFP.FeatureSet>();
        private List<DPFP.FeatureSet> checkScans = new List<DPFP.FeatureSet>();
        private int attempts;
        private volatile bool capturing;
        private volatile bool processing;
        private volatile bool busy;

        private volatile bool changedAny;
        // Read by the main window after the dialog closes: pull the fingerprint list again.
        public bool ChangedAny { get { return changedAny; } }

        // ---- page 1: manager sign-in ----
        private Panel signInPage;
        private TextBox emailBox;
        private TextBox passwordBox;
        private RoundButton continueButton;
        private Label signInStatus;

        // ---- page 2: enrol ----
        private Panel enrolPage;
        private TextBox searchBox;
        private ListBox peopleList;
        private Label managerLabel;
        private Avatar personAvatar;
        private Label personName;
        private Label personMeta;
        private Label onFileTitle;
        private readonly List<Label> onFileLabels = new List<Label>();
        private readonly List<RoundButton> removeButtons = new List<RoundButton>();
        private Label addTitle;
        private readonly Dictionary<string, RoundButton> fingerButtons = new Dictionary<string, RoundButton>();
        private RoundButton startButton;
        private SegmentBar progressBar;
        private Label progressLabel;
        private AlertBox statusBox;
        private RoundButton closeButton;

        public EnrollForm(ApiClient deskApi, SessionUser deskUser, StaffDirectory directory, TemplateSet knownTemplates, string deviceName)
        {
            this.deskApi = deskApi;
            this.directory = directory ?? StaffDirectory.Empty;
            this.knownTemplates = knownTemplates;
            this.deviceName = deviceName;
            BuildUi(deskUser != null && deskUser.CanEnroll ? deskUser.Email : string.Empty);
        }

        private static int P(int v) { return Theme.Px(v); }

        private void BuildUi(string emailHint)
        {
            Theme.PrepareForDpi(this);
            SuspendLayout();
            Text = AppInfo.Name + " — Enroll fingerprints";
            ClientSize = new Size(P(960), P(700));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            var header = new HeaderBand("Enroll fingerprints", "For managers: up to 2 fingers per employee", false);
            header.SetBounds(0, 0, ClientSize.Width, P(70));
            Controls.Add(header);

            BuildSignInPage(emailHint);
            BuildEnrolPage();

            closeButton = Theme.QuietButton("Close", Theme.TextSecondary);
            closeButton.SetBounds(ClientSize.Width - P(24) - P(140), ClientSize.Height - P(24) - P(42), P(140), P(42));
            closeButton.Click += delegate { Close(); };
            Controls.Add(closeButton);

            ResumeLayout(false);
            Theme.FitToScreen(this);

            Load += delegate { (emailBox.Text.Length > 0 ? passwordBox : emailBox).Focus(); };
            // The search box only gets a window once its page is shown.
            searchBox.HandleCreated += delegate { Theme.SetPlaceholder(searchBox, "Search name or code..."); };
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (processing && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    ShowStatus("Still saving the fingerprint — this window can close once the server has answered.", Theme.Brand);
                    return;
                }
                StopCapture();
            };
        }

        private void BuildSignInPage(string emailHint)
        {
            signInPage = new Panel();
            signInPage.BackColor = Theme.Background;
            signInPage.SetBounds(0, P(70), ClientSize.Width, ClientSize.Height - P(70) - P(74));
            int width = P(420), x = (ClientSize.Width - width) / 2;

            var title = Theme.Label("Manager sign-in", Theme.FontTitle, Theme.TextPrimary);
            title.SetBounds(x, P(40), width, P(36));
            var hint = Theme.Label("Only a manager can enroll or remove fingerprints. Sign in with your POS email and password.",
                Theme.FontSmall, Theme.TextSecondary);
            hint.SetBounds(x, P(80), width, P(40));

            var emailLabel = Theme.Label("EMAIL", Theme.FontLabel, Theme.TextSecondary);
            emailLabel.SetBounds(x, P(132), width, P(16));
            emailBox = Theme.TextBox();
            emailBox.Text = emailHint ?? string.Empty;
            FieldFrame emailFrame = FieldFrame.Wrap(emailBox, x, P(150), width, P(42));

            var passLabel = Theme.Label("PASSWORD", Theme.FontLabel, Theme.TextSecondary);
            passLabel.SetBounds(x, P(204), width, P(16));
            passwordBox = Theme.TextBox();
            passwordBox.UseSystemPasswordChar = true;
            FieldFrame passFrame = FieldFrame.Wrap(passwordBox, x, P(222), width, P(42));

            continueButton = Theme.PrimaryButton("Continue");
            continueButton.SetBounds(x, P(286), width, P(48));
            continueButton.Click += Continue_Click;

            signInStatus = Theme.Label(string.Empty, Theme.FontSmall, Theme.Danger);
            signInStatus.TextAlign = ContentAlignment.TopCenter;
            signInStatus.SetBounds(x, P(344), width, P(70));

            signInPage.Controls.Add(title);
            signInPage.Controls.Add(hint);
            signInPage.Controls.Add(emailLabel);
            signInPage.Controls.Add(emailFrame);
            signInPage.Controls.Add(passLabel);
            signInPage.Controls.Add(passFrame);
            signInPage.Controls.Add(continueButton);
            signInPage.Controls.Add(signInStatus);
            Controls.Add(signInPage);
            AcceptButton = continueButton;
        }

        private void BuildEnrolPage()
        {
            enrolPage = new Panel();
            enrolPage.BackColor = Theme.Background;
            enrolPage.SetBounds(0, P(70), ClientSize.Width, ClientSize.Height - P(70) - P(74));
            enrolPage.Visible = false;

            int pad = P(24);
            int listWidth = P(330);

            // ---- left: who ----
            var whoLabel = Theme.Label("1. EMPLOYEE", Theme.FontLabel, Theme.Brand);
            whoLabel.SetBounds(pad, P(16), listWidth, P(16));
            searchBox = Theme.TextBox();
            FieldFrame searchFrame = FieldFrame.Wrap(searchBox, pad, P(36), listWidth, P(40));
            searchBox.TextChanged += delegate { FillPeople(); };

            peopleList = new ListBox();
            peopleList.BorderStyle = BorderStyle.None;
            peopleList.DrawMode = DrawMode.OwnerDrawFixed;
            peopleList.ItemHeight = P(50);
            peopleList.IntegralHeight = false;
            peopleList.BackColor = Theme.CardColor;
            peopleList.ForeColor = Theme.TextPrimary;
            peopleList.SetBounds(pad, P(84), listWidth, enrolPage.Height - P(84) - P(8));
            peopleList.DrawItem += People_DrawItem;
            peopleList.SelectedIndexChanged += delegate
            {
                if (capturing || processing) return;
                SelectPerson(peopleList.SelectedItem as StaffMember);
            };
            peopleList.HandleCreated += delegate { Theme.DarkScrollbars(peopleList); };

            // ---- right: their fingers ----
            int x = pad + listWidth + P(28);
            int width = ClientSize.Width - x - pad;

            managerLabel = Theme.Label(string.Empty, Theme.FontSmall, Theme.TextMuted);
            managerLabel.TextAlign = ContentAlignment.MiddleRight;
            managerLabel.SetBounds(x, P(14), width, P(18));

            personAvatar = new Avatar();
            personAvatar.SetBounds(x, P(36), P(72), P(72));
            personName = Theme.Label("Pick an employee on the left", Theme.FontHeadline, Theme.TextPrimary);
            personName.AutoEllipsis = true;
            personName.SetBounds(x + P(86), P(44), width - P(86), P(30));
            personMeta = Theme.Label(string.Empty, Theme.FontSmall, Theme.TextSecondary);
            personMeta.SetBounds(x + P(86), P(76), width - P(86), P(22));

            onFileTitle = Theme.Label("FINGERS ON FILE", Theme.FontLabel, Theme.TextSecondary);
            onFileTitle.SetBounds(x, P(122), width, P(16));
            for (int i = 0; i < 2; i++)
            {
                var label = Theme.Label(string.Empty, Theme.FontBody, Theme.TextPrimary);
                label.SetBounds(x, P(144 + i * 40), width - P(120), P(34));
                label.TextAlign = ContentAlignment.MiddleLeft;
                var remove = Theme.QuietButton("Remove", Theme.Danger);
                remove.SetBounds(x + width - P(110), P(144 + i * 40), P(110), P(34));
                remove.Visible = false;
                int index = i;
                remove.Click += delegate { RemoveFinger(index); };
                onFileLabels.Add(label);
                removeButtons.Add(remove);
                enrolPage.Controls.Add(label);
                enrolPage.Controls.Add(remove);
            }

            addTitle = Theme.Label("2. FINGER (pick one, then scan it 4 times)", Theme.FontLabel, Theme.Brand);
            addTitle.SetBounds(x, P(236), width, P(16));
            // Two hands as two rows of five buttons -- touch-friendly, and a
            // finger already on file says so.
            int buttonWidth = (width - P(8) * 4) / 5;
            for (int i = 0; i < Fingers.Keys.Length; i++)
            {
                string key = Fingers.Keys[i];
                int row = i < 5 ? 0 : 1;
                int col = i % 5;
                var b = Theme.QuietButton((row == 0 ? "L · " : "R · ") + Fingers.ShortLabel(key), Theme.TextPrimary);
                b.Font = Theme.FontSmallBold;
                b.SetBounds(x + col * (buttonWidth + P(8)), P(256 + row * 58), buttonWidth, P(52));
                b.Click += delegate { PickFinger(key); };
                fingerButtons[key] = b;
                enrolPage.Controls.Add(b);
            }

            startButton = Theme.PrimaryButton("3. Start scanning");
            startButton.SetBounds(x, P(380), width, P(48));
            startButton.Enabled = false;
            startButton.Click += Start_Click;

            progressBar = new SegmentBar();
            progressBar.Total = RequiredScans;
            progressBar.SetBounds(x, P(440), width, P(10));
            progressLabel = Theme.Label("0 of " + RequiredScans + " scans", Theme.FontSmall, Theme.TextSecondary);
            progressLabel.TextAlign = ContentAlignment.TopCenter;
            progressLabel.SetBounds(x, P(454), width, P(18));

            statusBox = new AlertBox();
            statusBox.SetBounds(x, P(478), width, P(72));

            enrolPage.Controls.Add(whoLabel);
            enrolPage.Controls.Add(searchFrame);
            enrolPage.Controls.Add(peopleList);
            enrolPage.Controls.Add(managerLabel);
            enrolPage.Controls.Add(personAvatar);
            enrolPage.Controls.Add(personName);
            enrolPage.Controls.Add(personMeta);
            enrolPage.Controls.Add(onFileTitle);
            enrolPage.Controls.Add(addTitle);
            enrolPage.Controls.Add(startButton);
            enrolPage.Controls.Add(progressBar);
            enrolPage.Controls.Add(progressLabel);
            enrolPage.Controls.Add(statusBox);
            Controls.Add(enrolPage);

            SelectPerson(null);
        }

        // Developer preview (Preview.cs, /render): the second page with a list.
        internal void PreviewEnrolPage(int selectIndex, string finger)
        {
            managerLabel.Text = "Manager: Usman";
            signInPage.Visible = false;
            enrolPage.Visible = true;
            FillPeople();
            if (selectIndex >= 0 && selectIndex < peopleList.Items.Count) peopleList.SelectedIndex = selectIndex;
            if (finger != null) PickFinger(finger);
        }

        // ------------------------------------------------------------ sign-in

        private void Continue_Click(object sender, EventArgs e)
        {
            string email = emailBox.Text.Trim();
            string password = passwordBox.Text;
            if (email.Length == 0 || password.Length == 0)
            {
                signInStatus.ForeColor = Theme.Danger;
                signInStatus.Text = "Enter the manager's email and password.";
                return;
            }
            continueButton.Enabled = false;
            signInStatus.ForeColor = Theme.TextSecondary;
            signInStatus.Text = "Checking...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                SignInOutcome outcome;
                try { outcome = SignInOutcome.Attempt(deskApi, email, password, true); }
                catch (Exception ex) { Log.Error("Manager sign-in failed", ex); outcome = new SignInOutcome { Message = ex.Message }; }
                RunOnUi(delegate
                {
                    continueButton.Enabled = true;
                    passwordBox.Text = string.Empty;
                    if (outcome.User == null)
                    {
                        signInStatus.ForeColor = Theme.Danger;
                        signInStatus.Text = outcome.Message;
                        return;
                    }
                    // Kept in memory for this window only; never saved.
                    managerApi = deskApi.WithToken(outcome.Token);
                    manager = outcome.User;
                    Log.Info("Enrolment window opened by " + manager.Email);
                    managerLabel.Text = "Manager: " + (manager.Name ?? manager.Email);
                    signInPage.Visible = false;
                    enrolPage.Visible = true;
                    AcceptButton = null;
                    FillPeople();
                    searchBox.Focus();
                    ReloadPeople();
                });
            });
        }

        // ------------------------------------------------------------ people

        private void ReloadPeople()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                StaffDirectory fresh = null;
                Dictionary<string, object> result = null;
                try { fresh = StaffDirectory.Load(deskApi, out result); }
                catch (Exception ex) { Log.Error("Employee list could not be reloaded", ex); }
                RunOnUi(delegate
                {
                    if (fresh == null)
                    {
                        if (directory.Count == 0)
                            ShowStatus("The employee list could not be loaded: " + (Json.Message(result) ?? "no connection") + ".", Theme.Danger);
                        return;
                    }
                    string keep = selected != null ? selected.Id : null;
                    directory = fresh;
                    FillPeople();
                    if (keep != null && !capturing && !processing) SelectPerson(directory.Find(keep));
                });
            });
        }

        private void FillPeople()
        {
            string term = searchBox.Text.Trim();
            string keep = selected != null ? selected.Id : null;
            peopleList.BeginUpdate();
            peopleList.Items.Clear();
            foreach (StaffMember m in directory.All.Where(p => p.Matches(term))) peopleList.Items.Add(m);
            peopleList.EndUpdate();
            if (keep != null)
            {
                for (int i = 0; i < peopleList.Items.Count; i++)
                    if (((StaffMember)peopleList.Items[i]).Id == keep) { peopleList.SelectedIndex = i; break; }
            }
            if (directory.Count == 0) ShowStatus("No employees yet. Add them in the admin panel (HR → Employees) first.", Theme.Warning);
        }

        private void People_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= peopleList.Items.Count) return;
            var m = peopleList.Items[e.Index] as StaffMember;
            bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(isSelected ? Theme.BrandSoft : Theme.CardColor)) g.FillRectangle(bg, e.Bounds);
            if (m == null) return;
            if (isSelected)
                using (var rail = new SolidBrush(Theme.Brand)) g.FillRectangle(rail, e.Bounds.X, e.Bounds.Y + P(6), P(4), e.Bounds.Height - P(12));

            var circle = new Rectangle(e.Bounds.X + P(12), e.Bounds.Y + P(8), P(34), P(34));
            Draw.FillRounded(g, circle, P(17), Theme.BrandSoft);
            TextRenderer.DrawText(g, m.Initials, Theme.FontSmallBold, circle, Theme.Brand,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int left = circle.Right + P(10);
            int count = FingersOf(m).Count;
            string badge = count == 0 ? "NO FINGER" : count + (count == 1 ? " FINGER" : " FINGERS");
            Size badgeSize = TextRenderer.MeasureText(g, badge, Theme.FontPill);
            var badgeBox = new Rectangle(e.Bounds.Right - P(10) - badgeSize.Width - P(12), e.Bounds.Y + P(14), badgeSize.Width + P(12), P(20));
            Draw.FillRounded(g, badgeBox, P(10), count == 0 ? Theme.WarningSoft : Theme.SuccessSoft);
            TextRenderer.DrawText(g, badge, Theme.FontPill, badgeBox, count == 0 ? Theme.Warning : Theme.Success,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            int textWidth = Math.Max(0, badgeBox.Left - left - P(6));
            TextRenderer.DrawText(g, m.Name, Theme.FontSmallBold, new Rectangle(left, e.Bounds.Y + P(6), textWidth, P(20)),
                Theme.TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string meta = m.Designation + (m.Code.Length > 0 ? (m.Designation.Length > 0 ? "  ·  " : string.Empty) + m.Code : string.Empty);
            TextRenderer.DrawText(g, meta, Theme.FontListMeta, new Rectangle(left, e.Bounds.Y + P(26), textWidth, P(18)),
                Theme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // Fingers on file for a person as this window knows them: the server's
        // list, plus fingers saved here, minus fingers removed here.
        private List<string> FingersOf(StaffMember m)
        {
            var fingers = new List<string>();
            if (m == null) return fingers;
            foreach (FingerInfo f in m.Enrolled)
                if (!removedHere.Contains(m.Id + "|" + f.Finger) && !fingers.Contains(f.Finger)) fingers.Add(f.Finger);
            foreach (TemplateCandidate c in linkedHere)
                if (c.EmployeeId == m.Id && !fingers.Contains(c.Finger)) fingers.Add(c.Finger);
            return fingers;
        }

        private void SelectPerson(StaffMember m)
        {
            selected = m;
            selectedFinger = null;
            List<string> fingers = FingersOf(m);

            personAvatar.Visible = m != null;
            if (m != null) personAvatar.ShowPerson(m.Name, deskApi.Resolve(m.PhotoUrl));
            personName.Text = m != null ? m.Name : "Pick an employee on the left";
            personMeta.Text = m != null ? (m.Designation + (m.Code.Length > 0 ? "  ·  " + m.Code : string.Empty)) : string.Empty;

            for (int i = 0; i < 2; i++)
            {
                bool has = i < fingers.Count;
                string line = string.Empty;
                if (has)
                {
                    FingerInfo info = m.Enrolled.FirstOrDefault(f => f.Finger == fingers[i]);
                    line = "✓  " + Fingers.Label(fingers[i]);
                    if (info != null && info.EnrolledAt.HasValue)
                        line += "   ·   " + ShopTime.DayLabel(info.EnrolledAt.Value) + (string.IsNullOrEmpty(info.DeviceName) ? string.Empty : " on " + info.DeviceName);
                }
                else if (m != null && i == fingers.Count)
                {
                    line = "—  none yet";
                }
                onFileLabels[i].Text = line;
                onFileLabels[i].ForeColor = has ? Theme.Success : Theme.TextMuted;
                removeButtons[i].Visible = has;
                removeButtons[i].Tag = has ? fingers[i] : null;
            }

            foreach (KeyValuePair<string, RoundButton> pair in fingerButtons)
            {
                bool onFile = fingers.Contains(pair.Key);
                pair.Value.Badge = onFile ? "ON FILE" : null;
                pair.Value.Selected = false;
                // A third finger cannot be added: only a finger on file (re-scan) stays open.
                pair.Value.Enabled = m != null && (onFile || fingers.Count < EmployeeLimit);
            }
            startButton.Enabled = false;
            startButton.Text = "3. Start scanning";
            UpdateProgress(0);

            if (m == null) ShowStatus("Pick the employee, then the finger.", Theme.Neutral);
            else if (fingers.Count >= EmployeeLimit)
                ShowStatus(m.Name + " already has " + EmployeeLimit + " fingers on file. Pick one of them to scan it again, or remove one first.", Theme.Neutral);
            else ShowStatus("Pick which finger of " + m.Name + "'s to enroll.", Theme.Neutral);
        }

        private const int EmployeeLimit = 2;

        private void PickFinger(string key)
        {
            if (selected == null || capturing || processing) return;
            selectedFinger = key;
            foreach (KeyValuePair<string, RoundButton> pair in fingerButtons) pair.Value.Selected = pair.Key == key;
            bool replacing = FingersOf(selected).Contains(key);
            startButton.Enabled = true;
            startButton.Text = replacing ? "3. Scan " + Fingers.Label(key).ToLowerInvariant() + " again (replaces it)" : "3. Start scanning " + Fingers.Label(key).ToLowerInvariant();
            ShowStatus(replacing
                ? "The " + Fingers.Label(key).ToLowerInvariant() + " on file will be replaced by the new scans."
                : "Ready: " + selected.Name + "'s " + Fingers.Label(key).ToLowerInvariant() + ". Press Start, then scan it 4 times.", Theme.Neutral);
        }

        // ------------------------------------------------------------ remove

        private void RemoveFinger(int index)
        {
            StaffMember m = selected;
            var finger = removeButtons[index].Tag as string;
            if (m == null || finger == null || capturing || processing || managerApi == null) return;
            DialogResult answer = MessageBox.Show(this,
                "Remove " + m.Name + "'s " + Fingers.Label(finger).ToLowerInvariant() + "?" + Environment.NewLine + Environment.NewLine
                + "That finger stops working on the reader straight away.",
                "Remove a fingerprint", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            SetBusyUi(true);
            ShowStatus("Removing...", Theme.Brand);
            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, object> result = managerApi.Delete("api/desktop/fingerprints/" + m.Id + "/" + finger);
                RunOnUi(delegate
                {
                    SetBusyUi(false);
                    if (Json.Ok(result))
                    {
                        changedAny = true;
                        removedHere.Add(m.Id + "|" + finger);
                        linkedHere.RemoveAll(c => c.EmployeeId == m.Id && c.Finger == finger);
                        Log.Info("Removed " + m.Id + " " + finger + " (by " + manager.Email + ")");
                        SelectPerson(m);
                        ShowStatus(Json.Message(result) ?? "Removed.", Theme.Success);
                        peopleList.Invalidate();
                        ReloadPeople();
                    }
                    else
                    {
                        ShowStatus(ServerProblem(result, "Could not remove the finger"), Theme.Danger);
                    }
                });
            });
        }

        // ------------------------------------------------------------ capture

        private void Start_Click(object sender, EventArgs e)
        {
            if (selected == null || selectedFinger == null || capturing || processing) return;
            if (knownTemplates == null)
            {
                ShowStatus("The list of fingerprints already enrolled has not loaded yet, so this finger cannot be checked against it. "
                    + "Close this window, wait until the counter shows \"Place your finger\", then try again.", Theme.Danger);
                return;
            }
            acceptedScans = new List<DPFP.FeatureSet>();
            checkScans = new List<DPFP.FeatureSet>();
            attempts = 0;
            UpdateProgress(0);
            capturing = true;
            SetBusyUi(true);
            ShowStatus("Scan 1 of " + RequiredScans + ": " + selected.Name + ", put your " + Fingers.Label(selectedFinger).ToLowerInvariant()
                + " flat on the reader.", Theme.Brand);
            try
            {
                if (capturer == null)
                {
                    capturer = new DPFP.Capture.Capture();
                    capturer.EventHandler = this;
                }
                capturer.StartCapture();
            }
            catch (Exception ex)
            {
                Log.Error("Enrolment: the reader could not be opened", ex);
                capturing = false;
                SetBusyUi(false);
                ShowStatus("The fingerprint reader is not available: " + ex.Message + Environment.NewLine
                    + "Check the USB cable and that the DigitalPersona software is installed.", Theme.Danger);
            }
        }

        private void StopCapture()
        {
            try { if (capturer != null) capturer.StopCapture(); }
            catch { }
        }

        private void SetBusyUi(bool isBusy)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusyUi), isBusy); return; }
            peopleList.Enabled = !isBusy;
            searchBox.Enabled = !isBusy;
            foreach (RoundButton b in fingerButtons.Values) b.Enabled = !isBusy && selected != null;
            foreach (RoundButton b in removeButtons) b.Enabled = !isBusy;
            startButton.Enabled = !isBusy && selectedFinger != null;
            closeButton.Enabled = !processing;
            if (!isBusy && selected != null)
            {
                // Put the finger limits back.
                List<string> fingers = FingersOf(selected);
                foreach (KeyValuePair<string, RoundButton> pair in fingerButtons)
                    pair.Value.Enabled = fingers.Contains(pair.Key) || fingers.Count < EmployeeLimit;
            }
        }

        // ---- DPFP.Capture.EventHandler (the SDK's capture thread) ----

        public void OnComplete(object Capture, string ReaderSerialNumber, DPFP.Sample Sample)
        {
            if (!capturing || busy) return;
            busy = true;
            try
            {
                attempts++;
                string error;
                DPFP.FeatureSet features = FingerprintEnroller.ExtractEnrollmentFeatures(Sample.Bytes, out error);
                if (features == null)
                {
                    ShowStatus((error ?? "Could not read that scan.") + Environment.NewLine
                        + "Scan " + (acceptedScans.Count + 1) + " of " + RequiredScans + ": try again.", Theme.Danger);
                    return;
                }
                acceptedScans.Add(features);
                string checkError;
                DPFP.FeatureSet check = FingerprintMatcher.ExtractVerificationFeatures(Sample.Bytes, out checkError);
                if (check != null) checkScans.Add(check);
                UpdateProgress(acceptedScans.Count);

                if (acceptedScans.Count < RequiredScans)
                {
                    ShowStatus("Good. Lift the finger, then scan " + (acceptedScans.Count + 1) + " of " + RequiredScans + ".", Theme.Brand);
                    return;
                }

                capturing = false;
                StopCapture();
                processing = true;
                RunOnUi(delegate { closeButton.Enabled = false; });
                ShowStatus("Saving...", Theme.Brand);
                FinishEnrollment();
            }
            catch (Exception ex)
            {
                Log.Error("Enrolment failed", ex);
                capturing = false;
                StopCapture();
                if (!processing) SetBusyUi(false);
                ShowStatus("Something went wrong: " + ex.Message + Environment.NewLine + "Press Start to try again.", Theme.Danger);
            }
            finally
            {
                if (processing && !capturing)
                {
                    processing = false;
                    SetBusyUi(false);
                }
                busy = false;
            }
        }

        public void OnFingerGone(object Capture, string ReaderSerialNumber) { }
        public void OnFingerTouch(object Capture, string ReaderSerialNumber) { }
        public void OnReaderConnect(object Capture, string ReaderSerialNumber) { }
        public void OnSampleQuality(object Capture, string ReaderSerialNumber, DPFP.Capture.CaptureFeedback CaptureFeedback) { }

        public void OnReaderDisconnect(object Capture, string ReaderSerialNumber)
        {
            RunOnUi(delegate
            {
                if (!capturing) return;
                capturing = false;
                SetBusyUi(false);
                ShowStatus("The fingerprint reader was unplugged. Plug it back in and press Start again.", Theme.Danger);
            });
        }

        // Fuse, check against everyone on file, then save (on the reader's thread).
        private void FinishEnrollment()
        {
            StaffMember person = selected;
            string finger = selectedFinger;

            EnrollmentResult fused = FingerprintEnroller.Fuse(acceptedScans);
            if (!fused.Ready)
            {
                ShowStatus((fused.Error ?? "Enrolment failed.") + Environment.NewLine + "Press Start to try again.", Theme.Danger);
                return;
            }

            string owner = FindExistingOwner(person, finger);
            if (owner != null)
            {
                Log.Info("Enrolment refused on this PC for " + person.Id + " " + finger + ": " + owner.Replace(Environment.NewLine, " "));
                Sounds.PlayProblem(null);
                ShowStatus(owner, Theme.Danger);
                return;
            }

            string template = Convert.ToBase64String(fused.TemplateBytes);
            var body = new Dictionary<string, object>();
            body["finger"] = finger;
            body["template"] = template;
            body["quality"] = (int)Math.Round(100.0 * acceptedScans.Count / Math.Max(attempts, acceptedScans.Count));
            body["deviceName"] = deviceName;

            Dictionary<string, object> result = managerApi.Post("api/desktop/fingerprints/" + person.Id, body, 30000);
            if (!Json.Ok(result))
            {
                string problem = ServerProblem(result, "The fingerprint was not saved");
                Log.Error("Enrolment refused by the server for " + person.Id + " " + finger + ": " + problem);
                ShowStatus(problem, Theme.Danger);
                return;
            }

            changedAny = true;
            Log.Info("Enrolled " + person.Id + " " + finger + " (by " + manager.Email + ")");
            var linked = new TemplateCandidate { EmployeeId = person.Id, Finger = finger, TemplateBase64 = template };
            RunOnUi(delegate
            {
                linkedHere.RemoveAll(c => c.Key() == linked.Key());
                linkedHere.Add(linked);
                removedHere.Remove(linked.Key());
                SelectPerson(person);
                peopleList.Invalidate();
                ShowStatus((Json.Message(result) ?? "Saved.") + Environment.NewLine
                    + person.Name + " can use the reader now. Test it once the window is closed.", Theme.Success);
                ReloadPeople();
            });
            Sounds.PlayIn(null);
        }

        // One finger belongs to one person: the new scans are matched, at the
        // reader's own threshold, against every finger already on file except
        // the one being replaced. Returns why to refuse, or null.
        private string FindExistingOwner(StaffMember person, string finger)
        {
            var others = new List<TemplateCandidate>();
            var seen = new HashSet<string>();
            foreach (TemplateCandidate c in linkedHere)
            {
                if (c.EmployeeId == person.Id && c.Finger == finger) continue;
                others.Add(c);
                seen.Add(c.Key());
            }
            foreach (TemplateCandidate c in knownTemplates.Candidates)
            {
                if (seen.Contains(c.Key()) || removedHere.Contains(c.Key())) continue;
                if (c.EmployeeId == person.Id && c.Finger == finger) continue;
                others.Add(c);
            }
            if (others.Count == 0) return null;
            if (checkScans.Count == 0) return "These scans could not be checked against the fingerprints on file. Press Start to try again.";

            foreach (DPFP.FeatureSet scan in checkScans)
            {
                MatchResult match = FingerprintMatcher.Identify(scan, others);
                if (match.Ambiguous) return "This finger already matches more than one person on file. Use a different finger.";
                if (!match.Matched) continue;
                if (match.EmployeeId == person.Id)
                    return "This finger is already on file for " + person.Name + " as the " + Fingers.Label(match.Finger).ToLowerInvariant()
                        + ". Pick that finger to scan it again, or use a different finger.";
                StaffMember other = directory.Find(match.EmployeeId);
                return "This finger is already enrolled for " + (other != null ? other.Name : "another employee") + "."
                    + Environment.NewLine + "One finger can belong to one person only. If that is wrong, remove it from them first.";
            }
            return null;
        }

        private string ServerProblem(Dictionary<string, object> result, string what)
        {
            int status = Json.HttpStatus(result);
            if (Json.IsNetworkError(result)) return what + ": no connection to the server. Check the internet and try again.";
            if (status == 401) return what + ": the manager sign-in has expired. Close this window and open it again.";
            if (status == 403) return what + ": this manager account is not allowed to enroll fingerprints.";
            return what + ": " + (Json.Message(result) ?? "HTTP " + status) + ".";
        }

        private void UpdateProgress(int completed)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int>(UpdateProgress), completed); return; }
            progressBar.Completed = completed;
            progressLabel.Text = completed + " of " + RequiredScans + " scans";
            progressLabel.ForeColor = completed >= RequiredScans ? Theme.Success : Theme.TextSecondary;
        }

        private void ShowStatus(string text, Color colour)
        {
            if (InvokeRequired) { RunOnUi(delegate { ShowStatus(text, colour); }); return; }
            statusBox.ShowMessage(text, colour);
        }

        private void RunOnUi(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (Exception ex)
            {
                Log.Error("The enrolment window could not be updated", ex);
            }
        }
    }
}
