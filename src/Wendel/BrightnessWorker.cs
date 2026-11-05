using System;
using System.Collections.Generic;
using System.Linq;
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

    private readonly IMonitorBackend backend;
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
    private bool stopping;

    // Worker thread only.
    private readonly List<Entry> entries = new();
    private readonly HashSet<string> capabilitiesLogged = new();
    private int nextId;

    /// <summary>Raised on the worker thread with the current monitors, whenever they change.</summary>
    public event Action<IReadOnlyList<MonitorState>>? Changed;

    public BrightnessWorker(IMonitorBackend backend)
        : this(backend, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50))
    {
    }

    internal BrightnessWorker(IMonitorBackend backend, TimeSpan writeInterval, TimeSpan retryPause)
    {
        this.backend = backend;
        this.writeInterval = writeInterval;
        this.retryPause = retryPause;
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

    private void Run()
    {
        var nextWrite = DateTime.MinValue;
        while (true)
        {
            bool rescan, refresh;
            Dictionary<int, int>? percents = null;
            Dictionary<int, bool>? autos = null;
            lock (gate)
            {
                if (stopping)
                    break;
                rescan = rescanRequested;
                refresh = refreshRequested;
                rescanRequested = refreshRequested = false;
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

    private void Scan()
    {
        Close();
        IReadOnlyList<IMonitorConnection> opened;
        try
        {
            opened = backend.Open();
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            Log.Error("The Monitor Configuration API is not available", e.ToString());
            opened = Array.Empty<IMonitorConnection>();
        }

        foreach (var connection in opened.OrderBy(c => c.ScreenX).ThenBy(c => c.ScreenY))
        {
            if (TryRead(connection, Vcp.Brightness, out int current, out int maximum, out string? error) && maximum > 0)
            {
                var entry = new Entry(nextId++, connection, maximum, current);
                entries.Add(entry);
                Log.Info($"Monitor at {connection.ScreenX},{connection.ScreenY} \"{connection.Description}\": brightness {current} of {maximum}");
            }
            else
            {
                Log.Info($"Monitor at {connection.ScreenX},{connection.ScreenY} \"{connection.Description}\" doesn't answer over DDC/CI",
                    error ?? $"It reports a maximum of {maximum}.");
                connection.Dispose();
            }
        }
        if (entries.Count == 0)
            Log.Info("No monitor answers over DDC/CI");

        // The sliders can work from here on. What follows can take seconds per monitor.
        Publish();
        bool anySensor = false;
        foreach (var entry in entries)
        {
            ProbeSensor(entry);
            anySensor |= entry.Sensor is not null;
        }
        if (anySensor)
            Publish();
        foreach (var entry in entries)
            LogCapabilities(entry);
    }

    /// <summary>
    /// A monitor supports the sensor if the code answers with a value the standard defines and a
    /// maximum other than 00h, which the standard says means "not supported".
    /// </summary>
    private void ProbeSensor(Entry entry)
    {
        var connection = entry.Connection;
        if (!TryRead(connection, Vcp.AmbientLightSensor, out int current, out int maximum, out string? error))
        {
            Log.Info($"Ambient light sensor of \"{connection.Description}\": not supported", error);
            return;
        }
        int value = Vcp.SensorValue(current);
        if ((value != Vcp.SensorDisabled && value != Vcp.SensorEnabled) || maximum == 0)
        {
            Log.Info($"Ambient light sensor of \"{connection.Description}\": not supported (value {current}, maximum {maximum})");
            return;
        }
        entry.Sensor = value;
        Log.Info($"Ambient light sensor of \"{connection.Description}\": {SensorName(value)} (value {current}, maximum {maximum})");
    }

    /// <summary>Logs the codes a monitor offers, once per run because the request can take seconds.</summary>
    private void LogCapabilities(Entry entry)
    {
        var connection = entry.Connection;
        string key = $"{connection.Description}@{connection.ScreenX},{connection.ScreenY}";
        if (capabilitiesLogged.Contains(key))
            return;

        string? capabilities = null;
        string? error = null;
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(retryPause);
            if (connection.TryReadCapabilities(out capabilities, out error))
                break;
        }
        if (capabilities is null)
        {
            Log.Info($"Could not read the capabilities of \"{connection.Description}\"", error);
            return;
        }
        capabilitiesLogged.Add(key);
        var codes = Capabilities.VcpCodes(capabilities);
        string listed = codes.Contains(Vcp.AmbientLightSensor) ? "listed" : "not listed";
        Log.Info($"Capabilities of \"{connection.Description}\": VCP codes {Capabilities.Format(codes)} (0x{Vcp.AmbientLightSensor:X2} {listed})", capabilities);
    }

    private void ReadAll()
    {
        bool changed = false;
        foreach (var entry in entries)
        {
            if (!TryRead(entry.Connection, Vcp.Brightness, out int current, out int maximum, out string? error) || maximum <= 0)
            {
                // Keep showing the last known value; a write finds out if the monitor is gone.
                Log.Info($"Could not read the brightness of \"{entry.Connection.Description}\"", error);
            }
            else if (current != entry.Raw || maximum != entry.Maximum)
            {
                entry.Raw = current;
                entry.Maximum = maximum;
                changed = true;
            }
            changed |= ReadSensor(entry);
        }
        if (changed)
            Publish();
    }

    /// <summary>Reads a sensor the monitor is known to have; true if its state changed.</summary>
    private bool ReadSensor(Entry entry)
    {
        if (entry.Sensor is null)
            return false;
        if (!TryRead(entry.Connection, Vcp.AmbientLightSensor, out int current, out _, out string? error))
        {
            Log.Info($"Could not read the ambient light sensor of \"{entry.Connection.Description}\"", error);
            return false;
        }
        int value = Vcp.SensorValue(current);
        if (value == entry.Sensor || (value != Vcp.SensorDisabled && value != Vcp.SensorEnabled))
            return false;
        entry.Sensor = value;
        return true;
    }

    private void WriteAll(Dictionary<int, int> percents, Dictionary<int, bool> autos)
    {
        bool failed = false;
        var toggled = new List<KeyValuePair<Entry, int>>();

        // The sensor first: a brightness following a switch to off is then not overridden.
        foreach (var auto in autos)
        {
            var entry = entries.Find(e => e.Id == auto.Key);
            if (entry?.Sensor is null)
                continue;
            int value = auto.Value ? Vcp.SensorEnabled : Vcp.SensorDisabled;
            if (entry.Connection.TryWrite(Vcp.AmbientLightSensor, value, out string? error))
            {
                toggled.Add(new KeyValuePair<Entry, int>(entry, value));
            }
            else
            {
                Log.Error($"Could not switch the ambient light sensor of \"{entry.Connection.Description}\" to {SensorName(value)}", error);
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
    /// Reports what the monitor says after a switch, not what was asked for. Monitors can accept
    /// a write and do nothing. The brightness is read too, because the switch may change it.
    /// </summary>
    private void ConfirmToggle(Entry entry, int requested)
    {
        string name = entry.Connection.Description;
        if (TryRead(entry.Connection, Vcp.AmbientLightSensor, out int current, out _, out string? error))
        {
            int value = Vcp.SensorValue(current);
            entry.Sensor = value == Vcp.SensorEnabled || value == Vcp.SensorDisabled ? value : requested;
            if (value == requested)
                Log.Info($"Ambient light sensor of \"{name}\": switched to {SensorName(requested)}");
            else
                Log.Info($"Ambient light sensor of \"{name}\": ignored the switch to {SensorName(requested)}, it reports value {current}");
        }
        else
        {
            entry.Sensor = requested;
            Log.Info($"Ambient light sensor of \"{name}\": switched to {SensorName(requested)}, but it couldn't be read back", error);
        }

        if (TryRead(entry.Connection, Vcp.Brightness, out int brightness, out int maximum, out _) && maximum > 0)
        {
            entry.Raw = brightness;
            entry.Maximum = maximum;
        }
    }

    private static string SensorName(int value) => value == Vcp.SensorEnabled ? "enabled" : "disabled";

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
                e.Sensor is null ? null : e.Sensor == Vcp.SensorEnabled))
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

        /// <summary>The value of the ambient light sensor's code; null if the monitor has none.</summary>
        public int? Sensor { get; set; }
    }
}
