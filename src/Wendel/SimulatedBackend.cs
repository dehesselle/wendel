// SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
//
// SPDX-License-Identifier: 0BSD

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

        public bool TryRead(byte code, out int current, out int maximum, out string? error)
        {
            Thread.Sleep(delay);
            lock (monitor)
            {
                current = maximum = 0;
                bool isSensor = code == monitor.SensorCode && monitor.SensorAnswers;
                if (code != Vcp.Brightness && !isSensor)
                {
                    error = "Simulated: no such code";
                    return false;
                }
                if (monitor.FailReads > 0)
                {
                    monitor.FailReads--;
                    error = "Simulated read failure";
                    return false;
                }
                current = isSensor ? monitor.Sensor : monitor.Value;
                maximum = isSensor ? monitor.SensorMaximum : monitor.Maximum;
                error = null;
                return true;
            }
        }

        public bool TryWrite(byte code, int value, out string? error)
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
                if (code == Vcp.Brightness)
                {
                    monitor.Value = value;
                    monitor.Writes.Add(value);
                }
                else
                {
                    monitor.SensorWrites.Add(value);
                    if (!monitor.IgnoresSensorWrites)
                        monitor.Sensor = value;
                }
                error = null;
                return true;
            }
        }

        public bool TryReadCapabilities(out string? capabilities, out string? error)
        {
            Thread.Sleep(delay);
            lock (monitor)
            {
                monitor.CapabilitiesCalls++;
                if (monitor.FailCapabilities > 0)
                {
                    monitor.FailCapabilities--;
                    capabilities = null;
                    error = "Simulated capabilities failure";
                    return false;
                }
                capabilities = monitor.Capabilities;
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

    /// <summary>Whether the code of the ambient light sensor answers at all.</summary>
    public bool SensorAnswers { get; set; }

    public byte SensorCode { get; set; } = MonitorProfiles.Standard.Code;

    public int Sensor { get; set; } = MonitorProfiles.Standard.Off;

    public int SensorMaximum { get; set; } = MonitorProfiles.Standard.On;

    /// <summary>Accepts sensor writes without acting on them, as some monitors do.</summary>
    public bool IgnoresSensorWrites { get; set; }

    public string Capabilities { get; set; } = "(prot(monitor)type(lcd)model(simulated)vcp(02 10 12 14(05 08) 60(0F 11))mccs_ver(2.1))";

    /// <summary>The next this many reads fail.</summary>
    public int FailReads { get; set; }

    /// <summary>The next this many writes fail.</summary>
    public int FailWrites { get; set; }

    /// <summary>The next this many capabilities requests fail.</summary>
    public int FailCapabilities { get; set; }

    public int CapabilitiesCalls { get; set; }

    public List<int> Writes { get; } = new();

    public List<int> SensorWrites { get; } = new();
}
