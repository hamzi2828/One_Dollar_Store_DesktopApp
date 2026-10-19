using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    public class LoginSuccessEventArgs : EventArgs
    {
        public string Token;
        public DateTime ExpiresAtUtc;
        public SessionUser User;
    }

    // Signs the counter in ONCE with a manager's or admin's POS email and
    // password (POST /user/login, the same account as the admin panel). The
    // account's role must have "Attendance desktop app" (attendance_kiosk) view
    // and create; anything less is refused here, before the token is kept.
    //
    // It also fronts for a saved sign-in the app could not confirm at start-up
    // (no internet, server trouble): it says why and offers Retry.
    public class LoginForm : Form
    {
        private readonly ApiClient api;
        private readonly AppConfig config;
        private readonly string blocker;

        private TextBox emailBox;
        private TextBox passwordBox;
        private CheckBox showPassword;
        private RoundButton signInButton;
        private RoundButton retryButton;
        private Label statusLabel;
        private bool busy;

        public event EventHandler<LoginSuccessEventArgs> LoggedIn;
        public event EventHandler RetryRequested;

        public LoginForm(ApiClient api, AppConfig config, string emailHint)
        {
            this.api = api;
            this.config = config;
            blocker = config.SignInBlocker;
            BuildUi(emailHint);
            if (config.LoadError != null) ShowStatus(config.LoadError, Theme.Danger);
        }

        private static int P(int v) { return Theme.Px(v); }

        private void BuildUi(string emailHint)
        {
            Theme.PrepareForDpi(this);
            SuspendLayout();
            Text = AppInfo.Name + " — Sign in";
            ClientSize = new Size(P(480), P(640));
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            var header = new HeaderBand("Attendance", "Staff clock in by fingerprint", false);
            header.SetBounds(0, 0, ClientSize.Width, P(70));

            int x = P(40), width = ClientSize.Width - P(80);
            var title = Theme.Label("Sign in this counter", Theme.FontTitle, Theme.TextPrimary);
            title.SetBounds(x, P(92), width, P(36));
            var hint = Theme.Label("Use a manager or admin account from the POS. The counter stays signed in after this.",
                Theme.FontSmall, Theme.TextSecondary);
            hint.SetBounds(x, P(130), width, P(36));

            var emailLabel = Theme.Label("EMAIL", Theme.FontLabel, Theme.TextSecondary);
            emailLabel.SetBounds(x, P(178), width, P(16));
            emailBox = Theme.TextBox();
            emailBox.Text = emailHint ?? string.Empty;
            FieldFrame emailFrame = FieldFrame.Wrap(emailBox, x, P(196), width, P(42));

            var passLabel = Theme.Label("PASSWORD", Theme.FontLabel, Theme.TextSecondary);
            passLabel.SetBounds(x, P(250), width, P(16));
            passwordBox = Theme.TextBox();
            passwordBox.UseSystemPasswordChar = true;
            FieldFrame passFrame = FieldFrame.Wrap(passwordBox, x, P(268), width - P(84), P(42));
            showPassword = Theme.CheckBox("Show");
            showPassword.Font = Theme.FontSmall;
            showPassword.SetBounds(x + width - P(74), P(278), P(74), P(24));
            showPassword.CheckedChanged += delegate { passwordBox.UseSystemPasswordChar = !showPassword.Checked; };

            signInButton = Theme.PrimaryButton("Sign in");
            signInButton.SetBounds(x, P(330), width, P(48));
            signInButton.Enabled = blocker == null;
            signInButton.Click += SignIn_Click;

            statusLabel = Theme.Label(string.Empty, Theme.FontSmall, Theme.Danger);
            statusLabel.TextAlign = ContentAlignment.TopCenter;
            statusLabel.SetBounds(x, P(388), width, P(74));

            retryButton = Theme.QuietButton("Retry", Theme.Brand);
            retryButton.SetBounds(x, P(466), width, P(40));
            retryButton.Visible = false;
            retryButton.Click += delegate
            {
                if (busy) return;
                if (RetryRequested != null) RetryRequested(this, EventArgs.Empty);
            };

            var serverLabel = Theme.Label("Server: " + config.ServerLabel + "   ·   This PC: " + config.DeviceLabel,
                Theme.FontSmall, Theme.TextMuted);
            serverLabel.TextAlign = ContentAlignment.MiddleCenter;
            serverLabel.SetBounds(x, P(540), width, P(18));

            var settingsLink = new LinkLabel();
            settingsLink.Text = "Settings (server, PC name, sound)";
            settingsLink.Font = Theme.FontSmall;
            settingsLink.LinkColor = Theme.Brand;
            settingsLink.ActiveLinkColor = Theme.BrandDark;
            settingsLink.VisitedLinkColor = Theme.Brand;
            settingsLink.LinkBehavior = LinkBehavior.HoverUnderline;
            settingsLink.BackColor = Color.Transparent;
            settingsLink.TextAlign = ContentAlignment.MiddleCenter;
            settingsLink.SetBounds(x, P(562), width, P(22));
            settingsLink.LinkClicked += delegate { if (!busy) SettingsForm.Edit(this, config); };

            var version = Theme.Label(AppInfo.Label, Theme.FontSmall, Theme.TextMuted);
            version.TextAlign = ContentAlignment.MiddleCenter;
            version.SetBounds(x, P(600), width, P(18));

            Controls.Add(header);
            Controls.Add(title);
            Controls.Add(hint);
            Controls.Add(emailLabel);
            Controls.Add(emailFrame);
            Controls.Add(passLabel);
            Controls.Add(passFrame);
            Controls.Add(showPassword);
            Controls.Add(signInButton);
            Controls.Add(statusLabel);
            Controls.Add(retryButton);
            Controls.Add(serverLabel);
            Controls.Add(settingsLink);
            Controls.Add(version);
            AcceptButton = signInButton;
            ResumeLayout(false);
            Theme.FitToScreen(this);

            if (blocker != null) ShowStatus(blocker, Theme.Danger);
            Load += delegate { (emailBox.Text.Length > 0 ? passwordBox : emailBox).Focus(); };
        }

        // A saved sign-in could not be confirmed. `canRetry`: asking again may
        // help (no internet), so Retry is offered.
        public void ShowSessionProblem(string message, bool canRetry)
        {
            ShowStatus(message, canRetry ? Theme.Warning : Theme.Danger);
            retryButton.Visible = canRetry;
        }

        public void SetBusy(bool isBusy, string text)
        {
            busy = isBusy;
            signInButton.Enabled = !isBusy && blocker == null;
            retryButton.Enabled = !isBusy;
            Cursor = isBusy ? Cursors.WaitCursor : Cursors.Default;
            if (text != null) ShowStatus(text, Theme.TextSecondary);
        }

        private void SignIn_Click(object sender, EventArgs e)
        {
            if (busy) return;
            if (blocker != null) { ShowStatus(blocker, Theme.Danger); return; }
            string email = emailBox.Text.Trim();
            string password = passwordBox.Text;
            if (email.Length == 0 || password.Length == 0)
            {
                ShowStatus("Enter the email and password.", Theme.Danger);
                return;
            }

            SetBusy(true, "Signing in...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                SignInOutcome outcome;
                try { outcome = SignInOutcome.Attempt(api, email, password, false); }
                catch (Exception ex)
                {
                    Log.Error("Sign-in failed", ex);
                    outcome = new SignInOutcome { Message = "Sign-in failed: " + ex.Message };
                }
                RunOnUi(delegate
                {
                    SetBusy(false, null);
                    if (outcome.User == null)
                    {
                        ShowStatus(outcome.Message, Theme.Danger);
                        return;
                    }
                    passwordBox.Text = string.Empty;
                    retryButton.Visible = false;
                    ShowStatus("Signed in.", Theme.Success);
                    Log.Info("Signed in as " + outcome.User.Email);
                    if (LoggedIn != null)
                        LoggedIn(this, new LoginSuccessEventArgs { Token = outcome.Token, ExpiresAtUtc = outcome.ExpiresAtUtc, User = outcome.User });
                });
            });
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
                Log.Error("Sign-in result could not be shown", ex);
            }
        }

        private void ShowStatus(string text, Color color)
        {
            statusLabel.ForeColor = color;
            statusLabel.Text = text ?? string.Empty;
        }
    }

    // One email + password sign-in, checked against what the window needs:
    // the counter (needs "Attendance desktop app" view + create) or the
    // enrolment window (needs employees edit or attendance edit).
    public class SignInOutcome
    {
        public string Token;
        public DateTime ExpiresAtUtc;
        public SessionUser User;   // null = refused; Message says why
        public string Message;

        public static SignInOutcome Attempt(ApiClient api, string email, string password, bool forEnrolment)
        {
            var outcome = new SignInOutcome();
            var body = new Dictionary<string, object>();
            body["email"] = email;
            body["password"] = password;
            Dictionary<string, object> login = api.Send("POST", "user/login", body, false, ApiClient.DefaultTimeoutMs);

            if (Json.IsNetworkError(login))
            {
                outcome.Message = "No connection to the server. Check the internet on this PC and try again."
                    + Environment.NewLine + "(" + (Json.Message(login) ?? "no answer") + ")";
                return outcome;
            }
            int status = Json.HttpStatus(login);
            string token = Json.Str(login, "token");
            if (status == 401 || status == 400) { outcome.Message = "Wrong email or password."; return outcome; }
            if (!Json.Ok(login) || string.IsNullOrEmpty(token))
            {
                outcome.Message = Json.Message(login) ?? ("Sign-in failed (HTTP " + status + ").");
                return outcome;
            }

            ApiClient probe = api.WithToken(token);
            Dictionary<string, object> session = probe.Get("api/desktop/session");
            SessionUser user = Json.Ok(session) ? SessionUser.From(Json.Data(session)) : null;
            if (user == null)
            {
                outcome.Message = Json.IsNetworkError(session)
                    ? "Signed in, but the server stopped answering. Try again."
                    : (Json.Message(session) ?? "The server did not confirm the account.")
                        + (Json.HttpStatus(session) == 404 ? " (Is the server up to date? /api/desktop is missing.)" : string.Empty);
                return outcome;
            }

            if (forEnrolment && !user.CanEnroll)
            {
                outcome.Message = "This account cannot enroll fingerprints. In the admin panel its role needs "
                    + "Employees \"edit\" or Attendance \"edit\".";
                return outcome;
            }
            if (!forEnrolment && !user.CanRunDesk)
            {
                outcome.Message = "This account cannot run the attendance app. In the admin panel (Roles), give its role "
                    + "\"Attendance desktop app\" with View and Create, then sign in again.";
                return outcome;
            }

            outcome.Token = token;
            outcome.ExpiresAtUtc = TokenExpiry(token) ?? DateTime.UtcNow.AddDays(7);
            outcome.User = user;
            return outcome;
        }

        // The "exp" claim of a JWT, without checking the signature (the server
        // does that); only used to know when the saved sign-in runs out.
        public static DateTime? TokenExpiry(string jwt)
        {
            try
            {
                string[] parts = (jwt ?? string.Empty).Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                while (payload.Length % 4 != 0) payload += "=";
                string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                var data = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                long exp = Convert.ToInt64(data["exp"]);
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(exp);
            }
            catch
            {
                return null;
            }
        }
    }
}
