using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // The DOLLAR & Co look for every window: black, the brand yellow, a green
    // for "&" and for "in", white text. Everything is built in code (no
    // designer: this compiles with the csc.exe that ships with Windows), so the
    // palette and type scale here are what make it one product.
    //
    // Colour carries meaning on the counter and is used for nothing else:
    //   green = clocked in on time   amber = late / waiting   blue = clocked out
    //   red   = not recorded / absent / reader missing        grey = off
    public static class Theme
    {
        // ---- surfaces ----
        public static readonly Color Background = Color.FromArgb(10, 10, 10);
        public static readonly Color CardColor = Color.FromArgb(21, 21, 23);
        public static readonly Color CardRaised = Color.FromArgb(30, 30, 33);
        public static readonly Color FieldColor = Color.FromArgb(31, 31, 35);
        public static readonly Color Border = Color.FromArgb(44, 44, 49);
        public static readonly Color BorderStrong = Color.FromArgb(70, 70, 78);

        // ---- brand ----
        public static readonly Color Brand = Color.FromArgb(255, 214, 10);       // #FFD60A "DOLLAR"
        public static readonly Color BrandDark = Color.FromArgb(230, 192, 0);
        public static readonly Color BrandSoft = Color.FromArgb(46, 39, 6);
        public static readonly Color BrandGreen = Color.FromArgb(34, 197, 94);   // "&"
        public static readonly Color OnBrand = Color.FromArgb(10, 10, 10);        // text on yellow

        // ---- meaning ----
        public static readonly Color Success = Color.FromArgb(34, 197, 94);
        public static readonly Color SuccessSoft = Color.FromArgb(14, 42, 24);
        public static readonly Color Warning = Color.FromArgb(245, 158, 11);
        public static readonly Color WarningSoft = Color.FromArgb(51, 35, 10);
        public static readonly Color Info = Color.FromArgb(59, 130, 246);
        public static readonly Color InfoSoft = Color.FromArgb(15, 30, 54);
        public static readonly Color Danger = Color.FromArgb(239, 68, 68);
        public static readonly Color DangerSoft = Color.FromArgb(58, 20, 20);
        public static readonly Color Leave = Color.FromArgb(167, 139, 250);
        public static readonly Color Neutral = Color.FromArgb(113, 113, 122);
        public static readonly Color NeutralSoft = Color.FromArgb(34, 34, 38);

        // ---- text ----
        public static readonly Color TextPrimary = Color.FromArgb(244, 244, 245);
        public static readonly Color TextSecondary = Color.FromArgb(161, 161, 170);
        public static readonly Color TextMuted = Color.FromArgb(113, 113, 122);

        // ---- type ----
        public static readonly Font FontWordmark = MakeFont(new[] { "Segoe UI Black", "Arial Black" }, 19, FontStyle.Regular);
        public static readonly Font FontWordmarkSmall = MakeFont(new[] { "Segoe UI Black", "Arial Black" }, 14, FontStyle.Regular);
        public static readonly Font FontTitle = new Font("Segoe UI", 17, FontStyle.Bold);
        public static readonly Font FontSubtitle = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        public static readonly Font FontLabel = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        public static readonly Font FontBody = new Font("Segoe UI", 10.5f, FontStyle.Regular);
        public static readonly Font FontButton = new Font("Segoe UI", 10f, FontStyle.Bold);
        public static readonly Font FontSmall = new Font("Segoe UI", 9f, FontStyle.Regular);
        public static readonly Font FontSmallBold = new Font("Segoe UI", 9f, FontStyle.Bold);
        public static readonly Font FontHeadline = new Font("Segoe UI", 15, FontStyle.Bold);
        public static readonly Font FontStatus = new Font("Segoe UI", 13, FontStyle.Regular);
        public static readonly Font FontPersonName = new Font("Segoe UI", 28, FontStyle.Bold);
        public static readonly Font FontResultWord = new Font("Segoe UI", 40, FontStyle.Bold);
        public static readonly Font FontResultTime = new Font("Segoe UI", 30, FontStyle.Bold);
        public static readonly Font FontResultDetail = new Font("Segoe UI", 16, FontStyle.Bold);
        public static readonly Font FontListName = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        public static readonly Font FontListMeta = new Font("Segoe UI", 9f, FontStyle.Regular);
        public static readonly Font FontPill = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        public static readonly Font FontClock = new Font("Segoe UI", 20, FontStyle.Bold);
        public static readonly Font FontCount = new Font("Segoe UI", 20, FontStyle.Bold);
        public static readonly Font FontBlocked = new Font("Segoe UI", 26, FontStyle.Bold);
        public static readonly Font FontInitials = new Font("Segoe UI", 30, FontStyle.Bold);
        public static readonly Font FontInitialsSmall = new Font("Segoe UI", 11, FontStyle.Bold);

        private static Font MakeFont(string[] families, float size, FontStyle style)
        {
            try
            {
                var installed = FontFamily.Families.Select(f => f.Name).ToList();
                foreach (string name in families)
                    if (installed.Contains(name)) return new Font(name, size, style);
            }
            catch { }
            return new Font("Segoe UI", size, FontStyle.Bold);
        }

        // The soft tint that goes with an accent (for note boxes).
        public static Color TintFor(Color accent)
        {
            if (accent == Success) return SuccessSoft;
            if (accent == Danger) return DangerSoft;
            if (accent == Warning) return WarningSoft;
            if (accent == Info) return InfoSoft;
            if (accent == Brand) return BrandSoft;
            return NeutralSoft;
        }

        // ---- DPI ----
        //
        // app.manifest declares the process per-monitor DPI aware, so on a
        // 150 % screen the app lays itself out larger instead of Windows
        // stretching a blurry bitmap. Every window here is laid out in code
        // from 96-dpi numbers passed through Px(); fonts are in points, which
        // Windows already draws at the screen's DPI.
        private static float scale;

        public static float Scale
        {
            get
            {
                if (scale <= 0)
                {
                    try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; }
                    catch { scale = 1f; }
                    if (scale <= 0) scale = 1f;
                }
                return scale;
            }
        }

        public static int Px(int pixels)
        {
            return (int)Math.Round(pixels * Scale);
        }

        public static void PrepareForDpi(ContainerControl form)
        {
            form.AutoScaleMode = AutoScaleMode.None;
        }

        // A window laid out for 96 dpi * Scale can be taller than a small
        // screen (1366 x 768 at 125 %). Then it is shrunk to the screen and
        // scrolls, rather than running off the bottom.
        public static void FitToScreen(Form form)
        {
            try
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Size border = form.Size - form.ClientSize;
                Size outer = form.ClientSize + border;
                if (outer.Width <= area.Width && outer.Height <= area.Height) return;
                form.AutoScroll = true;
                form.AutoScrollMinSize = form.ClientSize;
                form.Size = new Size(Math.Min(outer.Width, area.Width), Math.Min(outer.Height, area.Height));
            }
            catch
            {
                // Cosmetic.
            }
        }

        // The icon embedded by build.cmd (/win32icon), for every window.
        private static Icon appIcon;
        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null)
                {
                    try { appIcon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); }
                    catch { appIcon = null; }
                }
                return appIcon;
            }
        }

        // Yellow button, black text: the one main action on a window.
        public static RoundButton PrimaryButton(string text)
        {
            var b = new RoundButton(Brand, OnBrand, BrandDark, Color.Empty);
            b.Text = text;
            return b;
        }

        // Dark button with a border, text in `accent`.
        public static RoundButton QuietButton(string text, Color accent)
        {
            var b = new RoundButton(CardRaised, accent, Color.FromArgb(42, 42, 47), BorderStrong);
            b.Text = text;
            return b;
        }

        public static TextBox TextBox()
        {
            var t = new TextBox();
            t.Font = FontBody;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = FieldColor;
            t.ForeColor = TextPrimary;
            return t;
        }

        public static Label Label(string text, Font font, Color color)
        {
            var l = new Label();
            l.Text = text;
            l.Font = font;
            l.ForeColor = color;
            l.AutoSize = false;
            l.BackColor = Color.Transparent;
            l.UseMnemonic = false; // "DOLLAR & Co" must not underline the C
            return l;
        }

        public static CheckBox CheckBox(string text)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Font = FontBody;
            c.ForeColor = TextPrimary;
            c.BackColor = Color.Transparent;
            // Standard (themed) box: a flat one draws its tick in the text
            // colour -- white on a white box, so a ticked box looked empty.
            c.FlatStyle = FlatStyle.Standard;
            c.UseMnemonic = false;
            return c;
        }

        // Grey prompt text inside an empty field. After the handle exists.
        public static void SetPlaceholder(Control field, string text)
        {
            try
            {
                if (field.IsHandleCreated) SendMessage(field.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            }
            catch { }
        }

        // Dark scrollbars on Windows 10 1809+ (a no-op elsewhere).
        public static void DarkScrollbars(Control control)
        {
            try
            {
                if (control.IsHandleCreated) SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            }
            catch { }
        }

        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string lParam);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);
    }
}
