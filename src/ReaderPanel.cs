using System;
using System.Drawing;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // LEFT half of the counter window: the fingerprint reader, and whoever has
    // just put a finger on it -- nobody else.
    //
    //   resting   a pulsing fingerprint and "Place your finger on the reader"
    //   matched   the person's photo (or initials) and name AT ONCE, matched
    //             on this PC, with "Marking attendance..."
    //   result    the server's answer, big: "IN 9:58 am · on time" (green),
    //             "IN 10:25 am · 25 min late" (amber), "OUT 6:05 pm · worked
    //             7 h 35 m" (blue); then back to resting after a few seconds
    //
    // Under it all, a big "Break" button: pressed, an amber banner says "Break:
    // scan your finger" and the next scan starts (or ends) a break.
    public class ReaderPanel : Panel
    {
        private enum Mode { Idle, Person }

        private readonly ReadyPulse pulse;
        private readonly Label idleHeadline;
        private readonly Label idleSubtext;
        private readonly RoundButton idleAction;
        private EventHandler idleActionHandler;
        private readonly AlertBox notice;
        private string noticeText;

        private readonly Avatar avatar;
        private readonly Label nameLabel;
        private readonly Label metaLabel;
        private readonly ResultCard card;
        private readonly Label messageLabel;

        private readonly RoundButton breakButton;
        private readonly AlertBox breakBanner;
        private bool breakAvailable;
        private bool breakArmed;

        // The Break button was pressed (to arm break mode, or to cancel it).
        public event EventHandler BreakClicked;

        private Mode mode = Mode.Idle;

        public ReaderPanel()
        {
            BackColor = Theme.CardColor;
            DoubleBuffered = true;

            pulse = new ReadyPulse();
            idleHeadline = Theme.Label(string.Empty, new Font("Segoe UI", 21, FontStyle.Bold), Theme.TextPrimary);
            idleHeadline.TextAlign = ContentAlignment.MiddleCenter;
            idleSubtext = Theme.Label(string.Empty, Theme.FontStatus, Theme.TextSecondary);
            idleSubtext.TextAlign = ContentAlignment.TopCenter;
            idleAction = Theme.QuietButton("Retry", Theme.Brand);
            idleAction.Visible = false;
            idleAction.Click += delegate
            {
                EventHandler handler = idleActionHandler;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            notice = new AlertBox();
            notice.Visible = false;

            avatar = new Avatar();
            nameLabel = Theme.Label(string.Empty, Theme.FontPersonName, Theme.TextPrimary);
            nameLabel.TextAlign = ContentAlignment.MiddleCenter;
            nameLabel.AutoEllipsis = true;
            metaLabel = Theme.Label(string.Empty, Theme.FontStatus, Theme.TextSecondary);
            metaLabel.TextAlign = ContentAlignment.MiddleCenter;
            metaLabel.AutoEllipsis = true;
            card = new ResultCard();
            messageLabel = Theme.Label(string.Empty, Theme.FontBody, Theme.TextSecondary);
            messageLabel.TextAlign = ContentAlignment.TopCenter;

            breakButton = Theme.QuietButton("Break", Theme.Warning);
            breakButton.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            breakButton.Visible = false;
            breakButton.TabStop = false;
            breakButton.Click += delegate
            {
                EventHandler handler = BreakClicked;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            breakBanner = new AlertBox();
            breakBanner.MessageFont = new Font("Segoe UI", 15, FontStyle.Bold);
            breakBanner.Visible = false;

            Controls.Add(breakButton);
            Controls.Add(breakBanner);
            Controls.Add(pulse);
            Controls.Add(idleHeadline);
            Controls.Add(idleSubtext);
            Controls.Add(idleAction);
            Controls.Add(notice);
            Controls.Add(avatar);
            Controls.Add(nameLabel);
            Controls.Add(metaLabel);
            Controls.Add(card);
            Controls.Add(messageLabel);

            ShowIdle(PulseState.Off, "Starting...", string.Empty, Theme.TextSecondary);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        private void Relayout()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            int pad = Theme.Px(24);
            int inner = w - pad * 2;

            notice.SetBounds(pad, pad, inner, Theme.Px(72));

            // The Break button along the bottom; its banner under the notice.
            int buttonHeight = Theme.Px(64);
            int buttonWidth = Math.Min(inner, Theme.Px(340));
            breakButton.SetBounds((w - buttonWidth) / 2, h - pad - buttonHeight, buttonWidth, buttonHeight);
            int bottom = h - (breakAvailable ? pad + buttonHeight + Theme.Px(8) : 0);
            int top = pad + (noticeText != null ? Theme.Px(84) : 0);
            breakBanner.SetBounds(pad, top, inner, Theme.Px(60));
            if (BannerShowing) top += Theme.Px(72);

            // Resting: the glyph, the line, the supporting text, as one block
            // a little above the middle.
            int room = bottom - top;
            int pulseSize = Math.Max(Theme.Px(60), Math.Min(Math.Min(inner * 6 / 10, Math.Min(h * 4 / 10, room - Theme.Px(150))), Theme.Px(240)));
            int blockTop = Math.Max(top, top + (room - pulseSize - Theme.Px(150)) / 2);
            if (noticeText == null && !BannerShowing && !breakAvailable)
                blockTop = Math.Max(pad, (h - pulseSize - Theme.Px(150)) / 2);
            pulse.SetBounds((w - pulseSize) / 2, blockTop, pulseSize, pulseSize);
            idleHeadline.SetBounds(pad, blockTop + pulseSize + Theme.Px(12), inner, Theme.Px(44));
            idleSubtext.SetBounds(pad, idleHeadline.Bottom + Theme.Px(4), inner, Theme.Px(80));
            idleAction.SetBounds((w - Theme.Px(180)) / 2, idleSubtext.Top + Theme.Px(56), Theme.Px(180), Theme.Px(42));

            // A person: photo, name, what they do, then the result card.
            int avatarSize = Math.Min(Theme.Px(170), bottom / 4);
            int personTop = Math.Max(pad, (bottom - avatarSize - Theme.Px(330)) / 2);
            avatar.SetBounds((w - avatarSize) / 2, personTop, avatarSize, avatarSize);
            nameLabel.SetBounds(pad, avatar.Bottom + Theme.Px(10), inner, Theme.Px(56));
            metaLabel.SetBounds(pad, nameLabel.Bottom, inner, Theme.Px(28));
            card.SetBounds(pad, metaLabel.Bottom + Theme.Px(14), inner, Theme.Px(140));
            messageLabel.SetBounds(pad, card.Bottom + Theme.Px(10), inner, Math.Max(Theme.Px(40), bottom - card.Bottom - Theme.Px(20)));
        }

        // The resting state and every in-between one ("Reading...", "Not
        // recognised"): one glyph, one line, one supporting line.
        public void ShowIdle(PulseState pulseState, string headline, string subtext, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action<PulseState, string, string, Color>(ShowIdle), pulseState, headline, subtext, color); return; }
            mode = Mode.Idle;
            pulse.State = pulseState;
            pulse.Slashed = false;
            idleHeadline.Text = headline ?? string.Empty;
            idleHeadline.ForeColor = color;
            idleSubtext.Text = subtext ?? string.Empty;
            idleAction.Visible = false;
            idleActionHandler = null;
            SetModeVisibility();
        }

        // A resting state with one thing to do about it ("Retry").
        public void ShowIdleAction(string headline, string subtext, Color color, string actionText, EventHandler onAction)
        {
            ShowIdle(PulseState.Off, headline, subtext, color);
            idleAction.Text = actionText ?? "Retry";
            idleActionHandler = onAction;
            idleAction.Visible = onAction != null;
            idleAction.BringToFront();
        }

        // The finger matched someone on this PC: their name goes up at once.
        public void ShowPerson(StaffMember person, string fallbackName, string line, Color lineColor, string photoUrl)
        {
            string name = person != null ? person.Name : (fallbackName ?? "Matched");
            string meta = person != null ? person.Designation : string.Empty;
            ShowPersonCard(name, meta, photoUrl, null, null, null, lineColor, line);
        }

        // The server's answer (or "saved on this PC" / "not marked").
        public void ShowResult(string name, string designation, string photoUrl, string word, string time, string detail,
            Color accent, string message)
        {
            ShowPersonCard(name, designation, photoUrl, word, time, detail, accent, message);
        }

        private void ShowPersonCard(string name, string designation, string photoUrl, string word, string time, string detail,
            Color accent, string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(delegate { ShowPersonCard(name, designation, photoUrl, word, time, detail, accent, message); }));
                return;
            }
            mode = Mode.Person;
            pulse.State = PulseState.Off;
            avatar.Ring = word != null ? accent : Theme.Brand;
            avatar.ShowPerson(name, photoUrl);
            nameLabel.Text = name ?? string.Empty;
            metaLabel.Text = designation ?? string.Empty;
            card.Visible = word != null;
            card.Set(word, time, detail, accent);
            messageLabel.Text = message ?? string.Empty;
            messageLabel.ForeColor = word == null ? accent : Theme.TextSecondary;
            SetModeVisibility();
            card.Visible = word != null;
        }

        // The Break button is offered (the reader is ready and the server has
        // attendance on for this counter).
        public void SetBreakAvailable(bool available)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBreakAvailable), available); return; }
            if (breakAvailable == available) return;
            breakAvailable = available;
            SetModeVisibility();
        }

        // Break mode on (with the seconds left) or off.
        public void SetBreakArmed(bool armed, int secondsLeft)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool, int>(SetBreakArmed), armed, secondsLeft); return; }
            bool changed = armed != breakArmed;
            breakArmed = armed;
            if (armed)
            {
                breakBanner.ShowMessage("BREAK: scan your finger now" + (secondsLeft > 0 ? "   (" + secondsLeft + ")" : string.Empty), Theme.Warning);
                breakButton.Text = "Cancel break";
                breakButton.SetColours(Theme.Warning, Theme.OnBrand, Color.FromArgb(220, 140, 8), Color.Empty);
            }
            else
            {
                breakButton.Text = "Break";
                breakButton.SetColours(Theme.CardRaised, Theme.Warning, Color.FromArgb(42, 42, 47), Theme.BorderStrong);
            }
            if (changed) SetModeVisibility();
        }

        private bool BannerShowing { get { return breakArmed && breakAvailable && mode == Mode.Idle; } }

        // A standing note over the resting state (offline, punches waiting).
        public void SetNotice(string text, Color accent)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, Color>(SetNotice), text, accent); return; }
            noticeText = string.IsNullOrEmpty(text) ? null : text;
            if (noticeText != null) notice.ShowMessage(noticeText, accent);
            notice.Visible = noticeText != null && mode == Mode.Idle;
            Relayout();
        }

        private void SetModeVisibility()
        {
            bool idle = mode == Mode.Idle;
            pulse.Visible = idle;
            idleHeadline.Visible = idle;
            idleSubtext.Visible = idle;
            if (!idle) idleAction.Visible = false;
            notice.Visible = idle && noticeText != null;
            avatar.Visible = !idle;
            nameLabel.Visible = !idle;
            metaLabel.Visible = !idle;
            card.Visible = !idle;
            messageLabel.Visible = !idle;
            breakButton.Visible = breakAvailable;
            breakBanner.Visible = BannerShowing;
            Relayout();
        }
    }

    // The big result: "IN  9:58 am" with "on time" under it, on a tint of the
    // result's colour.
    public class ResultCard : Control
    {
        private string word = string.Empty, time = string.Empty, detail = string.Empty;
        private Color accent = Theme.Success;

        public ResultCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
        }

        public void Set(string resultWord, string resultTime, string resultDetail, Color colour)
        {
            word = resultWord ?? string.Empty;
            time = resultTime ?? string.Empty;
            detail = resultDetail ?? string.Empty;
            accent = colour;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.CardColor);
            if (word.Length == 0) return;
            Draw.FillAndStrokeRounded(g, new Rectangle(0, 0, Width, Height), Theme.Px(12), Theme.TintFor(accent), accent, 2f * Theme.Scale);

            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            int lineHeight = Height * 6 / 10;
            Size wordSize = TextRenderer.MeasureText(g, word, Theme.FontResultWord, new Size(int.MaxValue, lineHeight), flags);
            Size timeSize = TextRenderer.MeasureText(g, time, Theme.FontResultTime, new Size(int.MaxValue, lineHeight), flags);
            int gap = time.Length > 0 ? Theme.Px(18) : 0;
            int total = wordSize.Width + gap + timeSize.Width;
            int x = Math.Max(Theme.Px(12), (Width - total) / 2);
            int y = Theme.Px(6);
            TextRenderer.DrawText(g, word, Theme.FontResultWord, new Rectangle(x, y, wordSize.Width + 4, lineHeight), accent, flags);
            if (time.Length > 0)
                TextRenderer.DrawText(g, time, Theme.FontResultTime, new Rectangle(x + wordSize.Width + gap, y, timeSize.Width + 4, lineHeight),
                    Theme.TextPrimary, flags);
            TextRenderer.DrawText(g, detail, Theme.FontResultDetail,
                new Rectangle(Theme.Px(10), y + lineHeight - Theme.Px(4), Width - Theme.Px(20), Height - lineHeight - Theme.Px(4)), accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
