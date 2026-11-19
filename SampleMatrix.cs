using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace EtherCatBench
{
    sealed class SampleMatrix : Control
    {
        static readonly Color[] Palette =
        {
            Color.FromArgb(232, 244, 233), Color.FromArgb(214, 236, 216),
            Color.FromArgb(190, 226, 194), Color.FromArgb(160, 212, 166),
            Color.FromArgb(126, 194, 134), Color.FromArgb(96, 170, 108),
            Color.FromArgb(70, 140, 86), Color.FromArgb(48, 110, 68)
        };

        readonly int sampleCount;
        readonly int columns;
        byte[] samples;

        public SampleMatrix(int sampleCount, int columns)
        {
            if (sampleCount <= 0) throw new ArgumentOutOfRangeException("sampleCount");
            if (columns <= 0) throw new ArgumentOutOfRangeException("columns");
            this.sampleCount = sampleCount;
            this.columns = columns;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            BackColor = Color.FromArgb(245, 247, 250);
        }

        public void ShowSamples(byte[] samples)
        {
            if (samples != null && samples.Length != sampleCount)
                throw new ArgumentException("Exact sample length required.", "samples");
            this.samples = samples == null ? null : (byte[])samples.Clone();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Rectangle client = ClientRectangle;
            if (client.Width <= 0 || client.Height <= 0) return;

            int rows = (sampleCount + columns - 1) / columns;
            int gap = 2;
            int margin = 4;
            int availW = Math.Max(1, client.Width - margin * 2 - gap * (columns - 1));
            int availH = Math.Max(1, client.Height - margin * 2 - gap * (rows - 1));
            int cellW = Math.Max(1, availW / columns);
            int cellH = Math.Max(1, availH / rows);

            for (int i = 0; i < sampleCount; i++)
            {
                int row = i / columns;
                int col = i % columns;
                Rectangle cell = new Rectangle(
                    margin + col * (cellW + gap),
                    margin + row * (cellH + gap), cellW, cellH);
                cell = Rectangle.Intersect(cell, client);
                if (cell.Width <= 0 || cell.Height <= 0) continue;

                bool hasData = samples != null;
                byte value = hasData ? samples[i] : (byte)0;
                Color back = hasData
                    ? Palette[value >> 5]
                    : Color.FromArgb(225, 228, 232);
                using (var brush = new SolidBrush(back))
                    g.FillRectangle(brush, cell);

                bool dark = hasData && value >= 224;
                Color labelColor = dark ? Color.FromArgb(225, 235, 228)
                    : Color.FromArgb(70, 75, 80);
                Color valueColor = dark ? Color.White : Color.FromArgb(30, 35, 40);
                string label = (i + 1).ToString("D2", CultureInfo.InvariantCulture);
                string text = hasData
                    ? value.ToString(CultureInfo.InvariantCulture)
                    : "-";

                if (cell.Height < Font.Height * 2 + 2)
                {
                    TextRenderer.DrawText(g, label + ": " + text, Font, cell, valueColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    continue;
                }

                int labelHeight = Font.Height;
                Rectangle labelRect = new Rectangle(cell.X + 2, cell.Y + 1,
                    Math.Max(0, cell.Width - 4), Math.Max(0, labelHeight));
                Rectangle valueRect = new Rectangle(cell.X, cell.Y + labelHeight,
                    cell.Width, Math.Max(0, cell.Height - labelHeight));

                if (labelRect.Width > 0 && labelRect.Height > 0)
                    TextRenderer.DrawText(g, label, Font, labelRect, labelColor,
                        TextFormatFlags.Left | TextFormatFlags.Top |
                        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (valueRect.Width > 0 && valueRect.Height > 0)
                    TextRenderer.DrawText(g, text, Font, valueRect, valueColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
