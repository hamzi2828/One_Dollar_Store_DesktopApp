using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // A round picture of the person: their photo from the POS when there is
    // one, otherwise their initials on the brand yellow. The initials show at
    // once; the photo replaces them when (if) it has downloaded.
    public class Avatar : Control
    {
        private string initials = "?";
        private Image photo;
        private string photoUrl;
        private Color ring = Theme.Brand;

        public Avatar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
        }

        public Color Ring
        {
            get { return ring; }
            set { ring = value; Invalidate(); }
        }

        // `url` may be null; the photo is fetched in the background.
        public void ShowPerson(string name, string url)
        {
            initials = StaffMember.InitialsOf(name);
            photoUrl = url;
            photo = PhotoCache.TryGet(url);
            Invalidate();
            if (photo == null && !string.IsNullOrEmpty(url))
            {
                string wanted = url;
                PhotoCache.Fetch(url, delegate (Image image)
                {
                    if (image == null || IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (photoUrl != wanted) return; // someone else is showing now
                            photo = image;
                            Invalidate();
                        }));
                    }
                    catch { }
                });
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Parent != null ? Parent.BackColor : Theme.CardColor);

            int size = Math.Min(Width, Height) - 1;
            int ringWidth = Math.Max(2, size / 28);
            var outer = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
            var inner = Rectangle.Inflate(outer, -ringWidth - 1, -ringWidth - 1);

            using (var ringBrush = new SolidBrush(ring)) g.FillEllipse(ringBrush, outer);

            if (photo != null)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(inner);
                    g.SetClip(path);
                    // Cover the circle, cropping the longer side.
                    float s = Math.Max((float)inner.Width / photo.Width, (float)inner.Height / photo.Height);
                    float w = photo.Width * s, h = photo.Height * s;
                    g.DrawImage(photo, inner.X + (inner.Width - w) / 2, inner.Y + (inner.Height - h) / 2, w, h);
                    g.ResetClip();
                }
                return;
            }

            using (var fill = new SolidBrush(Theme.BrandSoft)) g.FillEllipse(fill, inner);
            Font font = size > Theme.Px(70) ? Theme.FontInitials : Theme.FontInitialsSmall;
            TextRenderer.DrawText(g, initials, font, inner, Theme.Brand,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // Employee photos downloaded once per session and kept in memory (a shop
    // has a few dozen people). Failures are remembered so a broken link is not
    // asked for at every scan.
    public static class PhotoCache
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
        private static readonly HashSet<string> failed = new HashSet<string>();
        private static readonly HashSet<string> pending = new HashSet<string>();

        public static Image TryGet(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (gate)
            {
                Image image;
                return images.TryGetValue(url, out image) ? image : null;
            }
        }

        // Calls `done` (on a pool thread) with the image, or null.
        public static void Fetch(string url, Action<Image> done)
        {
            if (string.IsNullOrEmpty(url)) { if (done != null) done(null); return; }
            lock (gate)
            {
                Image cached;
                if (images.TryGetValue(url, out cached)) { if (done != null) done(cached); return; }
                if (failed.Contains(url)) { if (done != null) done(null); return; }
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                Image image = null;
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Timeout = 15000;
                    request.ReadWriteTimeout = 15000;
                    request.UserAgent = AppInfo.ClientId;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        if (buffer.Length > 0 && buffer.Length < 8 * 1024 * 1024)
                        {
                            buffer.Position = 0;
                            using (Image raw = Image.FromStream(buffer))
                                image = Shrink(raw, 256);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("Photo not loaded (" + ex.Message + ")");
                }

                lock (gate)
                {
                    if (image != null) images[url] = image;
                    else failed.Add(url);
                    pending.Remove(url);
                }
                if (done != null)
                {
                    try { done(image); }
                    catch (Exception ex) { Log.Error("Photo could not be shown", ex); }
                }
            });
        }

        // Fetch everyone's photo in the background so a match shows it at once.
        public static void Prefetch(IEnumerable<string> urls)
        {
            foreach (string url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                lock (gate)
                {
                    if (images.ContainsKey(url) || failed.Contains(url) || pending.Contains(url)) continue;
                    pending.Add(url);
                }
                Fetch(url, null);
            }
        }

        // A copy no bigger than `max` px (a camera photo can be 4000 px wide).
        private static Image Shrink(Image raw, int max)
        {
            float s = Math.Min(1f, (float)max / Math.Max(raw.Width, raw.Height));
            int w = Math.Max(1, (int)(raw.Width * s)), h = Math.Max(1, (int)(raw.Height * s));
            var copy = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(raw, 0, 0, w, h);
            }
            return copy;
        }
    }
}
