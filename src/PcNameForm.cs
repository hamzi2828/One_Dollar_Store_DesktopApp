using System;
using System.Drawing;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // The "PC name" button: renames this PC (the name on every punch and the
    // one the website lists for this counter) without the Settings window,
    // which the website's Roles page may hide. Saved to desktopApp.config.json
    // the same way Settings saves it; takes effect at once, no restart.
    public class PcNameForm : Form
    {
        private readonly AppConfig config;
        private TextBox nameBox;
        private AlertBox statusBox;

        public PcNameForm(AppConfig config)
        {
            this.config = config;
            BuildUi();
        }

        // True when a new name was saved (config.DeviceName is updated).
        public static bool Edit(IWin32Window owner, AppConfig config)
        {
            using (var dialog = new PcNameForm(config))
                return dialog.ShowDialog(owner) == DialogResult.OK;
        }

        private static int P(int v) { return Theme.Px(v); }

        private void BuildUi()
        {
            Theme.PrepareForDpi(this);
            SuspendLayout();
            Text = AppInfo.Name + " — PC name";
            ClientSize = new Size(P(460), P(300));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody;
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            var header = new HeaderBand("PC name", "The name of this counter PC", false);
            header.SetBounds(0, 0, ClientSize.Width, P(70));

            int x = P(28), width = ClientSize.Width - P(56);
            var label = Theme.Label("THIS PC'S NAME", Theme.FontLabel, Theme.TextSecondary);
            label.SetBounds(x, P(90), width, P(16));
            nameBox = Theme.TextBox();
            nameBox.MaxLength = 60;
            nameBox.Text = config.DeviceLabel;
            FieldFrame frame = FieldFrame.Wrap(nameBox, x, P(108), width, P(40));

            statusBox = new AlertBox();
            statusBox.SetBounds(x, P(158), width, P(56));
            statusBox.ShowMessage("Shown on each punch and on the website, e.g. \"Counter PC\".", Theme.Neutral);

            var saveButton = Theme.PrimaryButton("Save");
            saveButton.SetBounds(ClientSize.Width - x - P(140), P(232), P(140), P(44));
            saveButton.Click += Save_Click;
            var cancelButton = Theme.QuietButton("Cancel", Theme.TextSecondary);
            cancelButton.SetBounds(saveButton.Left - P(12) - P(120), P(232), P(120), P(44));
            cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(header);
            Controls.Add(label);
            Controls.Add(frame);
            Controls.Add(statusBox);
            Controls.Add(cancelButton);
            Controls.Add(saveButton);
            AcceptButton = saveButton;
            CancelButton = cancelButton;
            ResumeLayout(false);
            Theme.FitToScreen(this);
        }

        private void Save_Click(object sender, EventArgs e)
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0)
            {
                statusBox.ShowMessage("Give this PC a name, e.g. Counter PC.", Theme.Danger);
                return;
            }
            if (name == config.DeviceLabel)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            string error = AppConfig.Save(config.ApiBaseUrl, name, config.PosUrl, config.SoundOn, config.VoiceOn, config.LabelPrinter);
            if (error != null)
            {
                Log.Error("PC name not saved: " + error);
                statusBox.ShowMessage(error, Theme.Danger);
                return;
            }
            Log.Info("PC name changed from \"" + config.DeviceLabel + "\" to \"" + name + "\"");
            config.DeviceName = name;
            ApiClient.DeviceName = name;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
