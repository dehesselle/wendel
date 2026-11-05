using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Wendel;

/// <summary>
/// A thin bar at a screen edge (or a small square in the corner) that grows into a panel with
/// one brightness slider per monitor while the cursor is over it. One window does both, anchored
/// to the edge, so the cursor that opened the panel is inside it.
/// </summary>
internal sealed class BarForm : Form
{
    // Sizes in pixels at 100 % scaling.
    private const int StripLength = 140;
    private const int CornerSize = 24;

    private const double PanelOpacity = 0.96;
    private const double MaxCollapsedOpacity = 0.98;
    private const int OpenDelayMilliseconds = 120;
    private const int CloseDelayMilliseconds = 400;
    private const int AnimationMilliseconds = 150;
    private const int SettleMilliseconds = 600;
    private const int WheelNotch = 120;
    private const int AppBarMessage = NativeMethods.WM_APP + 1;

    // Broadcast when Explorer (re)starts; appbar registrations are lost with the old one.
    private static readonly int TaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    private enum Phase
    {
        Collapsed,
        Opening,
        Open,
        Closing,
    }

    private readonly Timer frameTimer;
    private readonly Timer settleTimer;

    // What the user asked for, shown instead of the monitor's last reported value until
    // things settle, because reports lag behind a drag by the write rate limit.
    private readonly Dictionary<int, int> requested = new();
    private readonly Dictionary<int, bool> requestedAuto = new();

    private IReadOnlyList<MonitorState> monitors = Array.Empty<MonitorState>();
    private bool scanned;
    private BarPosition position = BarPosition.Left;
    private int thickness = 8;
    private int wheelStep = 5;
    private double collapsedOpacity = 0.5;
    private bool fullScreenActive;
    private int dpi = 96;
    private Font? font;

    private Phase phase;
    private double progress;
    private int lastTick;
    private int hoverStart;
    private int lastInside;
    private int dragColumn = -1;
    private int wheelRemainder;

    /// <summary>The panel starts to open; a good moment to look at the monitors again.</summary>
    public event Action? Opening;

    /// <summary>The user wants a monitor (its id) at a brightness in percent.</summary>
    public event Action<int, int>? PercentRequested;

    /// <summary>The user wants automatic brightness on or off for a monitor (its id).</summary>
    public event Action<int, bool>? AutoRequested;

