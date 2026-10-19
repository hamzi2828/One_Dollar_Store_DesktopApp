using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    public enum PulseState
    {
        Off,    // nothing to put a finger on (or a result is showing)
        Ready,  // waiting for a finger: a slow ring travelling outwards
        Busy    // a finger is being read: a faster ring travelling inwards
    }

    // A fingerprint glyph with a pulsing ring. A still "Place your finger" line
    // does not answer the question people have at a counter -- is this thing
    // on? Motion answers it from across the shop. It stops when there is no
    // reader, so a dead counter never looks like a live one. `Slashed` draws a
    // red line through it (reader not connected).
    public class ReadyPulse : Control
    {
        private const int FrameMs = 40;
        private readonly Timer timer;
        private PulseState state = PulseState.Off;
        private double phase;
        private bool slashed;
        private Color ink = Theme.Brand;

        public ReadyPulse()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            timer = new Timer();
            timer.Interval = FrameMs;
            timer.Tick += delegate
            {
                phase += state == PulseState.Busy ? 0.045 : 0.016;
                if (phase >= 1) phase -= 1;
                Invalidate();
            };
        }

        public PulseState State
        {
            get { return state; }
            set
            {
                if (state == value) return;
                state = value;
                phase = 0;
                if (state == PulseState.Off) timer.Stop();
                else timer.Start();
                Invalidate();
            }
        }

        public bool Slashed
        {
            get { return slashed; }
            set { slashed = value; Invalidate(); }
        }

        public Color Ink
        {
            get { return ink; }
            set { ink = value; Invalidate(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && timer != null) { timer.Stop(); timer.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.CardColor);

            float size = Math.Min(Width, Height);
            var centre = new PointF(Width / 2f, Height / 2f);
            Color colour = state == PulseState.Off && !slashed ? Theme.Neutral : (slashed ? Theme.Neutral : ink);

            if (state != PulseState.Off)
            {
                double t = state == PulseState.Busy ? 1 - phase : phase;
                float minRadius = size * 0.36f;
                float maxRadius = size * 0.48f;
                float radius = minRadius + (maxRadius - minRadius) * (float)t;
                int alpha = (int)(170 * (1 - t));
                if (alpha > 0)
                {
                    using (var pen = new Pen(Color.FromArgb(alpha, colour), 3f * Theme.Scale))
                        g.DrawEllipse(pen, centre.X - radius, centre.Y - radius, radius * 2, radius * 2);
                }
            }

            // Concentric arcs, taller than wide and nearly closed: a fingertip,
            // not a wifi symbol.
            float unit = size * 0.062f;
            using (var pen = new Pen(colour, 2.8f * Theme.Scale))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                for (int i = 0; i < 5; i++)
                {
                    float rx = unit * (i + 1) * 0.74f;
                    float ry = unit * (i + 1) * 1.00f;
                    var bounds = new RectangleF(centre.X - rx, centre.Y - ry, rx * 2, ry * 2);
                    g.DrawArc(pen, bounds, 110 + i * 6, 320 - i * 12);
                }
                var core = new RectangleF(centre.X - unit * 0.34f, centre.Y - unit * 0.44f, unit * 0.68f, unit * 0.88f);
                g.DrawArc(pen, core, 110, 320);
            }

            if (slashed)
            {
                float r = size * 0.40f;
                using (var pen = new Pen(Theme.Danger, 6f * Theme.Scale))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawEllipse(pen, centre.X - r, centre.Y - r, r * 2, r * 2);
                    float d = r * 0.7071f;
                    g.DrawLine(pen, centre.X - d, centre.Y - d, centre.X + d, centre.Y + d);
                }
            }
        }
    }
}
