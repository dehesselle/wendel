using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Wendel;

/// <summary>A monitor as the UI sees it. Immutable; a new list arrives with every change.</summary>
internal sealed class MonitorState
{
    public MonitorState(int id, string description, int percent)
    {
        Id = id;
        Description = description;
        Percent = percent;
    }

    /// <summary>Identifies the monitor until the next rescan.</summary>
    public int Id { get; }

    public string Description { get; }

    public int Percent { get; }
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
    private bool rescanRequested;
    private bool refreshRequested;
    private bool stopping;

    // Worker thread only.
    private readonly List<Entry> entries = new();
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

    /// <summary>Reads the brightness again, as it can change through the monitor's own buttons.</summary>
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

    private void Run()
    {
        var nextWrite = DateTime.MinValue;
        while (true)
        {
            bool rescan, refresh;
            Dictionary<int, int>? writes = null;
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
                }
                else if (pendingPercent.Count > 0 && DateTime.UtcNow >= nextWrite)
                {
                    writes = new Dictionary<int, int>(pendingPercent);
                    pendingPercent.Clear();
                }
            }

            if (rescan)
                Scan();
            else if (refresh)
                ReadAll();
            if (writes is not null)
            {
                WriteAll(writes);
                nextWrite = DateTime.UtcNow + writeInterval;
            }

            bool waiting;
            lock (gate)
                waiting = pendingPercent.Count > 0;
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
            if (TryRead(connection, out int current, out int maximum, out string? error) && maximum > 0)
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
        Publish();
    }

    private void ReadAll()
    {
        bool changed = false;
        foreach (var entry in entries)
        {
            if (!TryRead(entry.Connection, out int current, out int maximum, out string? error) || maximum <= 0)
            {
                // Keep showing the last known value; a write finds out if the monitor is gone.
                Log.Info($"Could not read the brightness of \"{entry.Connection.Description}\"", error);
                continue;
            }
            if (current != entry.Raw || maximum != entry.Maximum)
            {
                entry.Raw = current;
                entry.Maximum = maximum;
                changed = true;
            }
        }
        if (changed)
            Publish();
    }

    private void WriteAll(Dictionary<int, int> writes)
    {
        bool failed = false;
        foreach (var write in writes)
        {
            var entry = entries.Find(e => e.Id == write.Key);
            if (entry is null)
                continue;
            int raw = (int)Math.Round(write.Value * entry.Maximum / 100.0);
            if (entry.Connection.TryWrite(raw, out string? error))
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
            Rescan();
        else
            Publish();
    }

    private bool TryRead(IMonitorConnection connection, out int current, out int maximum, out string? error)
    {
        current = maximum = 0;
        error = null;
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(retryPause);
            if (connection.TryRead(out current, out maximum, out error))
                return true;
        }
        return false;
    }

    private void Publish()
    {
        var states = entries
            .Select(e => new MonitorState(e.Id, e.Connection.Description, (int)Math.Round(e.Raw * 100.0 / e.Maximum)))
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
    }
}