    public BarForm()
    {
        frameTimer = new Timer { Interval = 15 };
        frameTimer.Tick += OnFrame;
        settleTimer = new Timer { Interval = SettleMilliseconds };
        settleTimer.Tick += OnSettled;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        AllowTransparency = true;
        DoubleBuffered = true;
        Text = "wendel";

        SystemEvents.DisplaySettingsChanged += OnSystemLayoutChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void Apply(Settings settings)
    {
        position = settings.BarPosition;
        thickness = settings.EffectiveThickness;
        wheelStep = settings.EffectiveWheelStep;
        collapsedOpacity = Math.Min(MaxCollapsedOpacity, settings.EffectiveOpacity);
        Relayout();
    }

    /// <summary>Takes the monitors as the worker reports them. Sets <c>scanned</c>: until the first report the panel says it is still looking.</summary>
    public void SetMonitors(IReadOnlyList<MonitorState> value)
    {
        monitors = value;
        scanned = true;
        if (dragColumn >= monitors.Count)
            dragColumn = -1;
        Relayout();
    }

    private int Scale(int value) => (int)Math.Round(value * dpi / 96.0);

    private SliderLayout Sliders => new(monitors.Count, dpi, monitors.Any(m => m.Auto is not null));

    private Rectangle StripBounds(Rectangle area)
    {
        Size size = position switch
        {
            BarPosition.Top => new Size(Scale(StripLength), Scale(thickness)),
            BarPosition.BottomRightCorner => new Size(Scale(CornerSize), Scale(CornerSize)),
            _ => new Size(Scale(thickness), Scale(StripLength)),
        };
        return AtEdge(area, size);
    }

    private Rectangle AtEdge(Rectangle area, Size size)
    {
        int centredX = area.Left + (area.Width - size.Width) / 2;
        int centredY = area.Top + (area.Height - size.Height) / 2;
        Point location = position switch
        {
            BarPosition.Right => new Point(area.Right - size.Width, centredY),
            BarPosition.Top => new Point(centredX, area.Top),
            BarPosition.BottomRightCorner => new Point(area.Right - size.Width, area.Bottom - size.Height),
            _ => new Point(area.Left, centredY),
        };
        return new Rectangle(location, size);
    }

    /// <summary>Where the fully open panel is on the screen.</summary>
    private Rectangle PanelBounds => AtEdge(Screen.PrimaryScreen.WorkingArea, Sliders.PanelSize);

    /// <summary>Sets window bounds, opacity and color for the current monitors, settings and animation.</summary>
    private void Relayout()
    {
        var area = Screen.PrimaryScreen.WorkingArea;
        int newDpi = NativeMethods.GetDpiAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
        if (newDpi != dpi || font is null)
        {
            dpi = newDpi;
            font?.Dispose();
            font = new Font("Segoe UI", Scale(13), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // Smoothstep: starts and ends gently.
        double t = progress * progress * (3 - 2 * progress);
        Rectangle strip = StripBounds(area);
        Rectangle panel = PanelBounds;
        Bounds = new Rectangle(
            Interpolate(strip.X, panel.X, t),
            Interpolate(strip.Y, panel.Y, t),
            Interpolate(strip.Width, panel.Width, t),
            Interpolate(strip.Height, panel.Height, t));

        Color from = scanned && monitors.Count == 0 ? Artwork.Inactive : Artwork.Accent;
        BackColor = Color.FromArgb(
            Interpolate(from.R, Artwork.Panel.R, t),
            Interpolate(from.G, Artwork.Panel.G, t),
            Interpolate(from.B, Artwork.Panel.B, t));
        Opacity = collapsedOpacity + (PanelOpacity - collapsedOpacity) * t;
        Invalidate();
    }

    private static int Interpolate(int from, int to, double t) => from + (int)Math.Round((to - from) * t);

    /// <summary>
    /// Hides the collapsed bar while a full-screen app is active, like the taskbar. Hiding keeps
    /// the window handle.
    /// </summary>
    private void UpdateVisibility()
    {
        Visible = !(fullScreenActive && phase == Phase.Collapsed);
    }

    private void StartFrames()
    {
        lastTick = Environment.TickCount;
        frameTimer.Start();
    }

    private void Open()
    {
        if (phase is Phase.Open or Phase.Opening)
            return;
        bool wasCollapsed = phase == Phase.Collapsed;
        phase = Phase.Opening;
        lastInside = Environment.TickCount;
        wheelRemainder = 0;
        StartFrames();
        if (wasCollapsed)
            Opening?.Invoke();
    }

    /// <summary>
    /// Runs while anything can change: from the first hover until the panel has closed again.
    /// Checking the cursor here rather than relying on leave messages also covers a window
    /// that moves under a resting cursor.
    /// </summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        int now = Environment.TickCount;
        int elapsed = unchecked(now - lastTick);
        lastTick = now;
        bool inside = Bounds.Contains(Cursor.Position);
        if (inside)
            lastInside = now;

        switch (phase)
        {
            case Phase.Collapsed:
                if (!inside)
                    frameTimer.Stop();
                else if (unchecked(now - hoverStart) >= OpenDelayMilliseconds)
                    Open();
                return;
            case Phase.Opening:
            case Phase.Open:
                if (dragColumn < 0 && !inside && unchecked(now - lastInside) >= CloseDelayMilliseconds)
                    phase = Phase.Closing;
                break;
            case Phase.Closing:
                if (inside)
                    phase = Phase.Opening;
                break;
        }

        double step = (double)elapsed / AnimationMilliseconds;
        if (phase == Phase.Opening)
        {
            progress = Math.Min(1, progress + step);
            if (progress >= 1)
                phase = Phase.Open;
            Relayout();
        }
        else if (phase == Phase.Closing)
        {
            progress = Math.Max(0, progress - step);
            if (progress <= 0)
            {
                phase = Phase.Collapsed;
                frameTimer.Stop();
            }
            Relayout();
            UpdateVisibility();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (phase == Phase.Collapsed)
            return;

        // Paint the finished panel where it will end up; the window reveals it as it grows.
        var g = e.Graphics;
        Rectangle panel = PanelBounds;
        g.TranslateTransform(panel.X - Left, panel.Y - Top);
        var layout = Sliders;

        using (var border = new Pen(Artwork.Track, Math.Max(1, Scale(1))))
            g.DrawRectangle(border, 0, 0, panel.Width - 1, panel.Height - 1);

        const TextFormatFlags centred = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.PreserveGraphicsTranslateTransform;
        if (monitors.Count == 0)
        {
            TextRenderer.DrawText(g, scanned ? "No DDC/CI monitor" : "Looking for monitors…", font,
                new Rectangle(Point.Empty, panel.Size), Color.White, centred | TextFormatFlags.WordBreak);
            return;
        }

        for (int column = 0; column < monitors.Count; column++)
        {
            var monitor = monitors[column];
            int percent = DisplayedPercent(monitor);
            bool auto = DisplayedAuto(monitor);
            TextRenderer.DrawText(g, $"{percent} %", font, layout.LabelBounds(column), Color.White, centred | TextFormatFlags.SingleLine);

            Rectangle track = layout.TrackBounds(column);
            Rectangle thumb = layout.ThumbBounds(column, percent);
            int centre = thumb.Top + thumb.Height / 2;
            using (var background = new SolidBrush(Artwork.Track))
                g.FillRectangle(background, track);
            using (var fill = new SolidBrush(auto ? Artwork.Inactive : Artwork.Accent))
                g.FillRectangle(fill, track.X, centre, track.Width, track.Bottom - centre);
            using (var thumbBrush = new SolidBrush(auto ? Artwork.DisabledThumb : Color.White))
                g.FillRectangle(thumbBrush, thumb);

            if (monitor.Auto is not null)
                PaintCheckbox(g, layout, column, auto);
        }
    }

    private void PaintCheckbox(Graphics g, SliderLayout layout, int column, bool isChecked)
    {
        Rectangle box = layout.AutoBoxBounds(column);
        if (isChecked)
        {
            using var fill = new SolidBrush(Artwork.Accent);
            g.FillRectangle(fill, box);
            var smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Artwork.DrawCheckmark(g, box, Artwork.Panel, Math.Max(1, Scale(2)));
            g.SmoothingMode = smoothing;
        }
        else
        {
            using var border = new Pen(Artwork.Inactive, Math.Max(1, Scale(1)));
            g.DrawRectangle(border, box.X, box.Y, box.Width - 1, box.Height - 1);
        }
        TextRenderer.DrawText(g, "Auto", font, layout.AutoTextBounds(column), Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsTranslateTransform);
    }

    private int DisplayedPercent(MonitorState monitor) =>
        requested.TryGetValue(monitor.Id, out int percent) ? percent : monitor.Percent;

    private bool DisplayedAuto(MonitorState monitor) =>
        requestedAuto.TryGetValue(monitor.Id, out bool auto) ? auto : monitor.Auto == true;

    /// <summary>The cursor relative to the finished panel, valid while the window is still growing.</summary>
    private Point CursorInPanel()
    {
        Point cursor = Cursor.Position;
        Rectangle panel = PanelBounds;
        return new Point(cursor.X - panel.X, cursor.Y - panel.Y);
    }

    private void Request(int column, int percent)
    {
        var monitor = monitors[column];
        percent = Math.Max(0, Math.Min(100, percent));
        settleTimer.Stop();
        settleTimer.Start();
        if (DisplayedPercent(monitor) == percent)
            return;
        requested[monitor.Id] = percent;
        Invalidate();
        PercentRequested?.Invoke(monitor.Id, percent);
    }

    private void ToggleAuto(int column)
    {
        var monitor = monitors[column];
        if (monitor.Auto is null)
            return;
        bool on = !DisplayedAuto(monitor);
        settleTimer.Stop();
        settleTimer.Start();
        requestedAuto[monitor.Id] = on;
        // A brightness the user just set no longer applies once the sensor is in charge.
        if (on)
            requested.Remove(monitor.Id);
        Invalidate();
        AutoRequested?.Invoke(monitor.Id, on);
    }

    private void OnSettled(object? sender, EventArgs e)
    {
        if (dragColumn >= 0)
            return;
        settleTimer.Stop();
        requested.Clear();
        requestedAuto.Clear();
        Invalidate();
    }

    private void DragTo(Point point)
    {
        Request(dragColumn, Sliders.PercentAt(point.Y));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (phase == Phase.Collapsed && !frameTimer.Enabled)
        {
            hoverStart = Environment.TickCount;
            StartFrames();
        }
        if (dragColumn >= 0)
            DragTo(CursorInPanel());
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        // A click on the bar only opens it; it isn't aimed at any slider yet. Windows Forms
        // captures the mouse for the time of the press, so a drag can leave the panel.
        bool wasOpen = phase is Phase.Open or Phase.Opening;
        Open();
        if (!wasOpen || monitors.Count == 0)
            return;
        Point point = CursorInPanel();
        var layout = Sliders;
        int column = layout.ColumnAt(point.X);
        if (layout.IsInAutoRow(point.Y))
        {
            ToggleAuto(column);
            return;
        }
        if (DisplayedAuto(monitors[column]))
            return;
        dragColumn = column;
        DragTo(point);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            dragColumn = -1;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        dragColumn = -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Open();
        if (monitors.Count == 0)
            return;

        // High-resolution wheels send fractions of a notch.
        wheelRemainder += e.Delta;
        int notches = wheelRemainder / WheelNotch;
        wheelRemainder %= WheelNotch;
        if (notches == 0)
            return;
        int column = Sliders.ColumnAt(CursorInPanel().X);
        if (DisplayedAuto(monitors[column]))
            return;
        Request(column, DisplayedPercent(monitors[column]) + notches * wheelStep);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.RegisterAppBar(Handle, AppBarMessage);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.UnregisterAppBar(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)NativeMethods.MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
        if (m.Msg == NativeMethods.WM_DPICHANGED)
        {
            BeginInvoke(new Action(Relayout));
        }
        else if (m.Msg == AppBarMessage && m.WParam.ToInt64() == NativeMethods.ABN_FULLSCREENAPP)
        {
            fullScreenActive = m.LParam != IntPtr.Zero;
            UpdateVisibility();
        }
        else if (m.Msg == TaskbarCreated && TaskbarCreated != 0)
        {
            NativeMethods.RegisterAppBar(Handle, AppBarMessage);
            fullScreenActive = false;
            UpdateVisibility();
        }
    }

    private void OnSystemLayoutChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(new Action(Relayout));
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Raised for work area changes, e.g. when the taskbar moves or auto-hides.
        if (e.Category == UserPreferenceCategory.Desktop)
            OnSystemLayoutChanged(sender, e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnSystemLayoutChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            frameTimer.Dispose();
            settleTimer.Dispose();
            font?.Dispose();
        }
        base.Dispose(disposing);
    }
}
