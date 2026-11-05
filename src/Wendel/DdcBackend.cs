using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Wendel;

/// <summary>
/// Real monitors, through the Monitor Configuration API in dxva2.dll. Windows implements the
/// DDC/CI transport; this class knows nothing of what the codes mean.
/// </summary>
internal sealed class DdcBackend : IMonitorBackend
{
    public IReadOnlyList<IMonitorConnection> Open()
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            handles.Add(monitor);
            return true;
        }, IntPtr.Zero);

        var connections = new List<IMonitorConnection>();
        foreach (var monitor in handles)
            OpenPhysicalMonitors(monitor, connections);
        return connections;
    }

    private static void OpenPhysicalMonitors(IntPtr monitor, List<IMonitorConnection> connections)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        GetMonitorInfo(monitor, ref info);

        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0)
            return;
        var physical = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
        {
            Log.Error($"Could not open the display at {info.rcMonitor.Left},{info.rcMonitor.Top}", LastError());
            return;
        }
        foreach (var p in physical)
            connections.Add(new Connection(p, info.rcMonitor.Left, info.rcMonitor.Top));
    }

    private static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    private sealed class Connection : IMonitorConnection
    {
        private readonly PHYSICAL_MONITOR monitor;

        public Connection(PHYSICAL_MONITOR monitor, int x, int y)
        {
            this.monitor = monitor;
            ScreenX = x;
            ScreenY = y;
        }

        public string Description => monitor.szPhysicalMonitorDescription;

        public int ScreenX { get; }

        public int ScreenY { get; }

        public bool TryRead(byte code, out int current, out int maximum, out string? error)
        {
            current = maximum = 0;
            if (!GetVCPFeatureAndVCPFeatureReply(monitor.hPhysicalMonitor, code, out _, out uint cur, out uint max))
            {
                error = LastError();
                return false;
            }
            current = (int)cur;
            maximum = (int)max;
            error = null;
            return true;
        }

        public bool TryWrite(byte code, int value, out string? error)
        {
            if (!SetVCPFeature(monitor.hPhysicalMonitor, code, (uint)value))
            {
                error = LastError();
                return false;
            }
            error = null;
            return true;
        }

        public bool TryReadCapabilities(out string? capabilities, out string? error)
        {
            capabilities = null;
            if (!GetCapabilitiesStringLength(monitor.hPhysicalMonitor, out uint length))
            {
                error = LastError();
                return false;
            }
            var buffer = new StringBuilder((int)length);
            if (!CapabilitiesRequestAndCapabilitiesReply(monitor.hPhysicalMonitor, buffer, length))
            {
                error = LastError();
                return false;
            }
            capabilities = buffer.ToString();
            error = null;
            return true;
        }

        public void Dispose()
        {
            DestroyPhysicalMonitor(monitor.hPhysicalMonitor);
        }
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, out uint pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint pdwCapabilitiesStringLengthInCharacters);

    // The string is ASCII, and the length includes the terminating null character.
    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, [Out, MarshalAs(UnmanagedType.LPStr)] StringBuilder pszASCIICapabilitiesString, uint dwCapabilitiesStringLengthInCharacters);

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
