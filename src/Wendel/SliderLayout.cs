using System;
using System.Drawing;

namespace Wendel;

/// <summary>
/// Where things are in the panel, in pixels at a given DPI. Pure geometry, kept apart from the
/// form so it can be tested without a desktop.
/// </summary>
internal readonly struct SliderLayout
{
    // Sizes in pixels at 100 % scaling.
    private const int ColumnWidth = 56;
    private const int MessageWidth = 120;
    private const int PanelHeight = 220;
    private const int AutoRowHeight = 24;
    private const int Padding = 6;
    private const int LabelHeight = 28;
    private const int TrackWidth = 8;
    private const int ThumbHeight = 8;
    private const int BoxSize = 13;
    private const int BoxInset = 5;
    private const int BoxGap = 4;

    private readonly int dpi;
    private readonly bool autoRow;

    /// <param name="monitorCount">The number of sliders.</param>
    /// <param name="dpi">The scaling.</param>
    /// <param name="autoRow">Whether any monitor has a checkbox, which adds a row below the tracks.</param>
    public SliderLayout(int monitorCount, int dpi, bool autoRow = false)
    {
        MonitorCount = monitorCount;
        this.dpi = dpi;
        this.autoRow = autoRow;
    }

    public int MonitorCount { get; }

    private int Scale(int value) => (int)Math.Round(value * dpi / 96.0);

    public Size PanelSize => new(
        MonitorCount == 0 ? Scale(MessageWidth) : Scale(Padding) * 2 + MonitorCount * Scale(ColumnWidth),
        Scale(PanelHeight) + (autoRow ? Scale(AutoRowHeight) : 0));

    /// <summary>The column at <paramref name="x"/>, the nearest one in the padding or outside the panel.</summary>
    public int ColumnAt(int x)
    {
        if (MonitorCount == 0)
            return -1;
        int column = (x - Scale(Padding)) / Scale(ColumnWidth);
        if (x < Scale(Padding))
            column = 0;
        return Math.Min(column, MonitorCount - 1);
    }

    public Rectangle LabelBounds(int column) => new(
        Scale(Padding) + column * Scale(ColumnWidth),
        Scale(Padding),
        Scale(ColumnWidth),
        Scale(LabelHeight));

    public Rectangle TrackBounds(int column)
    {
        int top = Scale(Padding) + Scale(LabelHeight) + Scale(4);
        return new Rectangle(
            Scale(Padding) + column * Scale(ColumnWidth) + (Scale(ColumnWidth) - Scale(TrackWidth)) / 2,
            top,
            Scale(TrackWidth),
            Scale(PanelHeight) - Scale(Padding) - Scale(ThumbHeight) / 2 - top);
    }

    public Rectangle ThumbBounds(int column, int percent)
    {
        var track = TrackBounds(column);
        int y = track.Bottom - (int)Math.Round(track.Height * percent / 100.0);
        int width = Scale(ColumnWidth) - Scale(20);
        return new Rectangle(track.X + (track.Width - width) / 2, y - Scale(ThumbHeight) / 2, width, Scale(ThumbHeight));
    }

    /// <summary>The percentage for a cursor at <paramref name="y"/>, clamped to the track.</summary>
    public int PercentAt(int y)
    {
        var track = TrackBounds(0);
        double percent = (track.Bottom - y) * 100.0 / track.Height;
        return Math.Max(0, Math.Min(100, (int)Math.Round(percent)));
    }

    /// <summary>Whether <paramref name="y"/> is in the row of the checkboxes, if there is one.</summary>
    public bool IsInAutoRow(int y) => autoRow && y >= Scale(PanelHeight) - Scale(Padding);

    public Rectangle AutoBoxBounds(int column)
    {
        int y = Scale(PanelHeight) + (Scale(AutoRowHeight) - Scale(Padding) - Scale(BoxSize)) / 2;
        return new Rectangle(
            Scale(Padding) + column * Scale(ColumnWidth) + Scale(BoxInset),
            y,
            Scale(BoxSize),
            Scale(BoxSize));
    }

    public Rectangle AutoTextBounds(int column)
    {
        var box = AutoBoxBounds(column);
        int left = box.Right + Scale(BoxGap);
        return new Rectangle(
            left,
            box.Y - Scale(2),
            Scale(Padding) + (column + 1) * Scale(ColumnWidth) - left,
            box.Height + Scale(4));
    }
}
