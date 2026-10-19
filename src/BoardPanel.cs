using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DollarCoAttendance
{
    // RIGHT half of the counter window: today's staff list from
    // GET /api/desktop/board -- who is in (since when), who is late, who has
    // not come in yet, who has gone home, who is absent or on leave -- with the
    // counts across the top. Punches saved on this PC and not sent yet are
    // listed first in amber; any the server would not record once sent, in red.
    public class BoardPanel : Panel
    {
        private readonly Label titleLabel;
        private readonly Label dateLabel;
        private readonly CountStrip counts;
        private readonly ListBox list;
        private readonly Label emptyLabel;
        private readonly AlertBox note;
        private bool noteShowing;

        public BoardPanel()
        {
            BackColor = Theme.CardColor;
            DoubleBuffered = true;

            titleLabel = Theme.Label("TODAY'S STAFF", Theme.FontLabel, Theme.TextSecondary);
            dateLabel = Theme.Label(string.Empty, Theme.FontLabel, Theme.TextSecondary);
            dateLabel.TextAlign = ContentAlignment.MiddleRight;
            counts = new CountStrip();

            list = new ListBox();
            list.BorderStyle = BorderStyle.None;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = Theme.Px(60);
            list.IntegralHeight = false;
            list.BackColor = Theme.CardColor;
            list.ForeColor = Theme.TextPrimary;
            list.Font = Theme.FontBody;
            list.SelectionMode = SelectionMode.None;
            list.DrawItem += List_DrawItem;
            list.HandleCreated += delegate { Theme.DarkScrollbars(list); };

            emptyLabel = Theme.Label("Loading today's list...", Theme.FontStatus, Theme.TextSecondary);
            emptyLabel.TextAlign = ContentAlignment.MiddleCenter;

            note = new AlertBox();
            note.Visible = false;

            Controls.Add(titleLabel);
            Controls.Add(dateLabel);
            Controls.Add(counts);
            Controls.Add(list);
            Controls.Add(emptyLabel);
            Controls.Add(note);
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
            int pad = Theme.Px(18);
            int inner = w - pad * 2;
            titleLabel.SetBounds(pad, pad, inner / 2, Theme.Px(18));
            dateLabel.SetBounds(pad + inner / 2, pad, inner - inner / 2, Theme.Px(18));
            counts.SetBounds(pad, titleLabel.Bottom + Theme.Px(8), inner, Theme.Px(66));
            int listTop = counts.Bottom + Theme.Px(12);
            int noteHeight = noteShowing ? Theme.Px(58) : 0;
            note.SetBounds(pad, h - pad - Theme.Px(58), inner, Theme.Px(58));
            int listHeight = Math.Max(list.ItemHeight, h - listTop - pad - (noteShowing ? noteHeight + Theme.Px(10) : 0));
            list.SetBounds(pad, listTop, inner, listHeight);
            emptyLabel.SetBounds(pad, listTop + Theme.Px(30), inner, Theme.Px(60));
        }

        // `localRows` (queued / not recorded) go on top of the server's list.
        public void SetBoard(BoardData board, List<BoardRow> localRows)
        {
            if (InvokeRequired) { BeginInvoke(new Action<BoardData, List<BoardRow>>(SetBoard), board, localRows); return; }

            var rows = new List<BoardRow>();
            if (localRows != null) rows.AddRange(localRows);
            if (board != null) rows.AddRange(board.Rows);

            BoardCounts c = board != null ? board.Counts : new BoardCounts();
            counts.SetCounts(c);
            string day = board != null ? ShopTime.DayLabel(board.Date) : ShopTime.DayLabel(ShopTime.NowUtc);
            dateLabel.Text = day.ToUpperInvariant() + (c.Out > 0 ? "  ·  " + c.Out + " GONE HOME" : string.Empty);
            titleLabel.Text = board != null && !string.IsNullOrEmpty(board.Holiday)
                ? "TODAY'S STAFF  ·  " + board.Holiday.ToUpperInvariant()
                : "TODAY'S STAFF";

            int top = list.TopIndex;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (BoardRow row in rows) list.Items.Add(row);
            list.EndUpdate();
            if (top > 0 && top < list.Items.Count) list.TopIndex = top;

            emptyLabel.Text = board == null
                ? "Today's list has not loaded yet."
                : "No staff on today's list.";
            emptyLabel.Visible = rows.Count == 0;
            list.Visible = rows.Count > 0;
        }

        public void ShowNote(string text, Color accent)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string, Color>(ShowNote), text, accent); return; }
            noteShowing = !string.IsNullOrEmpty(text);
            note.ShowMessage(text, accent);
            note.Visible = noteShowing;
            Relayout();
        }

        private void List_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var row = list.Items[e.Index] as BoardRow;
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(Theme.CardColor)) g.FillRectangle(bg, e.Bounds);
            if (row == null) return;

            Color accent = row.Accent;
            var card = new Rectangle(e.Bounds.X, e.Bounds.Y + Theme.Px(3), e.Bounds.Width - Theme.Px(2), e.Bounds.Height - Theme.Px(6));
            Draw.FillRounded(g, card, Theme.Px(8), Theme.CardRaised);
            Draw.FillRounded(g, new Rectangle(card.X, card.Y, Theme.Px(5), card.Height), Theme.Px(2), accent);

            int left = card.X + Theme.Px(16);
            int right = card.Right - Theme.Px(12);

            // The pill and the times on the right.
            string pill = row.Pill;
            Size pillSize = TextRenderer.MeasureText(g, pill, Theme.FontPill);
            int pillWidth = pillSize.Width + Theme.Px(16);
            var pillBox = new Rectangle(right - pillWidth, card.Y + Theme.Px(8), pillWidth, Theme.Px(22));
            Draw.FillRounded(g, pillBox, Theme.Px(11), Theme.TintFor(accent) == Theme.NeutralSoft ? Theme.NeutralSoft : Theme.TintFor(accent));
            TextRenderer.DrawText(g, pill, Theme.FontPill, pillBox, accent == Theme.Neutral ? Theme.TextSecondary : accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            string times = row.TimesText;
            int timesWidth = Math.Min(Theme.Px(230), card.Width / 2);
            TextRenderer.DrawText(g, times, Theme.FontSmall,
                new Rectangle(right - timesWidth, pillBox.Bottom + Theme.Px(2), timesWidth, Theme.Px(20)), Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            int nameRight = pillBox.Left - Theme.Px(10);
            TextRenderer.DrawText(g, row.Name, Theme.FontListName,
                new Rectangle(left, card.Y + Theme.Px(6), Math.Max(0, nameRight - left), Theme.Px(26)), Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string meta = row.Designation;
            if (row.State != "queued" && row.State != "problem")
                meta = (meta.Length > 0 ? meta + "  ·  " : string.Empty) + row.ShiftText;
            TextRenderer.DrawText(g, meta, Theme.FontListMeta,
                new Rectangle(left, card.Y + Theme.Px(31), Math.Max(0, right - timesWidth - left - Theme.Px(8)), Theme.Px(18)), Theme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // The counts across the top of the board: five tiles, each a number and a word.
    public class CountStrip : Control
    {
        private BoardCounts counts = new BoardCounts();

        public CountStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
        }

        public void SetCounts(BoardCounts c)
        {
            counts = c ?? new BoardCounts();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.CardColor);
            var tiles = new[]
            {
                new KeyValuePair<string, KeyValuePair<int, Color>>("IN NOW", new KeyValuePair<int, Color>(counts.InNow, Theme.Success)),
                new KeyValuePair<string, KeyValuePair<int, Color>>("LATE", new KeyValuePair<int, Color>(counts.Late, Theme.Warning)),
                new KeyValuePair<string, KeyValuePair<int, Color>>("NOT IN YET", new KeyValuePair<int, Color>(counts.NotInYet, Theme.TextPrimary)),
                new KeyValuePair<string, KeyValuePair<int, Color>>("ABSENT", new KeyValuePair<int, Color>(counts.Absent, Theme.Danger)),
                new KeyValuePair<string, KeyValuePair<int, Color>>("ON LEAVE", new KeyValuePair<int, Color>(counts.OnLeave, Theme.Leave)),
            };
            int gap = Theme.Px(8);
            int tileWidth = (Width - gap * (tiles.Length - 1)) / tiles.Length;
            for (int i = 0; i < tiles.Length; i++)
            {
                var box = new Rectangle(i * (tileWidth + gap), 0, tileWidth, Height);
                Draw.FillRounded(g, box, Theme.Px(8), Theme.CardRaised);
                int value = tiles[i].Value.Key;
                Color colour = value > 0 ? tiles[i].Value.Value : Theme.TextMuted;
                TextRenderer.DrawText(g, value.ToString(), Theme.FontCount,
                    new Rectangle(box.X, box.Y + Theme.Px(4), box.Width, box.Height * 6 / 10), colour,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, tiles[i].Key, Theme.FontPill,
                    new Rectangle(box.X, box.Y + box.Height * 6 / 10, box.Width, box.Height * 4 / 10 - Theme.Px(4)), Theme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
