// SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
//
// SPDX-License-Identifier: 0BSD

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Wendel;

/// <summary>A monitor as the UI sees it. Immutable; a new list arrives with every change.</summary>
internal sealed class MonitorState
{
    public MonitorState(int id, string description, int percent, bool? auto = null)
    {
        Id = id;
        Description = description;
        Percent = percent;
        Auto = auto;
    }

    /// <summary>Identifies the monitor until the next rescan.</summary>
    public int Id { get; }

    public string Description { get; }

    public int Percent { get; }

    /// <summary>Whether automatic brightness is on; null if the monitor doesn't offer it.</summary>
    public bool? Auto { get; }
}

/// <summary>
/// Owns the monitor connections and is the only code that talks to them, because DDC/CI calls
/// block for tens of milliseconds. Requests from the UI thread only leave a note and wake the
/// worker thread. Writes keep only the latest value per monitor and are rate limited, so a drag
/// never queues up behind the monitor and the last value always lands.
/// </summary>
internal sealed class BrightnessWorker : IDisposable
{
    private const int ReadAttempts = 3;
    private const byte ManufacturerCodes = 0xE0;

    private readonly IMonitorBackend backend;
    private readonly Func<string?, AutoBrightnessControl> profiles;
    private readonly TimeSpan writeInterval;
    private readonly TimeSpan retryPause;
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;

    // Guarded by gate: everything the UI thread can leave for the worker.
    private readonly object gate = new();
    private readonly Dictionary<int, int> pendingPercent = new();
    private readonly Dictionary<int, bool> pendingAuto = new();
    private bool rescanRequested;
    private bool refreshRequested;
    private bool valuesRequested;
    private bool stopping;

    // Worker thread only.
    private readonly List<Entry> entries = new();
    private int nextId;

    /// <summary>Raised on the worker thread with the current monitors, whenever they change.</summary>
    public event Action<IReadOnlyList<MonitorState>>? Changed;

    /// <summary>Raised on the worker thread when a request to <see cref="LogValues"/> is done.</summary>
    public event Action? ValuesLogged;

