using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Wendel;

/// <summary>
/// Monitors held in memory, for working on the UI where no monitor speaks DDC/CI, such as a
/// VM (<c>--simulate N</c>), and for the tests. State survives a rescan, as on a real monitor.
/// </summary>
internal sealed class SimulatedBackend : IMonitorBackend
{
    private readonly int delayMilliseconds;

    public SimulatedBackend(int count, int delayMilliseconds = 40)
    {
        this.delayMilliseconds = delayMilliseconds;
        Monitors = Enumerable.Range(0, count)
            .Select(i => new SimulatedMonitor($"Simulated monitor {i + 1}", i * 1920))
            .ToList();
    }

    public List<SimulatedMonitor> Monitors { get; }

    /// <summary>How many times <see cref="Open"/> was called.</summary>
    public int OpenCount { get; private set; }

    public IReadOnlyList<IMonitorConnection> Open()
    {
        OpenCount++;
        return Monitors.Select(m => (IMonitorConnection)new Connection(m, delayMilliseconds)).ToList();
    }

    private sealed class Connection : IMonitorConnection
    {
        private readonly SimulatedMonitor monitor;
        private readonly int delay;

        public Connection(SimulatedMonitor monitor, int delay)
        {
            this.monitor = monitor;
            this.delay = delay;
        }

        public string Description => monitor.Description;

        public int ScreenX => monitor.ScreenX;

        public int ScreenY => 0;

        public bool TryRead(out int current, out int maximum, out string? error)
        {
            Thread.Sleep(delay);
            lock (monitor)
            {
                current = monitor.Value;
                maximum = monitor.Maximum;
                if (monitor.FailReads > 0)
                {
                    monitor.FailReads--;
                    error = "Simulated read failure";
                    return false;
                }
                error = null;
                return true;
            }
        }

        public bool TryWrite(int value, out string? error)
        {
            Thread.Sleep(delay);
            lock (monitor)
            {
                if (monitor.FailWrites > 0)
                {
                    monitor.FailWrites--;
                    error = "Simulated write failure";
                    return false;
                }
                monitor.Value = value;
                monitor.Writes.Add(value);
                error = null;
                return true;
            }
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>State of one simulated monitor. Lock on the object to access it across threads.</summary>
internal sealed class SimulatedMonitor
{
    public SimulatedMonitor(string description, int screenX)
    {
        Description = description;
        ScreenX = screenX;
    }

    public string Description { get; }

    public int ScreenX { get; set; }

    public int Value { get; set; } = 50;

    public int Maximum { get; set; } = 100;

    /// <summary>The next this many reads fail.</summary>
    public int FailReads { get; set; }

    /// <summary>The next this many writes fail.</summary>
    public int FailWrites { get; set; }

    public List<int> Writes { get; } = new();
}
