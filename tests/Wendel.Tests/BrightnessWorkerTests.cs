// SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
//
// SPDX-License-Identifier: 0BSD

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
    private int publishes;
    private int valuesLogged;

    public BrightnessWorkerTests()
    {
        Log.FilePath = Path.Combine(dir, "wendel.log");
        backend = new SimulatedBackend(2, delayMilliseconds: 1);
        worker = NewWorker(backend);
    }

    private BrightnessWorker NewWorker(SimulatedBackend source, Func<string?, AutoBrightnessControl>? profiles = null)
    {
        var created = new BrightnessWorker(source, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(1), profiles);
        created.Changed += states =>
        {
            lock (gate)
            {
                latest = states;
                publishes++;
                Monitor.PulseAll(gate);
            }
        };
        created.ValuesLogged += () => Interlocked.Increment(ref valuesLogged);
        return created;
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

    private const int Off = 1;
    private const int On = 2;

    /// <summary>Reads the log while the worker writes to it, without getting in its way: a blocked entry is lost.</summary>
    private string LogText()
    {
        if (!File.Exists(Log.FilePath))
            return "";
        using var stream = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private List<int> SensorWrites(int monitor)
    {
        lock (backend.Monitors[monitor])
            return backend.Monitors[monitor].SensorWrites.ToList();
    }

    /// <summary>A scan reports twice, the second time when it's finished. For tests that write nothing before.</summary>
    private void WaitForScans(int count)
    {
        int Published()
        {
            lock (gate)
                return publishes;
        }

        WaitUntil(() => Published() >= count * 2);
    }

    private void SetModel(int monitor, string model)
    {
        lock (backend.Monitors[monitor])
            backend.Monitors[monitor].Capabilities = $"(type(lcd)model({model})vcp(10 12 66 E2))";
    }

    private void GiveSensor(int monitor, int value, int maximum)
    {
        lock (backend.Monitors[monitor])
        {
            backend.Monitors[monitor].SensorAnswers = true;
            backend.Monitors[monitor].Sensor = value;
            backend.Monitors[monitor].SensorMaximum = maximum;
        }
    }

    private void LogValuesAndWait()
    {
        int before = Volatile.Read(ref valuesLogged);
        worker.LogValues();
        WaitUntil(() => Volatile.Read(ref valuesLogged) > before);
    }

    [Fact]
    public void MonitorWithoutASensorHasNoAutoState()
    {
        worker.Start();
        WaitForScans(1);
        var states = WaitFor(s => s.Count == 2);
        Assert.Null(states[0].Auto);
        Assert.Null(states[1].Auto);
    }

    [Theory]
    [InlineData(Off, false)]
    [InlineData(On, true)]
    public void ReportsTheSensorsState(int value, bool expected)
    {
        GiveSensor(0, value, On);
        worker.Start();
        var states = WaitFor(s => s.Count == 2 && s[0].Auto is not null);
        Assert.Equal(expected, states[0].Auto);
        Assert.Null(states[1].Auto);
    }

    [Theory]
    [InlineData(0, On)]
    [InlineData(3, On)]
    [InlineData(On, 0)]
    public void ValuesOutsideTheStandardMeanNoSensor(int value, int maximum)
    {
        GiveSensor(0, value, maximum);
        worker.Start();
        WaitForScans(1);
        Assert.Null(WaitFor(s => s.Count == 2)[0].Auto);
    }

    [Theory]
    [InlineData(0x81, false)]
    [InlineData(0x82, true)]
    public void TheU3225QEProfileAcceptsItsHighBit(int value, bool expected)
    {
        SetModel(0, "U3225QE");
        GiveSensor(0, value, 1);
        worker.Start();
        Assert.Equal(expected, WaitFor(s => s.Count == 2 && s[0].Auto is not null)[0].Auto);
    }

    [Fact]
    public void AnUnknownModelDoesNotGetTheHighBitAccepted()
    {
        SetModel(0, "P2715Q");
        GiveSensor(0, 0x82, 1);
        worker.Start();
        WaitForScans(1);
        Assert.Null(WaitFor(s => s.Count == 2)[0].Auto);
    }

    [Fact]
    public void TheProfileIsFoundAfterCapabilitiesFailedOnce()
    {
        SetModel(0, "U3225QE");
        GiveSensor(0, 0x81, 1);
        backend.Monitors[0].FailCapabilities = 3;
        worker.Start();
        WaitForScans(1);
        Assert.Null(WaitFor(s => s.Count == 2)[0].Auto);

        worker.Rescan();

        WaitFor(s => s.Count == 2 && s[0].Auto == false);
    }

    [Fact]
    public void AMonitorSwappedAtTheSamePlaceGetsItsOwnProfile()
    {
        SetModel(0, "P2715Q");
        GiveSensor(0, 0x81, 1);
        worker.Start();
        WaitForScans(1);
        Assert.Null(WaitFor(s => s.Count == 2)[0].Auto);

        SetModel(0, "U3225QE");
        worker.Rescan();

        WaitFor(s => s.Count == 2 && s[0].Auto == false);
    }

    [Fact]
    public void AProfilesCodeAndValuesAreUsedForEverything()
    {
        lock (backend.Monitors[0])
            backend.Monitors[0].SensorCode = 0xE8;
        GiveSensor(0, 0, 1);
        using var custom = NewWorker(backend, _ => new AutoBrightnessControl(0xE8, off: 0, on: 1));
        custom.Start();
        int id = WaitFor(s => s.Count == 2 && s[0].Auto == false)[0].Id;

        custom.SetAuto(id, true);
        WaitFor(s => s[0].Auto == true);
        custom.SetAuto(id, false);
        WaitFor(s => s[0].Auto == false);
        Assert.Equal(new[] { 1, 0 }, SensorWrites(0));

        lock (backend.Monitors[0])
            backend.Monitors[0].Sensor = 1;
        custom.Refresh();
        WaitFor(s => s[0].Auto == true);
    }

    [Fact]
    public void SwitchesTheSensorOnAndOff()
    {
        GiveSensor(0, Off, On);
        worker.Start();
        int id = WaitFor(s => s.Count == 2 && s[0].Auto == false)[0].Id;

        worker.SetAuto(id, true);
        WaitFor(s => s[0].Auto == true);
        worker.SetAuto(id, false);
        WaitFor(s => s[0].Auto == false);

        Assert.Equal(new[] { On, Off }, SensorWrites(0));
    }

    [Fact]
    public void WritesPlainValuesAfterReadingTheHighBit()
    {
        SetModel(0, "U3225QE");
        GiveSensor(0, 0x81, 1);
        worker.Start();
        int id = WaitFor(s => s.Count == 2 && s[0].Auto == false)[0].Id;

        worker.SetAuto(id, true);

        WaitFor(s => s[0].Auto == true);
        Assert.Equal(new[] { On }, SensorWrites(0));
    }

    [Fact]
    public void ReportsTheTruthWhenTheMonitorIgnoresASwitch()
    {
        GiveSensor(0, Off, On);
        backend.Monitors[0].IgnoresSensorWrites = true;
        worker.Start();
        int id = WaitFor(s => s.Count == 2 && s[0].Auto == false)[0].Id;
        WaitForScans(1);
        int before;
        lock (gate)
            before = publishes;

        worker.SetAuto(id, true);
        WaitFor(_ => publishes > before);

        Assert.Equal(new[] { On }, SensorWrites(0));
        Assert.Equal(false, WaitFor(_ => true)[0].Auto);
    }

    [Fact]
    public void ASwitchReadsTheBrightnessAgain()
    {
        GiveSensor(0, Off, On);
        worker.Start();
        int id = WaitFor(s => s.Count == 2 && s[0].Auto == false)[0].Id;
        lock (backend.Monitors[0])
            backend.Monitors[0].Value = 12;

        worker.SetAuto(id, true);

        WaitFor(s => s[0].Auto == true && s[0].Percent == 12);
    }

    [Fact]
    public void FailedSwitchRescans()
    {
        GiveSensor(0, Off, On);
        worker.Start();
        var states = WaitFor(s => s.Count == 2 && s[0].Auto == false);
        lock (backend.Monitors[0])
            backend.Monitors[0].FailWrites = 1;

        worker.SetAuto(states[0].Id, true);

        var after = WaitFor(s => s.Count == 2 && s[0].Id != states[0].Id && s[0].Auto is not null);
        Assert.Equal(false, after[0].Auto);
    }

    [Fact]
    public void SwitchingOnDiscardsAWaitingBrightness()
    {
        GiveSensor(0, Off, On);
        worker.Start();
        int id = WaitFor(s => s.Count == 2 && s[0].Auto == false)[0].Id;

        // The first write opens the rate limit's window, so the next value has to wait in it.
        worker.SetPercent(id, 10);
        WaitUntil(() => Writes(0).Count == 1);
        worker.SetPercent(id, 90);
        worker.SetAuto(id, true);

        WaitFor(s => s[0].Auto == true);
        Assert.Equal(new[] { 10 }, Writes(0));
    }

    [Fact]
    public void RefreshSeesTheSensorChangedOnTheMonitor()
    {
        GiveSensor(0, Off, On);
        worker.Start();
        WaitFor(s => s.Count == 2 && s[0].Auto == false);
        lock (backend.Monitors[0])
            backend.Monitors[0].Sensor = On;

        worker.Refresh();

        WaitFor(s => s[0].Auto == true);
    }

    [Fact]
    public void RefreshSeesAnOnScreenChangeWithTheHighBit()
    {
        SetModel(0, "U3225QE");
        GiveSensor(0, Off, 1);
        worker.Start();
        WaitFor(s => s.Count == 2 && s[0].Auto == false);
        lock (backend.Monitors[0])
            backend.Monitors[0].Sensor = 0x82;

        worker.Refresh();

        WaitFor(s => s[0].Auto == true);
    }

    [Fact]
    public void NormalUseLogsNoMonitorValues()
    {
        GiveSensor(0, Off, On);
        worker.Start();
        var states = WaitFor(s => s.Count == 2 && s[0].Auto == false);

        worker.SetPercent(states[1].Id, 20);
        WaitFor(s => s[1].Percent == 20);
        worker.SetAuto(states[0].Id, true);
        WaitFor(s => s[0].Auto == true);
        worker.Refresh();
        worker.Rescan();
        WaitFor(s => s.Count == 2 && s[0].Id != states[0].Id && s[0].Auto is not null);

        Assert.Equal("", LogText());
    }

    [Fact]
    public void FailedWritesAreLoggedWithoutBeingAsked()
    {
        worker.Start();
        var states = WaitFor(s => s.Count == 2);
        lock (backend.Monitors[0])
            backend.Monitors[0].FailWrites = 1;

        worker.SetPercent(states[0].Id, 20);

        WaitUntil(() => LogText().Contains("ERROR Could not set the brightness of \"Simulated monitor 1\" to 20"));
    }

    [Fact]
    public void LogsEverythingAboutAMonitorOnRequest()
    {
        SetModel(0, "U3225QE");
        GiveSensor(0, 0x81, 1);
        worker.Start();
        WaitForScans(1);

        LogValuesAndWait();

        string log = LogText();
        Assert.Contains("Monitor at 0,0 \"Simulated monitor 1\": model U3225QE, profile U3225QE", log);
        Assert.Contains("  capabilities (type(lcd)model(U3225QE)vcp(10 12 66 E2))", log);
        Assert.Contains("  auto brightness: off (code 66)", log);
        Assert.Contains("  10 cur=0x0032 max=0x0064", log);
        Assert.Contains("  66 cur=0x0081 max=0x0001", log);
        Assert.Contains("  12 -", log);
        Assert.Contains("Monitor at 1920,0 \"Simulated monitor 2\": model simulated, profile standard", log);
        Assert.Contains("  auto brightness: not supported (code 66)", log);
    }

    [Fact]
    public void LogsTheManufacturerRangeButNotOtherUnlistedCodes()
    {
        SetModel(0, "P2715Q");
        worker.Start();
        WaitForScans(1);

        LogValuesAndWait();

        string log = LogText();
        Assert.Contains("  E5 -", log);
        Assert.DoesNotContain("  61 -", log);
    }

    [Fact]
    public void LogsAllCodesWithoutACapabilitiesString()
    {
        // Without delays, as this reads every code several times.
        var quick = new SimulatedBackend(1, delayMilliseconds: 0);
        quick.Monitors[0].FailCapabilities = 1000;
        int done = 0;
        using var custom = new BrightnessWorker(quick, TimeSpan.FromMilliseconds(50), TimeSpan.Zero);
        custom.ValuesLogged += () => Interlocked.Increment(ref done);
        custom.Start();

        custom.LogValues();

        WaitUntil(() => Volatile.Read(ref done) > 0);
        string log = LogText();
        Assert.Contains("Simulated monitor 1\": model unknown, profile standard", log);
        Assert.Contains("  capabilities unavailable: Simulated capabilities failure", log);
        Assert.Contains("  61 -", log);
        Assert.Contains("  00 -", log);
    }

    [Fact]
    public void LogsMonitorsThatHaveNoSlider()
    {
        backend.Monitors[0].FailReads = 100000;
        worker.Start();
        Assert.Single(WaitFor(_ => true));

        LogValuesAndWait();

        Assert.Contains("Monitor at 0,0 \"Simulated monitor 1\" doesn't answer over DDC/CI", LogText());
        Assert.Contains("  Simulated read failure", LogText());
    }

    [Fact]
    public void LogsThatThereAreNoMonitors()
    {
        var none = new SimulatedBackend(0, delayMilliseconds: 1);
        using var custom = NewWorker(none);
        custom.Start();
        WaitFor(s => s.Count == 0);
        int before = Volatile.Read(ref valuesLogged);

        custom.LogValues();

        WaitUntil(() => Volatile.Read(ref valuesLogged) > before);
        Assert.Contains("No monitors found", LogText());
    }

    [Fact]
    public void TwoLogsShowWhatChangedOnTheMonitor()
    {
        SetModel(0, "U3225QE");
        GiveSensor(0, 0x81, 1);
        worker.Start();
        WaitForScans(1);

        LogValuesAndWait();
        lock (backend.Monitors[0])
            backend.Monitors[0].Sensor = 0x82;
        LogValuesAndWait();

        string log = LogText();
        Assert.Contains("  66 cur=0x0081 max=0x0001", log);
        Assert.Contains("  66 cur=0x0082 max=0x0001", log);
    }

    [Fact]
    public void LoggingValuesWritesNothingToTheMonitors()
    {
        GiveSensor(0, Off, On);
        worker.Start();
        WaitForScans(1);

        LogValuesAndWait();

        Assert.Empty(Writes(0));
        Assert.Empty(Writes(1));
        Assert.Empty(SensorWrites(0));
    }

    [Fact]
    public void AMonitorsSliderKeepsWorkingAfterLoggingValues()
    {
        worker.Start();
        var states = WaitFor(s => s.Count == 2);
        WaitForScans(1);

        LogValuesAndWait();
        worker.SetPercent(states[0].Id, 33);

        WaitFor(s => s[0].Percent == 33);
        Assert.Equal(new[] { 33 }, Writes(0));
    }

    [Fact]
    public void DisposeStopsTheWorker()
    {
        worker.Start();
        WaitFor(s => s.Count == 2);
        worker.Dispose();
    }
}