    public BrightnessWorker(IMonitorBackend backend)
        : this(backend, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50))
    {
    }

    internal BrightnessWorker(IMonitorBackend backend, TimeSpan writeInterval, TimeSpan retryPause, Func<string?, AutoBrightnessControl>? profiles = null)
    {
        this.backend = backend;
        this.writeInterval = writeInterval;
        this.retryPause = retryPause;
        this.profiles = profiles ?? MonitorProfiles.For;
        thread = new Thread(Run) { IsBackground = true, Name = "Brightness" };
    }

    /// <summary>Starts the thread with a scan of the monitors. Separate from the constructor so handlers can attach first.</summary>
    public void Start()
    {
        rescanRequested = true;
        thread.Start();
    }

    /// <summary>Finds the monitors again, for when the display setup changed.</summary>
    public void Rescan()
    {
        lock (gate)
            rescanRequested = true;
        wake.Set();
    }

    /// <summary>Reads the monitors again, as they can change through their own buttons.</summary>
    public void Refresh()
    {
        lock (gate)
            refreshRequested = true;
        wake.Set();
    }

    /// <summary>Requests a brightness; replaces any value still waiting for this monitor.</summary>
    public void SetPercent(int id, int percent)
    {
        lock (gate)
            pendingPercent[id] = Math.Max(0, Math.Min(100, percent));
        wake.Set();
    }

    /// <summary>
    /// Requests automatic brightness on or off. Switching it on discards a brightness still
    /// waiting for this monitor, which would otherwise fight the sensor.
    /// </summary>
    public void SetAuto(int id, bool on)
    {
        lock (gate)
        {
            pendingAuto[id] = on;
            if (on)
                pendingPercent.Remove(id);
        }
        wake.Set();
    }

    /// <summary>
    /// Writes all that monitors report to the log, for adding support for a new monitor. Only reads.
    /// Takes seconds per monitor; <see cref="ValuesLogged"/> says when it's done.
    /// </summary>
    public void LogValues()
    {
        lock (gate)
            valuesRequested = true;
        wake.Set();
    }

    private void Run()
    {
        var nextWrite = DateTime.MinValue;
        while (true)
        {
            bool rescan, refresh, values;
            Dictionary<int, int>? percents = null;
            Dictionary<int, bool>? autos = null;
            lock (gate)
            {
                if (stopping)
                    break;
                rescan = rescanRequested;
                refresh = refreshRequested;
                values = valuesRequested;
                rescanRequested = refreshRequested = valuesRequested = false;
                if (rescan)
                {
                    // Ids die with the rescan, and a monitor is read again anyway.
                    pendingPercent.Clear();
                    pendingAuto.Clear();
                }
                else if ((pendingPercent.Count > 0 || pendingAuto.Count > 0) && DateTime.UtcNow >= nextWrite)
                {
                    percents = new Dictionary<int, int>(pendingPercent);
                    autos = new Dictionary<int, bool>(pendingAuto);
                    pendingPercent.Clear();
                    pendingAuto.Clear();
                }
            }

            if (rescan)
                Scan();
            else if (refresh)
                ReadAll();
            if (percents is not null && autos is not null)
            {
                WriteAll(percents, autos);
                nextWrite = DateTime.UtcNow + writeInterval;
            }
            if (values)
                WriteValuesToLog();

            bool waiting;
            lock (gate)
                waiting = pendingPercent.Count > 0 || pendingAuto.Count > 0;
            if (waiting)
            {
                var remaining = nextWrite - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                    wake.WaitOne(remaining);
            }
            else
            {
                wake.WaitOne();
            }
        }
        Close();
    }

    private IEnumerable<IMonitorConnection> OpenSorted()
    {
        try
        {
            return backend.Open().OrderBy(c => c.ScreenX).ThenBy(c => c.ScreenY).ToList();
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            Log.Error("The Monitor Configuration API is not available", e.ToString());
            return Array.Empty<IMonitorConnection>();
        }
    }

    private void Scan()
    {
        Close();
        foreach (var connection in OpenSorted())
        {
            if (TryRead(connection, Vcp.Brightness, out int current, out int maximum, out _) && maximum > 0)
                entries.Add(new Entry(nextId++, connection, maximum, current));
            else
                connection.Dispose();
        }

        // The sliders can work from here on. What follows can take seconds per monitor.
        Publish();
        foreach (var entry in entries)
            FindAutoBrightness(entry);
        Publish();
    }

    /// <summary>
    /// Looks up what the monitor's model uses for automatic brightness and asks the monitor whether
    /// it supports that. Without a capabilities string the monitor gets the standard control, and
    /// the next scan tries again.
    /// </summary>
    private void FindAutoBrightness(Entry entry)
    {
        var connection = entry.Connection;
        var control = profiles(ModelOf(ReadCapabilities(connection, out _)));
        if (TryRead(connection, control.Code, out int current, out int maximum, out _)
            && control.Decode(current, maximum) is bool auto)
        {
            entry.Control = control;
            entry.Auto = auto;
        }
    }

    private static string? ModelOf(string? capabilities) =>
        capabilities is null ? null : Capabilities.Model(capabilities);

    private string? ReadCapabilities(IMonitorConnection connection, out string? error)
    {
        error = null;
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(retryPause);
            if (connection.TryReadCapabilities(out string? capabilities, out error))
                return capabilities;
        }
        return null;
    }

    private void ReadAll()
    {
        bool changed = false;
        foreach (var entry in entries)
        {
            // On a failed read, keep showing the last known value; a write finds out if the monitor is gone.
            if (TryRead(entry.Connection, Vcp.Brightness, out int current, out int maximum, out _) && maximum > 0
                && (current != entry.Raw || maximum != entry.Maximum))
            {
                entry.Raw = current;
                entry.Maximum = maximum;
                changed = true;
            }
            changed |= ReadAuto(entry);
        }
        if (changed)
            Publish();
    }

    /// <summary>Reads automatic brightness of a monitor known to have it; true if its state changed.</summary>
    private bool ReadAuto(Entry entry)
    {
        if (entry.Control is null || !TryRead(entry.Connection, entry.Control.Code, out int current, out int maximum, out _))
            return false;
        if (entry.Control.Decode(current, maximum) is not bool auto || auto == entry.Auto)
            return false;
        entry.Auto = auto;
        return true;
    }

    private void WriteAll(Dictionary<int, int> percents, Dictionary<int, bool> autos)
    {
        bool failed = false;
        var toggled = new List<KeyValuePair<Entry, bool>>();

        // Automatic brightness first: a brightness following a switch to off is then not overridden.
        foreach (var auto in autos)
        {
            var entry = entries.Find(e => e.Id == auto.Key);
            if (entry?.Control is null)
                continue;
            int value = auto.Value ? entry.Control.On : entry.Control.Off;
            if (entry.Connection.TryWrite(entry.Control.Code, value, out string? error))
            {
                toggled.Add(new KeyValuePair<Entry, bool>(entry, auto.Value));
            }
            else
            {
                Log.Error($"Could not switch automatic brightness of \"{entry.Connection.Description}\" {(auto.Value ? "on" : "off")}", error);
                failed = true;
            }
        }

        foreach (var write in percents)
        {
            var entry = entries.Find(e => e.Id == write.Key);
            if (entry is null)
                continue;
            int raw = (int)Math.Round(write.Value * entry.Maximum / 100.0);
            if (entry.Connection.TryWrite(Vcp.Brightness, raw, out string? error))
            {
                entry.Raw = raw;
            }
            else
            {
                Log.Error($"Could not set the brightness of \"{entry.Connection.Description}\" to {raw}", error);
                failed = true;
            }
        }

        // The usual cause is a handle gone stale. A rescan publishes the monitors' real
        // values, so the sliders don't keep claiming a value that never arrived.
        if (failed)
        {
            Rescan();
            return;
        }

        if (toggled.Count > 0)
        {
            // Give the monitors the time the standard allows them to act on a command.
            Thread.Sleep(writeInterval);
            foreach (var pair in toggled)
                ConfirmToggle(pair.Key, pair.Value);
        }
        Publish();
    }

    /// <summary>
    /// Takes what the monitor says after a switch, not what was asked for. Monitors can accept a
    /// write and do nothing. The brightness is read too, because the switch may change it.
    /// </summary>
    private void ConfirmToggle(Entry entry, bool requested)
    {
        var control = entry.Control!;
        bool? reported = null;
        if (TryRead(entry.Connection, control.Code, out int current, out int maximum, out _))
            reported = control.Decode(current, maximum);
        entry.Auto = reported ?? requested;

        if (TryRead(entry.Connection, Vcp.Brightness, out int brightness, out int brightnessMaximum, out _) && brightnessMaximum > 0)
        {
            entry.Raw = brightness;
            entry.Maximum = brightnessMaximum;
        }
    }

    /// <summary>
    /// Writes one log entry per monitor, on connections of its own so that monitors without a
    /// slider are covered as well. The format stays the same, so that two of these can be diffed.
    /// </summary>
    private void WriteValuesToLog()
    {
        var connections = OpenSorted().ToList();
        if (connections.Count == 0)
            Log.Info("No monitors found");
        foreach (var connection in connections)
        {
            using (connection)
                LogMonitor(connection);
        }
        ValuesLogged?.Invoke();
    }

    private void LogMonitor(IMonitorConnection connection)
    {
        string name = $"Monitor at {connection.ScreenX},{connection.ScreenY} \"{connection.Description}\"";
        if (!TryRead(connection, Vcp.Brightness, out _, out _, out string? brightnessError))
        {
            Log.Info($"{name} doesn't answer over DDC/CI", brightnessError);
            return;
        }

        string? capabilities = ReadCapabilities(connection, out string? capabilitiesError);
        string? model = ModelOf(capabilities);
        var control = profiles(model);

        var details = new StringBuilder();
        details.Append("capabilities ").AppendLine(capabilities ?? $"unavailable: {capabilitiesError}");
        details.AppendLine(DescribeAuto(connection, control));
        foreach (byte code in CodesToLog(capabilities, control))
        {
            details.AppendLine(TryRead(connection, code, out int current, out int maximum, out _)
                ? $"{code:X2} cur=0x{current:X4} max=0x{maximum:X4}"
                : $"{code:X2} -");
        }
        Log.Info($"{name}: model {model ?? "unknown"}, profile {MonitorProfiles.NameFor(model)}", details.ToString());
    }

    private string DescribeAuto(IMonitorConnection connection, AutoBrightnessControl control)
    {
        string code = $"(code {control.Code:X2})";
        if (!TryRead(connection, control.Code, out int current, out int maximum, out _))
            return $"auto brightness: not supported {code}";
        return control.Decode(current, maximum) switch
        {
            true => $"auto brightness: on {code}",
            false => $"auto brightness: off {code}",
            null => $"auto brightness: not supported {code}",
        };
    }

    /// <summary>
    /// The codes the monitor lists, and those that matter even if it doesn't: the manufacturer's
    /// range and the ones wendel uses. Everything if there is no list to go by.
    /// </summary>
    private static IEnumerable<byte> CodesToLog(string? capabilities, AutoBrightnessControl control)
    {
        var listed = capabilities is null ? Array.Empty<byte>() : Capabilities.VcpCodes(capabilities);
        var codes = new SortedSet<byte>(listed) { Vcp.Brightness, MonitorProfiles.Standard.Code, control.Code };
        int first = listed.Count == 0 ? 0 : ManufacturerCodes;
        for (int code = first; code <= 0xFF; code++)
            codes.Add((byte)code);
        return codes;
    }

    private bool TryRead(IMonitorConnection connection, byte code, out int current, out int maximum, out string? error)
    {
        current = maximum = 0;
        error = null;
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(retryPause);
            if (connection.TryRead(code, out current, out maximum, out error))
                return true;
        }
        return false;
    }

    private void Publish()
    {
        var states = entries
            .Select(e => new MonitorState(
                e.Id,
                e.Connection.Description,
                (int)Math.Round(e.Raw * 100.0 / e.Maximum),
                e.Auto))
            .ToList();
        Changed?.Invoke(states);
    }

    private void Close()
    {
        foreach (var entry in entries)
            entry.Connection.Dispose();
        entries.Clear();
    }

    public void Dispose()
    {
        lock (gate)
            stopping = true;
        wake.Set();
        if (thread.IsAlive)
            thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class Entry
    {
        public Entry(int id, IMonitorConnection connection, int maximum, int raw)
        {
            Id = id;
            Connection = connection;
            Maximum = maximum;
            Raw = raw;
        }

        public int Id { get; }

        public IMonitorConnection Connection { get; }

        public int Maximum { get; set; }

        public int Raw { get; set; }

        /// <summary>How this monitor switches automatic brightness; null if it can't.</summary>
        public AutoBrightnessControl? Control { get; set; }

        public bool? Auto { get; set; }
    }
}
