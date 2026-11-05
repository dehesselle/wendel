using System;
using System.Collections.Generic;

namespace Wendel;

/// <summary>The MCCS codes that are the same on every monitor. The rest is in <see cref="MonitorProfiles"/>.</summary>
internal static class Vcp
{
    /// <summary>"Luminance".</summary>
    public const byte Brightness = 0x10;
}

/// <summary>
/// A monitor as a backend sees it. All calls block for as long as the monitor takes, so only
/// <see cref="BrightnessWorker"/> makes them, on its own thread.
/// </summary>
internal interface IMonitorConnection : IDisposable
{
    /// <summary>What Windows calls the monitor, for the log.</summary>
    string Description { get; }

    /// <summary>Screen position, which orders the sliders left to right.</summary>
    int ScreenX { get; }

    int ScreenY { get; }

    /// <summary>Reads a VCP value and its maximum; on failure, returns false and sets <paramref name="error"/>.</summary>
    bool TryRead(byte code, out int current, out int maximum, out string? error);

    /// <summary>Writes a VCP value; on failure, returns false and sets <paramref name="error"/>.</summary>
    bool TryWrite(byte code, int value, out string? error);

    /// <summary>Reads the monitor's capabilities string; on failure, returns false and sets <paramref name="error"/>.</summary>
    bool TryReadCapabilities(out string? capabilities, out string? error);
}

internal interface IMonitorBackend
{
    /// <summary>
    /// Opens every physical monitor Windows knows about, whether or not it answers. The
    /// caller owns the connections. Failures to open are written to the log.
    /// </summary>
    IReadOnlyList<IMonitorConnection> Open();
}
