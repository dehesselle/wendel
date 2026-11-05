using System;
using System.Collections.Generic;

namespace Wendel;

/// <summary>How one kind of monitor switches its automatic brightness over DDC/CI.</summary>
internal sealed class AutoBrightnessControl
{
    private readonly int readMask;

    /// <param name="code">The VCP code.</param>
    /// <param name="off">The value for off, written and expected back.</param>
    /// <param name="on">The value for on, written and expected back.</param>
    /// <param name="readMask">The bits of a reported value that count.</param>
    public AutoBrightnessControl(byte code, int off, int on, int readMask = ~0)
    {
        Code = code;
        Off = off;
        On = on;
        this.readMask = readMask;
    }

    public byte Code { get; }

    public int Off { get; }

    public int On { get; }

    /// <summary>
    /// What a monitor's reply means: true for on, false for off, null if the monitor doesn't
    /// support this. A maximum of 0 means "not supported" in MCCS.
    /// </summary>
    public bool? Decode(int reported, int maximum)
    {
        if (maximum == 0)
            return null;
        int value = reported & readMask;
        if (value == On)
            return true;
        if (value == Off)
            return false;
        return null;
    }
}

/// <summary>
/// What is known about specific monitor models, by the name in their capabilities string. To add
/// a monitor, add a line to <see cref="Known"/>; docs/DEVELOPMENT.md has the steps.
/// </summary>
internal static class MonitorProfiles
{
    public const string StandardName = "standard";

    /// <summary>MCCS 2.2a: Ambient Light Sensor, 01h disabled, 02h enabled.</summary>
    public static readonly AutoBrightnessControl Standard = new(code: 0x66, off: 1, on: 2);

    private static readonly Dictionary<string, AutoBrightnessControl> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Reports a maximum of 1. Has reported 81h and 82h, including after a change from its
        // on-screen menu; once written with plain 1 or 2 it reports plain values, menu changes too.
        ["U3225QE"] = new(code: 0x66, off: 1, on: 2, readMask: 0x7F),
    };

    /// <summary>The control for a model; the standard one if the model is unknown or null.</summary>
    public static AutoBrightnessControl For(string? model) =>
        model is not null && Known.TryGetValue(model, out var control) ? control : Standard;

    /// <summary>The name of the profile a model gets, for the log.</summary>
    public static string NameFor(string? model) =>
        model is not null && Known.ContainsKey(model) ? model : StandardName;
}
