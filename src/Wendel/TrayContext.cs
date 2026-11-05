using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Wendel;

/// <summary>
/// Owns the tray icon, its menu, the bar and the brightness worker, connects them, and reacts
/// to settings and display changes.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "wendel";

    // Displays need a moment after waking up, and one change raises several events.
    private const int RescanDelayMilliseconds = 1500;

    private readonly NotifyIcon tray;
    private readonly ContextMenu menu;
    private readonly BarForm bar;
    private readonly BrightnessWorker worker;
    private readonly FileSystemWatcher? watcher;
    private readonly Timer reloadTimer;
    private readonly Timer rescanTimer;
    private readonly MenuItem autostartItem;
    private readonly MenuItem[] positionItems;
    private IReadOnlyList<MonitorState> monitors = Array.Empty<MonitorState>();
    private Settings settings;

    public TrayContext(IMonitorBackend backend)
    {
        settings = Settings.Load(out var loadError);

        bar = new BarForm();
        worker = new BrightnessWorker(backend);
        worker.Changed += OnMonitorsChanged;
        bar.PercentRequested += worker.SetPercent;
        bar.Opening += () =>
        {
            // The usual reason for finding nothing is a monitor that was off during the last scan.
            if (monitors.Count == 0)
                worker.Rescan();
            else
                worker.Refresh();
        };

        positionItems = new[]
        {
            PositionItem("Left edge", BarPosition.Left),
            PositionItem("Right edge", BarPosition.Right),
            PositionItem("Top edge", BarPosition.Top),
            PositionItem("Bottom-right corner", BarPosition.BottomRightCorner),
        };
        autostartItem = new MenuItem("Start with Windows", (_, _) => ToggleAutostart());

        NativeMethods.ApplyMenuTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        menu = new ContextMenu(new[]
        {
            new MenuItem("Position", positionItems),
            autostartItem,
            new MenuItem("Rescan monitors", (_, _) => worker.Rescan()),
            new MenuItem("Open settings file", (_, _) => OpenSettingsFile()),
            new MenuItem("Open log file", (_, _) => OpenLogFile()),
            new MenuItem("-"),
            new MenuItem("Exit", (_, _) => ExitThread()),
        });
        menu.Popup += (_, _) => UpdateMenuChecks();

        tray = new NotifyIcon
        {
            Icon = Artwork.CreateTrayIcon(),
            Text = "wendel",
            ContextMenu = menu,
            Visible = true,
        };

        // Pick up edits to settings.json; editors often write a file several times.
        reloadTimer = new Timer { Interval = 300 };
        reloadTimer.Tick += (_, _) => { reloadTimer.Stop(); ReloadSettings(); };
        rescanTimer = new Timer { Interval = RescanDelayMilliseconds };
        rescanTimer.Tick += (_, _) => { rescanTimer.Stop(); worker.Rescan(); };
        watcher = CreateWatcher();

        bar.Apply(settings);
        bar.Show();
        worker.Start();

        if (loadError is not null)
            ShowError(loadError);
    }

    private MenuItem PositionItem(string text, BarPosition position)
    {
        return new MenuItem(text, (_, _) =>
        {
            settings.BarPosition = position;
            SaveSettings();
            bar.Apply(settings);
        })
        { Tag = position, RadioCheck = true };
    }

    /// <summary>Runs on the worker thread.</summary>
    private void OnMonitorsChanged(IReadOnlyList<MonitorState> states)
    {
        try
        {
            bar.BeginInvoke(new Action(() =>
            {
                monitors = states;
                if (!bar.IsDisposed)
                    bar.SetMonitors(states);
            }));
        }
        catch (InvalidOperationException)
        {
            // The bar is already gone; the app is exiting.
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Switching between light and dark app mode raises the General category.
        if (e.Category == UserPreferenceCategory.General)
            NativeMethods.ApplyMenuTheme();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleRescan();

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            ScheduleRescan();
    }

    private void ScheduleRescan()
    {
        if (!bar.IsHandleCreated || bar.IsDisposed)
            return;
        bar.BeginInvoke(new Action(() =>
        {
            rescanTimer.Stop();
            rescanTimer.Start();
        }));
    }

    private void UpdateMenuChecks()
    {
        foreach (var item in positionItems)
            item.Checked = (BarPosition)item.Tag == settings.BarPosition;
        autostartItem.Checked = IsAutostartEnabled();
    }

    /// <summary>Logs <paramref name="message"/> and shows it in a tray notification.</summary>
    private void ShowError(string message)
    {
        Log.Error(message);
        tray.ShowBalloonTip(5000, "wendel", message, ToolTipIcon.Error);
    }

    private void SaveSettings()
    {
        try
        {
            settings.Save();
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            ShowError($"Could not save {Settings.FilePath}: {e.Message}");
        }
    }

    private void ReloadSettings()
    {
        settings = Settings.Load(out var error);
        bar.Apply(settings);
        if (error is not null)
            ShowError(error);
    }

    private FileSystemWatcher? CreateWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(Settings.FilePath)!;
            Directory.CreateDirectory(dir);
            var w = new FileSystemWatcher(dir, Path.GetFileName(Settings.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                SynchronizingObject = bar,
            };
            FileSystemEventHandler restart = (_, _) => { reloadTimer.Stop(); reloadTimer.Start(); };
            w.Changed += restart;
            w.Created += restart;
            w.Renamed += (_, _) => { reloadTimer.Stop(); reloadTimer.Start(); };
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            Log.Error($"Could not watch {Settings.FilePath}; changes need a restart.", e.ToString());
            return null;
        }
    }

    private void OpenSettingsFile()
    {
        if (!File.Exists(Settings.FilePath))
            SaveSettings();
        OpenInNotepad(Settings.FilePath);
    }

    private void OpenLogFile()
    {
        if (!File.Exists(Log.FilePath))
            Log.Info("Log file created");
        OpenInNotepad(Log.FilePath);
    }

    private void OpenInNotepad(string path)
    {
        try
        {
            // A Windows path can't contain a quote or end in a backslash if it names a file.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\""))?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            ShowError(e.Message);
        }
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private void ToggleAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key.GetValue(RunValueName) is null)
                key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue(RunValueName, false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException || e is System.Security.SecurityException || e is IOException)
        {
            ShowError($"Could not change autostart: {e.Message}");
        }
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            worker.Dispose();
            watcher?.Dispose();
            reloadTimer.Dispose();
            rescanTimer.Dispose();
            tray.Dispose();
            menu.Dispose();
            bar.Dispose();
        }
        base.Dispose(disposing);
    }
}
