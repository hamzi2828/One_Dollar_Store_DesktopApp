using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // Owner-drawn building blocks. Stock WinForms chrome (grey 3D buttons,
    // white fields, system-blue list rows) would break the black-and-yellow
    // look, so the few surfaces anyone looks at are painted here.
    internal static class Draw
    {
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0 || bounds.Width < radius * 2 || bounds.Height < radius * 2)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Rectangle bounds, int radius, Color fill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRect(bounds, radius))
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);
        }

        public static void FillAndStrokeRounded(Graphics g, Rectangle bounds, int radius, Color fill, Color border, float width)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var inner = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            using (GraphicsPath path = RoundedRect(inner, radius))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var pen = new Pen(border, width)) g.DrawPath(pen, path);
            }
        }

        // The shop's logo (assets\logo.png from the website, embedded by build.cmd,
        // transparent background). Null only if the build left it out.
        private static Image logo;
        private static bool logoTried;
        public static Image Logo
        {
            get
            {
                if (!logoTried)
                {
                    logoTried = true;
                    try
                    {
                        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DollarCo.Logo.png"))
                            if (s != null) logo = new Bitmap(s);
                    }
                    catch { logo = null; }
                }
                return logo;
            }
        }

        // Draws the DOLLAR & Co logo in a band `height` tall, its letters about as
        // tall as the old text wordmark. Returns the width drawn.
        public static int Wordmark(Graphics g, Font font, Point at, int height)
        {
            Image img = Logo;
            if (img == null) return TextWordmark(g, font, at, height);
            int h = (int)Math.Round(height * 0.74);
            int w = (int)Math.Round(h * (double)img.Width / img.Height);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(img, new Rectangle(at.X, at.Y + (height - h) / 2, w, h));
            return w;
        }

        public static int WordmarkWidth(Graphics g, Font font, int height)
        {
            Image img = Logo;
            if (img == null) return TextWordmarkWidth(g, font);
            int h = (int)Math.Round(height * 0.74);
            return (int)Math.Round(h * (double)img.Width / img.Height);
        }

        // Fallback: "DOLLAR" yellow, "&" green, "Co" white, as text.
        private static int TextWordmark(Graphics g, Font font, Point at, int height)
        {
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix;
            string[] parts = { "DOLLAR", " & ", "Co" };
            Color[] colours = { Theme.Brand, Theme.BrandGreen, Color.White };
            int x = at.X;
            for (int i = 0; i < parts.Length; i++)
            {
                Size size = TextRenderer.MeasureText(g, parts[i], font, new Size(int.MaxValue, height), flags);
                TextRenderer.DrawText(g, parts[i], font, new Rectangle(x, at.Y, size.Width + 2, height), colours[i], flags);
                x += size.Width;
            }
            return x - at.X;
        }

        private static int TextWordmarkWidth(Graphics g, Font font)
        {
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int width = 0;
            foreach (string part in new[] { "DOLLAR", " & ", "Co" })
                width += TextRenderer.MeasureText(g, part, font, new Size(int.MaxValue, 100), flags).Width;
            return width;
        }
    }

    // The black band across the top of every window: the wordmark, the
    // window's name, and a thin yellow rule underneath. MainForm puts its
    // clock and status labels on top of it.
    public class HeaderBand : Panel
    {
        private readonly string title;
        private string subtitle;
        private readonly bool large;

        public string Subtitle
        {
            get { return subtitle; }
            set { subtitle = value ?? string.Empty; Invalidate(); }
        }

        public HeaderBand(string title, string subtitle, bool large)
        {
            this.title = title ?? string.Empty;
            this.subtitle = subtitle ?? string.Empty;
            this.large = large;
            BackColor = Color.Black;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Font mark = large ? Theme.FontWordmark : Theme.FontWordmarkSmall;
            int left = Theme.Px(22);
            int top = Theme.Px(large ? 10 : 8);
            int markHeight = Theme.Px(large ? 38 : 30);

            int width = Draw.Wordmark(g, mark, new Point(left, top), markHeight);
            TextRenderer.DrawText(g, title, large ? Theme.FontHeadline : Theme.FontButton,
                new Rectangle(left + width + Theme.Px(12), top, Theme.Px(400), markHeight), Theme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, subtitle, Theme.FontSmall,
                new Rectangle(left, top + markHeight, Width / 2, Theme.Px(20)), Theme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            using (var rule = new SolidBrush(Theme.Brand))
                g.FillRectangle(rule, 0, Height - Theme.Px(3), Width, Theme.Px(3));
        }
    }

    // The rounded well a TextBox sits in. The box has no border of its own, so
    // this is what reads as the field, and it turns yellow while in use.
    public class FieldFrame : Panel
    {
        private bool active;
        public bool Active
        {
            get { return active; }
            set { active = value; Invalidate(); }
        }

        public FieldFrame()
        {
            BackColor = Theme.FieldColor;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Background);
            Draw.FillAndStrokeRounded(e.Graphics, new Rectangle(0, 0, Width, Height), Theme.Px(6),
                Theme.FieldColor, active ? Theme.Brand : Theme.BorderStrong, active ? 1.6f : 1f);
        }

        // Puts `inner` (a TextBox) in a frame at the given (already scaled)
        // bounds and keeps it centred whatever height it settles on.
        public static FieldFrame Wrap(Control inner, int x, int y, int width, int height)
        {
            var frame = new FieldFrame();
            frame.SetBounds(x, y, width, height);
            frame.Controls.Add(inner);
            EventHandler relayout = delegate
            {
                inner.Width = frame.Width - Theme.Px(20);
                inner.Location = new Point(Theme.Px(10), Math.Max(0, (frame.Height - inner.Height) / 2));
            };
            relayout(null, EventArgs.Empty);
            inner.SizeChanged += relayout;
            frame.SizeChanged += relayout;
            inner.Enter += delegate { frame.Active = true; };
            inner.Leave += delegate { frame.Active = false; };
            frame.Click += delegate { inner.Focus(); };
            return frame;
        }
    }

    // Capture progress as filled segments: "how far through" at a glance.
    public class SegmentBar : Control
    {
        private int total = 4;
        private int completed;

        public int Total { get { return total; } set { total = Math.Max(1, value); Invalidate(); } }
        public int Completed { get { return completed; } set { completed = value; Invalidate(); } }

        public SegmentBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.CardColor;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.CardColor);
            int gap = Theme.Px(8);
            int segment = (Width - gap * (total - 1)) / total;
            for (int i = 0; i < total; i++)
            {
                var bounds = new Rectangle(i * (segment + gap), 0, segment, Height);
                Draw.FillRounded(e.Graphics, bounds, Height / 2, i < completed ? Theme.Success : Theme.BorderStrong);
            }
        }
    }

    // A message as a tinted box with an accent bar down the left.
    public class AlertBox : Control
    {
        private Color accent = Theme.Neutral;
        private Color tint = Theme.NeutralSoft;
        private string message = string.Empty;
        private Font messageFont = Theme.FontBody;

        public AlertBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
        }

        public string Message { get { return message; } }

        public Font MessageFont
        {
            get { return messageFont; }
            set { messageFont = value ?? Theme.FontBody; Invalidate(); }
        }

        public void ShowMessage(string text, Color accentColor)
        {
            message = text ?? string.Empty;
            accent = accentColor;
            tint = Theme.TintFor(accentColor);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.CardColor);
            if (message.Length == 0) return;
            Draw.FillRounded(e.Graphics, new Rectangle(0, 0, Width, Height), Theme.Px(6), tint);
            Draw.FillRounded(e.Graphics, new Rectangle(0, 0, Theme.Px(4), Height), Theme.Px(2), accent);
            TextRenderer.DrawText(e.Graphics, message, messageFont,
                new Rectangle(Theme.Px(16), Theme.Px(6), Width - Theme.Px(26), Height - Theme.Px(12)), Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // Flat rounded button with a hover state. `Selected` draws a yellow ring
    // (the finger buttons in the enrolment window).
    public class RoundButton : Button
    {
        private Color baseColor;
        private Color hoverColor;
        private Color textColor;
        private Color borderColor;
        private bool hovering;
        private bool selected;
        private string badge;

        public RoundButton(Color background, Color foreground, Color hover, Color border)
        {
            baseColor = background;
            textColor = foreground;
            hoverColor = hover;
            borderColor = border;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.FontButton;
            Cursor = Cursors.Hand;
            UseMnemonic = false;
            MouseEnter += delegate { hovering = true; Invalidate(); };
            MouseLeave += delegate { hovering = false; Invalidate(); };
        }

        public bool Selected
        {
            get { return selected; }
            set { selected = value; Invalidate(); }
        }

        // A small word under the text ("ON FILE").
        public string Badge
        {
            get { return badge; }
            set { badge = value; Invalidate(); }
        }

        public void SetColours(Color background, Color foreground, Color hover, Color border)
        {
            baseColor = background;
            textColor = foreground;
            hoverColor = hover;
            borderColor = border;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var bounds = new Rectangle(0, 0, Width, Height);
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Background);

            Color fill = Enabled ? (hovering ? hoverColor : baseColor) : Theme.NeutralSoft;
            Color ink = Enabled ? textColor : Theme.TextMuted;
            int radius = Theme.Px(7);

            if (selected)
                Draw.FillAndStrokeRounded(e.Graphics, bounds, radius, fill, Theme.Brand, 2.4f * Theme.Scale);
            else if (borderColor == Color.Empty)
                Draw.FillRounded(e.Graphics, bounds, radius, fill);
            else
                Draw.FillAndStrokeRounded(e.Graphics, bounds, radius, fill, Enabled ? borderColor : Theme.Border, 1f);

            if (string.IsNullOrEmpty(badge))
            {
                TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                return;
            }
            var top = new Rectangle(0, 0, Width, Height * 54 / 100);
            var bottom = new Rectangle(0, Height * 56 / 100, Width, Height * 44 / 100);
            TextRenderer.DrawText(e.Graphics, Text, Font, top, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, badge, Theme.FontPill, bottom, Enabled ? Theme.Success : Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }
    }

    // A small filled circle: the reader's state in the header.
    public class StatusDot : Control
    {
        private Color dotColor = Theme.Neutral;

        public Color DotColor
        {
            get { return dotColor; }
            set { dotColor = value; Invalidate(); }
        }

        public StatusDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Black;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Color.Black);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(dotColor))
                e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
        }
    }
}
