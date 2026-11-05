using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace Wendel.Tests;

[Collection("Log")]
public sealed class BrightnessWorkerTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string originalLog = Log.FilePath;
    private readonly string dir = Path.Combine(Path.GetTempPath(), "wendel-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SimulatedBackend backend;
    private readonly BrightnessWorker worker;
    private readonly object gate = new();
    private IReadOnlyList<MonitorState>? latest;

    public BrightnessWorkerTests()
    {
        Log.FilePath = Path.Combine(dir, "wendel.log");
        backend = new SimulatedBackend(2, delayMilliseconds: 1);
        worker = new BrightnessWorker(backend, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1));
        worker.Changed += states =>
        {
            lock (gate)
            {
                latest = states;
                Monitor.PulseAll(gate);
            }
        };
    }

    public void Dispose()
    {
        worker.Dispose();
        Log.FilePath = originalLog;
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    private IReadOnlyList<MonitorState> WaitFor(Func<IReadOnlyList<MonitorState>, bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        lock (gate)
        {
            while (latest is null || !condition(latest))
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(remaining > TimeSpan.Zero, "Timed out waiting for a monitor report");
                Monitor.Wait(gate, remaining);
            }
            return latest;
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            Thread.Sleep(5);
        }
    }

    private List<int> Writes(int monitor)
    {
        lock (backend.Monitors[monitor])
            return backend.Monitors[monitor].Writes.ToList();
    }

    [Fact]
    public void ReportsAnsweringMonitorsWithTheirBrightness()
    {
        backend.Monitors[0].Value = 30;
        backend.Monitors[1].Value = 70;
        worker.Start();
        var states = WaitFor(s => s.Count == 2);
        Assert.Equal(new[] { 30, 70 }, states.Select(s => s.Percent));
        Assert.Equal("Simulated monitor 1", states[0].Description);
    }

    [Fact]
    public void OrdersMonitorsLeftToRight()
    {
        backend.Monitors[0].ScreenX = 1920;
        backend.Monitors[1].ScreenX = -1920;
        worker.Start();
        var states = WaitFor(s => s.Count == 2);
        Assert.Equal(new[] { "Simulated monitor 2", "Simulated monitor 1" }, states.Select(s => s.Description));
    }

    [Fact]
    public void RetriesIntermittentReads()
    {
        backend.Monitors[0].FailReads = 2;
        worker.Start();
        Assert.Equal(2, WaitFor(s => s.Count > 0).Count);
    }

    [Fact]
    public void LeavesOutMonitorsThatNeverAnswer()
    {
        backend.Monitors[0].FailReads = 1000;
        worker.Start();
        var states = WaitFor(_ => true);
        Assert.Equal("Simulated monitor 2", Assert.Single(states).Description);
    }

    [Fact]
    public void LeavesOutMonitorsWithoutARange()
    {
        backend.Monitors[0].Maximum = 0;
        worker.Start();
        var states = WaitFor(_ => true);
        Assert.Equal("Simulated monitor 2", Assert.Single(states).Description);
    }

    [Fact]
    public void ReportsAnEmptyListWhenNothingAnswers()
    {
        backend.Monitors[0].FailReads = backend.Monitors[1].FailReads = 1000;
        worker.Start();
        Assert.Empty(WaitFor(_ => true));
    }

    [Fact]
    public void BurstOfRequestsEndsOnTheLastValueWithFewWrites()
    {
        worker.Start();
        int id = WaitFor(s => s.Count == 2)[0].Id;
        var start = DateTime.UtcNow;

        for (int percent = 0; percent <= 100; percent++)
        {
            worker.SetPercent(id, percent);
            Thread.Sleep(1);
        }

        WaitUntil(() => Writes(0).LastOrDefault() == 100);
        var elapsed = DateTime.UtcNow - start;
        var writes = Writes(0);
        int allowed = (int)(elapsed.TotalMilliseconds / 50) + 2;
        Assert.True(writes.Count <= allowed, $"{writes.Count} writes in {elapsed.TotalMilliseconds} ms");
        Assert.True(writes.Count < 101, "Every request was written");
        Assert.Empty(Writes(1));
    }

    [Fact]
    public void KeepsAtLeastTheIntervalBetweenWrites()
    {
        worker.Start();
        int id = WaitFor(s => s.Count == 2)[0].Id;
        var start = DateTime.UtcNow;

        worker.SetPercent(id, 10);
        WaitUntil(() => Writes(0).Count == 1);
        worker.SetPercent(id, 20);
        WaitUntil(() => Writes(0).Count == 2);

        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(45), "The second write came too early");
    }

    [Fact]
    public void WritesDontInterfereBetweenMonitors()
    {
        worker.Start();
        var states = WaitFor(s => s.Count == 2);
        worker.SetPercent(states[0].Id, 20);
        worker.SetPercent(states[1].Id, 80);
        WaitFor(s => s[0].Percent == 20 && s[1].Percent == 80);
        Assert.Equal(new[] { 20 }, Writes(0));
        Assert.Equal(new[] { 80 }, Writes(1));
    }

    [Fact]
    public void ConvertsPercentToTheMonitorsRange()
    {
        backend.Monitors[0].Maximum = 200;
        backend.Monitors[0].Value = 100;
        worker.Start();
        var states = WaitFor(s => s.Count == 2);
        Assert.Equal(50, states[0].Percent);

        worker.SetPercent(states[0].Id, 25);
        WaitFor(s => s[0].Percent == 25);
        Assert.Equal(new[] { 50 }, Writes(0));
    }

    [Fact]
    public void ClampsRequests()
    {
        worker.Start();
        var states = WaitFor(s => s.Count == 2);
        worker.SetPercent(states[0].Id, 130);
        WaitFor(s => s[0].Percent == 100);
        Assert.Equal(new[] { 100 }, Writes(0));
    }

    [Fact]
    public void RefreshSeesChangesMadeOnTheMonitor()
    {
        worker.Start();
        WaitFor(s => s.Count == 2);
        lock (backend.Monitors[0])
            backend.Monitors[0].Value = 12;
        worker.Refresh();
        WaitFor(s => s[0].Percent == 12);
    }

    [Fact]
    public void RefreshKeepsTheLastValueWhenAReadFails()
    {
        backend.Monitors[0].Value = 40;
        worker.Start();
        WaitFor(s => s.Count == 2);
        lock (backend.Monitors[0])
            backend.Monitors[0].FailReads = 3;
        lock (backend.Monitors[1])
            backend.Monitors[1].Value = 90;
        worker.Refresh();
        var states = WaitFor(s => s[1].Percent == 90);
        Assert.Equal(40, states[0].Percent);
    }

    [Fact]
    public void FailedWriteRescansAndReportsTheRealValue()
    {
        backend.Monitors[0].Value = 60;
        worker.Start();
        var states = WaitFor(s => s.Count == 2);
        lock (backend.Monitors[0])
            backend.Monitors[0].FailWrites = 1;

        worker.SetPercent(states[0].Id, 10);

        var after = WaitFor(s => s.Count == 2 && s[0].Id != states[0].Id);
        Assert.Equal(60, after[0].Percent);
        Assert.Equal(2, backend.OpenCount);
    }

    [Fact]
    public void RequestForAMonitorFromBeforeARescanIsIgnored()
    {
        worker.Start();
        var old = WaitFor(s => s.Count == 2);
        worker.Rescan();
        var current = WaitFor(s => s[0].Id != old[0].Id);

        worker.SetPercent(old[0].Id, 5);
        worker.SetPercent(current[1].Id, 77);
        WaitFor(s => s[1].Percent == 77);

        Assert.Empty(Writes(0));
    }

    [Fact]
    public void RescanFindsAMonitorThatCameBack()
    {
        backend.Monitors[0].FailReads = 1000;
        worker.Start();
        Assert.Single(WaitFor(s => s.Count == 1));
        lock (backend.Monitors[0])
            backend.Monitors[0].FailReads = 0;
        worker.Rescan();
        WaitFor(s => s.Count == 2);
    }

    [Fact]
    public void DisposeStopsTheWorker()
    {
        worker.Start();
        WaitFor(s => s.Count == 2);
        worker.Dispose();
    }
}
