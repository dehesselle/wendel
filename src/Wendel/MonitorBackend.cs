using System;
using System.Collections.Generic;

namespace Wendel;

/// <summary>
/// A monitor as a backend sees it. Reads and writes block for as long as the monitor takes,
/// so only <see cref="BrightnessWorker"/> calls them, on its own thread.
/// </summary>
internal interface IMonitorConnection : IDisposable
{
    /// <summary>What Windows calls the monitor, for the log.</summary>
    string Description { get; }

    /// <summary>Screen position, which orders the sliders left to right.</summary>
    int ScreenX { get; }

    int ScreenY { get; }

    /// <summary>Reads the raw brightness value and its maximum; on failure, returns false and sets <paramref name="error"/>.</summary>
    bool TryRead(out int current, out int maximum, out string? error);

    /// <summary>Writes a raw brightness value; on failure, returns false and sets <paramref name="error"/>.</summary>
    bool TryWrite(int value, out string? error);
}

internal interface IMonitorBackend
{
    /// <summary>
    /// Opens every physical monitor Windows knows about, whether or not it answers. The
    /// caller owns the connections. Failures to open are written to the log.
    /// </summary>
    IReadOnlyList<IMonitorConnection> Open();
}
