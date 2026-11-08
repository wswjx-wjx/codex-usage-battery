using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace CodexUsageBattery
{
    internal struct MotionFrame
    {
        internal float Pulse, Sweep, NumberFlash;
        internal bool Vivid;

        internal static MotionFrame At(string mode, long now, long modeStartedAt, long refreshStartedAt,
            long numberChangedAt, bool disabled, bool lowQuota)
        {
            if (mode == "static" || disabled)
                return new MotionFrame { Pulse = 0.5f, Sweep = -1, NumberFlash = 0, Vivid = false };

            bool vivid = mode == "vivid";
            long elapsed = Math.Max(0, now - modeStartedAt);
            double period = vivid ? 1900 : 4200;
            if (lowQuota) period *= 0.7;
            float amplitude = vivid ? 0.5f : 0.4f;
            float pulse = (float)(0.5 + amplitude * Math.Sin(elapsed * 2 * Math.PI / period));
            float sweep = -1;
            if (vivid)
            {
                long wave = elapsed % 3000;
                if (wave <= 1250) sweep = wave / 1250f;
            }
            long sinceRefresh = now - refreshStartedAt;
            if (refreshStartedAt >= 0 && sinceRefresh >= 0 && sinceRefresh <= 1100)
                sweep = sinceRefresh / 1100f;
            long sinceChange = now - numberChangedAt;
            float flash = numberChangedAt >= 0 && sinceChange >= 0 && sinceChange <= 700 ? 1 - sinceChange / 700f : 0;
            return new MotionFrame { Pulse = pulse, Sweep = sweep, NumberFlash = flash, Vivid = vivid };
        }
    }

    // All skins share the same quota geometry so 100%, 5H and 7D remain readable.
    internal static class ThemePainter
    {
        internal static Color Pulsed(Color color, float pulse)
        {
            float brightness = 0.43f + 0.57f * Math.Max(0, Math.Min(1, pulse));
            return Color.FromArgb(color.A, (int)(color.R * brightness), (int)(color.G * brightness), (int)(color.B * brightness));
        }

        internal static Color Accent(string theme, bool secondary)
        {
            switch (theme)
            {
                case "neon": return secondary ? Color.FromArgb(199, 70, 198) : Color.FromArgb(0, 176, 204);
                case "glass": return secondary ? Color.FromArgb(138, 104, 205) : Color.FromArgb(34, 145, 190);
                case "pixel": return secondary ? Color.FromArgb(220, 137, 27) : Color.FromArgb(31, 157, 108);
                default: return secondary ? Color.FromArgb(246, 159, 36) : Color.FromArgb(6, 189, 208);
            }
        }

        internal static void Paint(Graphics g, Rectangle bounds, int? remaining, bool stale,
            string badge, float pulse, float sweep, float flash, string theme)
        { Paint(g, bounds, remaining, stale, badge, pulse, sweep, flash, theme, false); }

        internal static void Paint(Graphics g, Rectangle bounds, int? remaining, bool stale,
            string badge, float pulse, float sweep, float flash, string theme, bool vivid)
        {
            if (theme == "mecha")
            {
                BatteryPainter.Paint(g, bounds, remaining, stale, badge, pulse, sweep, flash, vivid);
                return;
            }
            var state = g.Save();
            g.TranslateTransform(bounds.Left, bounds.Top);
            float scale = bounds.Width / (float)BatteryPainter.LogicalWidth;
            g.ScaleTransform(scale, scale);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            bool unavailable = stale || !remaining.HasValue;
            bool glass = theme == "glass", pixel = theme == "pixel";
            Color border = glass ? Color.FromArgb(48, 115, 158) : pixel ? Color.FromArgb(65, 214, 156) : Color.FromArgb(171, 74, 247);
            Color side = glass ? Color.FromArgb(120, 105, 205) : pixel ? Color.FromArgb(248, 184, 69) : Color.FromArgb(36, 220, 244);
            Color charge = glass ? Color.FromArgb(8, 154, 201) : pixel ? Color.FromArgb(77, 231, 168) : Color.FromArgb(23, 223, 242);
            if (remaining.HasValue && remaining.Value <= 20) charge = Color.FromArgb(246, 166, 44);
            if (remaining.HasValue && remaining.Value <= 10) charge = Color.FromArgb(235, 74, 94);
            if (unavailable) { border = Color.FromArgb(126, 143, 158); side = Color.FromArgb(155, 169, 183); charge = Color.FromArgb(145, 159, 171); }

            PointF[] shape = pixel ? new PointF[] {
                new PointF(3, 8), new PointF(6, 8), new PointF(6, 5), new PointF(64, 5),
                new PointF(64, 8), new PointF(67, 8), new PointF(67, 12), new PointF(71, 12),
                new PointF(71, 21), new PointF(67, 21), new PointF(67, 26), new PointF(64, 26),
                new PointF(64, 29), new PointF(6, 29), new PointF(6, 26), new PointF(3, 26)
            } : new PointF[] {
                new PointF(3, 9), new PointF(7, 4), new PointF(62, 4), new PointF(67, 9),
                new PointF(67, 12), new PointF(70, 12), new PointF(71, 14), new PointF(71, 20),
                new PointF(70, 22), new PointF(67, 22), new PointF(67, 25), new PointF(62, 29),
                new PointF(7, 29), new PointF(3, 25)
            };
            using (var path = new GraphicsPath())
            using (var body = new LinearGradientBrush(new RectangleF(3, 4, 68, 25),
                glass ? Color.FromArgb(244, 252, 255) : pixel ? Color.FromArgb(31, 47, 56) : Color.FromArgb(36, 25, 58),
                glass ? Color.FromArgb(171, 214, 234) : pixel ? Color.FromArgb(9, 19, 25) : Color.FromArgb(8, 13, 29), 90f))
            using (var outer = new Pen(glass ? Color.FromArgb(38, 94, 133) : Color.FromArgb(8, 14, 25), 1))
            {
                path.AddPolygon(shape);
                g.SmoothingMode = SmoothingMode.None;
                g.FillPath(body, path);
                g.DrawPath(outer, path);
                var clip = g.Save();
                g.SetClip(path);
                if (!pixel) g.SmoothingMode = SmoothingMode.AntiAlias;
                int glow = unavailable ? 32 : (int)(45 + 155 * Math.Max(0, Math.Min(1, pulse)));
                Color pulseBorder = unavailable ? border : Pulsed(border, pulse);
                Color pulseSide = unavailable ? side : Pulsed(side, pulse);
                using (var glowPen = new Pen(Color.FromArgb(glow, border), pixel ? 3.6f : 4.2f))
                using (var borderPen = new Pen(pulseBorder, pixel ? 1.7f : 1.35f))
                using (var sidePen = new Pen(pulseSide, pixel ? 1.7f : 1.35f))
                {
                    g.DrawLine(glowPen, 8, 6.5f, 60, 6.5f);
                    g.DrawLine(borderPen, 8, 6.5f, 60, 6.5f);
                    g.DrawLine(borderPen, 6, 9, 6, 24);
                    g.DrawLine(sidePen, 64, 9, 64, 24);
                    g.DrawLine(sidePen, 11, 27.5f, 59, 27.5f);
                }
                using (var screen = new LinearGradientBrush(new RectangleF(10, 8, 51, 18),
                    glass ? Color.FromArgb(246, 253, 255) : Color.FromArgb(7, 16, 27),
                    glass ? Color.FromArgb(213, 239, 250) : Color.FromArgb(3, 9, 17), 90f))
                using (var edge = new Pen(Color.FromArgb(glass ? 150 : 100, border), 0.7f))
                {
                    g.FillRectangle(screen, 10, 8, 51, 18);
                    g.DrawRectangle(edge, 10, 8, 51, 18);
                }
                if (pixel)
                {
                    using (var px = new SolidBrush(side))
                    { g.FillRectangle(px, 8, 8, 2, 2); g.FillRectangle(px, 61, 24, 2, 2); g.FillRectangle(px, 68, 15, 2, 4); }
                }
                else
                {
                    using (var highlight = new Pen(Color.FromArgb(glass ? 150 : 135, Color.White), 0.6f))
                        g.DrawLine(highlight, 12, 9, 57, 9);
                }

                using (var track = new SolidBrush(glass ? Color.FromArgb(183, 211, 225) : Color.FromArgb(30, 48, 60)))
                using (var fill = new SolidBrush(unavailable ? charge : Pulsed(charge, pulse)))
                {
                    for (int i = 0; i < 10; i++) g.FillRectangle(track, 14 + i * 4.4f, 23.7f, 3.5f, 2.3f);
                    if (remaining.HasValue && remaining.Value > 0)
                    {
                        var filled = g.Save();
                        g.SetClip(new RectangleF(14, 23.5f, 43.5f * Math.Max(0, Math.Min(100, remaining.Value)) / 100f, 3f), CombineMode.Intersect);
                        for (int i = 0; i < 10; i++) g.FillRectangle(fill, 14 + i * 4.4f, 23.7f, 3.5f, 2.3f);
                        g.Restore(filled);
                    }
                }
                if (!unavailable && sweep >= 0 && sweep <= 1)
                {
                    float x = 14 + 40 * sweep;
                    if (vivid)
                    {
                        var screenClip = g.Save();
                        g.SetClip(new RectangleF(10, 9, 51, 13), CombineMode.Intersect);
                        using (var glowBand = new SolidBrush(Color.FromArgb(glass ? 65 : 105, charge)))
                            g.FillPolygon(glowBand, new PointF[] { new PointF(x - 7, 9), new PointF(x, 9),
                                new PointF(x + 7, 22), new PointF(x, 22) });
                        using (var beamCore = new Pen(Color.FromArgb(glass ? 90 : 120, Color.White), 1.2f))
                            g.DrawLine(beamCore, x - 1, 9, x + 6, 22);
                        g.Restore(screenClip);
                        using (var glowLine = new Pen(Color.FromArgb(155, charge), 3f))
                            g.DrawLine(glowLine, Math.Max(11, x - 3), 7.1f, Math.Min(60, x + 5), 7.1f);
                    }
                    using (var shine = new Pen(glass ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(230, 235, 255, 255), vivid ? 1.8f : 1.1f))
                        g.DrawLine(shine, x, 8.4f, Math.Min(58, x + (vivid ? 5 : 3)), 8.4f);
                    if (vivid && remaining.HasValue && remaining.Value > 0)
                    {
                        var lit = g.Save();
                        g.SetClip(new RectangleF(14, 23.5f, 43.5f * Math.Max(0, Math.Min(100, remaining.Value)) / 100f, 3f), CombineMode.Intersect);
                        using (var gleam = new SolidBrush(Color.FromArgb(150, Color.White))) g.FillRectangle(gleam, x - 1, 23.7f, 4, 2.3f);
                        g.Restore(lit);
                    }
                }
                string value = remaining.HasValue ? remaining.Value.ToString(CultureInfo.InvariantCulture) + "%" : "--";
                using (var font = new Font("Consolas", 15.8f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var badgeFont = new Font("Consolas", badge.Length > 2 ? 5f : 6.8f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                using (var valueBrush = new SolidBrush(unavailable ? (glass ? Color.FromArgb(103, 119, 133) : Color.FromArgb(184, 198, 210)) : glass ? Color.FromArgb(17, 51, 76) : Color.FromArgb(243, 252, 255)))
                using (var glowText = new SolidBrush(Color.FromArgb(unavailable ? 12 : (int)(32 + 95 * Math.Max(0, Math.Min(1, flash))), charge)))
                using (var badgeBrush = new SolidBrush(unavailable ? Color.FromArgb(124, 139, 152) : glass ? Color.FromArgb(100, 78, 174) : side))
                {
                    g.DrawString(value, font, glowText, new RectangleF(9.3f, 6.6f, 45, 18), format);
                    g.DrawString(value, font, valueBrush, new RectangleF(8.7f, 6.1f, 45, 18), format);
                    g.DrawString(badge, badgeFont, badgeBrush, new RectangleF(52f, 10.2f, 10.5f, 10.6f), format);
                }
                g.Restore(clip);
            }
            if (stale)
                using (var warning = new SolidBrush(Color.FromArgb(255, 193, 65)))
                using (var mark = new GraphicsPath())
                { mark.AddPolygon(new PointF[] { new PointF(69, 2), new PointF(71, 4), new PointF(69, 6), new PointF(67, 4) }); g.FillPath(warning, mark); }
            g.Restore(state);
        }
    }
}
