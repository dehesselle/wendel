using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Wendel;

/// <summary>
/// Drawn by code so the repository needs no binary assets.
/// </summary>
internal static class Artwork
{
    public static readonly Color Accent = Color.FromArgb(0xE0, 0xA8, 0x2E);
    public static readonly Color Panel = Color.FromArgb(0x1E, 0x1E, 0x22);
    public static readonly Color Track = Color.FromArgb(0x4A, 0x4A, 0x52);
    public static readonly Color Inactive = Color.FromArgb(0x70, 0x70, 0x78);
    public static readonly Color DisabledThumb = Color.FromArgb(0xA0, 0xA0, 0xA8);
    public static readonly Color Busy = Color.FromArgb(0x2E, 0x8B, 0xE0);

    /// <summary>A checkmark filling <paramref name="bounds"/>.</summary>
    public static void DrawCheckmark(Graphics g, RectangleF bounds, Color color, float penWidth)
    {
        using var pen = new Pen(color, penWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new PointF(bounds.X + bounds.Width * 0.2f, bounds.Y + bounds.Height * 0.55f),
            new PointF(bounds.X + bounds.Width * 0.42f, bounds.Y + bounds.Height * 0.76f),
            new PointF(bounds.X + bounds.Width * 0.8f, bounds.Y + bounds.Height * 0.26f),
        });
    }

    /// <summary>A sun: a disc with eight rays, filling <paramref name="bounds"/>.</summary>
    public static void DrawSun(Graphics g, RectangleF bounds, Color color)
    {
        float size = Math.Min(bounds.Width, bounds.Height);
        float cx = bounds.X + bounds.Width / 2;
        float cy = bounds.Y + bounds.Height / 2;
        float disc = size * 0.26f;
        using (var brush = new SolidBrush(color))
            g.FillEllipse(brush, cx - disc, cy - disc, disc * 2, disc * 2);
        using var pen = new Pen(color, size * 0.09f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            float dx = (float)Math.Cos(angle);
            float dy = (float)Math.Sin(angle);
            g.DrawLine(pen, cx + dx * size * 0.36f, cy + dy * size * 0.36f, cx + dx * size * 0.46f, cy + dy * size * 0.46f);
        }
    }

    /// <summary>
    /// The app icon at <paramref name="size"/> pixels, used for the tray at runtime and for the
    /// exe by the build (tools/IconGen).
    /// </summary>
    public static Bitmap RenderAppIcon(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        DrawSun(g, new RectangleF(0, 0, size, size), Accent);
        return bitmap;
    }

    public static Icon CreateTrayIcon()
    {
        using var bitmap = RenderAppIcon(32);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
