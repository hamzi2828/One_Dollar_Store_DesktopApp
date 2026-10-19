using System;
using System.Drawing;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // Edits desktopApp.config.json: the POS server, this PC's name (it goes on
    // every punch: "fingerprint · Counter PC"), and sound / voice on or off.
    // Saving restarts the app, because the server and the saved sign-in are
    // settled at start-up.
    public class SettingsForm : Form
    {
        private readonly AppConfig current;
        private TextBox serverBox;
        private TextBox deviceBox;
        private CheckBox soundBox;
        private CheckBox voiceBox;
        private AlertBox statusBox;

        public SettingsForm(AppConfig current)
        {
            this.current = current;
            BuildUi();
        }

        public static void Edit(IWin32Window owner, AppConfig current)
        {
            bool saved;
            using (var dialog = new SettingsForm(current))
                saved = dialog.ShowDialog(owner) == DialogResult.OK;
            if (!saved) return;
            try
            {
                Log.Info("Restarting to apply the new settings");
                Application.Restart();
            }
            catch (Exception ex)
            {
                Log.Error("Restart after a settings change failed", ex);
                MessageBox.Show(owner, "The settings were saved. Close " + AppInfo.Name + " and start it again to use them.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static int P(int v) { return Theme.Px(v); }

        private void BuildUi()
        {
            Theme.PrepareForDpi(this);
            SuspendLayout();
            Text = AppInfo.Name + " — Settings";
            ClientSize = new Size(P(540), P(560));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            var header = new HeaderBand("Settings", "Server, this PC's name, sound", false);
            header.SetBounds(0, 0, ClientSize.Width, P(70));

            int x = P(28), width = ClientSize.Width - P(56);

            var serverLabel = Theme.Label("POS SERVER ADDRESS", Theme.FontLabel, Theme.TextSecondary);
            serverLabel.SetBounds(x, P(90), width, P(16));
            serverBox = Theme.TextBox();
            serverBox.Text = current.ApiBaseUrl ?? string.Empty;
            FieldFrame serverFrame = FieldFrame.Wrap(serverBox, x, P(108), width, P(40));
            var serverHint = Theme.Label("Normally " + AppConfig.LiveServer + " — leave it unless told otherwise.",
                Theme.FontSmall, Theme.TextMuted);
            serverHint.SetBounds(x, P(152), width, P(18));

            var deviceLabel = Theme.Label("THIS PC'S NAME", Theme.FontLabel, Theme.TextSecondary);
            deviceLabel.SetBounds(x, P(184), width, P(16));
            deviceBox = Theme.TextBox();
            deviceBox.MaxLength = 60;
            deviceBox.Text = current.DeviceLabel;
            FieldFrame deviceFrame = FieldFrame.Wrap(deviceBox, x, P(202), width, P(40));
            var deviceHint = Theme.Label("Shown on each punch in the admin panel, e.g. \"Counter PC\".", Theme.FontSmall, Theme.TextMuted);
            deviceHint.SetBounds(x, P(246), width, P(18));

            soundBox = Theme.CheckBox("Play a sound for every fingerprint");
            soundBox.Checked = current.SoundOn;
            soundBox.SetBounds(x, P(280), width, P(28));
            voiceBox = Theme.CheckBox("Say the result out loud (\"Ali, clocked in\")");
            voiceBox.Checked = current.VoiceOn;
            voiceBox.SetBounds(x, P(312), width, P(28));

            statusBox = new AlertBox();
            statusBox.SetBounds(x, P(352), width, P(84));

            var pathLabel = Theme.Label("Saved to " + (current.ConfigPath ?? AppConfig.FilePath), Theme.FontSmall, Theme.TextMuted);
            pathLabel.AutoEllipsis = true;
            pathLabel.SetBounds(x, P(444), width, P(18));

            var versionLabel = Theme.Label(AppInfo.Label, Theme.FontSmall, Theme.TextMuted);
            versionLabel.SetBounds(x, P(502), P(140), P(18));

            var saveButton = Theme.PrimaryButton("Save and restart");
            saveButton.SetBounds(ClientSize.Width - x - P(190), P(488), P(190), P(44));
            saveButton.Click += Save_Click;
            var cancelButton = Theme.QuietButton("Cancel", Theme.TextSecondary);
            cancelButton.SetBounds(saveButton.Left - P(12) - P(120), P(488), P(120), P(44));
            cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(header);
            Controls.Add(serverLabel);
            Controls.Add(serverFrame);
            Controls.Add(serverHint);
            Controls.Add(deviceLabel);
            Controls.Add(deviceFrame);
            Controls.Add(deviceHint);
            Controls.Add(soundBox);
            Controls.Add(voiceBox);
            Controls.Add(statusBox);
            Controls.Add(pathLabel);
            Controls.Add(versionLabel);
            Controls.Add(cancelButton);
            Controls.Add(saveButton);
            AcceptButton = saveButton;
            CancelButton = cancelButton;
            ResumeLayout(false);
            Theme.FitToScreen(this);

            serverBox.TextChanged += delegate { ShowAssessment(); };
            deviceBox.TextChanged += delegate { ShowAssessment(); };
            if (current.LoadError != null) statusBox.ShowMessage(current.LoadError, Theme.Danger);
            else ShowAssessment();
        }

        private void ShowAssessment()
        {
            string url, error;
            Check(out url, out error);
            if (error != null) statusBox.ShowMessage(error, Theme.Danger);
            else statusBox.ShowMessage("Saving restarts the app. Each server keeps its own sign-in on this PC.", Theme.Neutral);
        }

        private bool Check(out string url, out string error)
        {
            error = null;
            string typed = serverBox.Text.Trim();
            if (typed.Length > 0 && typed.IndexOf("://", StringComparison.Ordinal) < 0) typed = "https://" + typed;
            url = AppConfig.NormalizeBaseUrl(typed);
            Uri parsed;
            if (url.Length == 0) error = "Enter the server address.";
            else if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) || parsed.Host.Length == 0)
                error = "The server address must be a full web address, e.g. " + AppConfig.LiveServer;
            else error = AppConfig.InsecureAddressProblem(url);
            if (error == null && deviceBox.Text.Trim().Length == 0) error = "Give this PC a name, e.g. Counter PC.";
            return error == null;
        }

        private void Save_Click(object sender, EventArgs e)
        {
            string url, error;
            if (!Check(out url, out error))
            {
                statusBox.ShowMessage(error, Theme.Danger);
                return;
            }
            string device = deviceBox.Text.Trim();
            if (current.LoadError == null && url == current.ApiBaseUrl && device == current.DeviceLabel
                && soundBox.Checked == current.SoundOn && voiceBox.Checked == current.VoiceOn)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            string saveError = AppConfig.Save(url, device, soundBox.Checked, voiceBox.Checked);
            if (saveError != null)
            {
                Log.Error("Settings not saved: " + saveError);
                statusBox.ShowMessage(saveError, Theme.Danger);
                return;
            }
            Log.Info("Settings saved: server " + url + ", PC " + device + ", sound " + soundBox.Checked + ", voice " + voiceBox.Checked);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
