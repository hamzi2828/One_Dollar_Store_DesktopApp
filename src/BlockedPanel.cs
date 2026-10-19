using System;
using System.Drawing;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // Covers the whole counter window when attendance cannot be marked at all:
    // no fingerprint reader plugged in, its driver missing, or the fingerprint
    // engine files missing. There is deliberately NO other way to clock in --
    // the shop marks attendance by fingerprint only -- so the window says so
    // plainly instead of offering one. A missed punch is added by a manager in
    // the admin panel (HR -> Attendance -> Correct times).
    public class BlockedPanel : Panel
    {
        private readonly ReadyPulse glyph;
        private readonly Label titleLabel;
        private readonly Label bodyLabel;
        private readonly Label helpLabel;

        public BlockedPanel()
        {
            BackColor = Theme.Background;
            DoubleBuffered = true;
            glyph = new ReadyPulse();
            glyph.Slashed = true;
            titleLabel = Theme.Label(string.Empty, Theme.FontBlocked, Theme.Danger);
            titleLabel.TextAlign = ContentAlignment.MiddleCenter;
            bodyLabel = Theme.Label(string.Empty, Theme.FontStatus, Theme.TextPrimary);
            bodyLabel.TextAlign = ContentAlignment.TopCenter;
            helpLabel = Theme.Label(
                "Staff who could not clock in: a manager adds the time in the admin panel under HR → Attendance → Correct times (with a reason).",
                Theme.FontBody, Theme.TextSecondary);
            helpLabel.TextAlign = ContentAlignment.TopCenter;
            Controls.Add(glyph);
            Controls.Add(titleLabel);
            Controls.Add(bodyLabel);
            Controls.Add(helpLabel);
        }

        public void ShowProblem(string title, string body)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, string>(ShowProblem), title, body); return; }
            titleLabel.Text = title ?? string.Empty;
            bodyLabel.Text = body ?? string.Empty;
            Relayout();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Danger, 2f * Theme.Scale))
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }

        private void Relayout()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            int textWidth = Math.Min(w - Theme.Px(80), Theme.Px(900));
            int x = (w - textWidth) / 2;
            int glyphSize = Math.Min(Theme.Px(200), h / 3);
            int top = Math.Max(Theme.Px(24), (h - glyphSize - Theme.Px(260)) / 2);
            glyph.SetBounds((w - glyphSize) / 2, top, glyphSize, glyphSize);
            Size title = TextRenderer.MeasureText(titleLabel.Text, titleLabel.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak);
            titleLabel.SetBounds(x, glyph.Bottom + Theme.Px(16), textWidth, Math.Max(Theme.Px(52), title.Height + Theme.Px(6)));
            bodyLabel.SetBounds(x, titleLabel.Bottom + Theme.Px(6), textWidth, Theme.Px(90));
            helpLabel.SetBounds(x, bodyLabel.Bottom + Theme.Px(10), textWidth, Theme.Px(60));
        }
    }
}
