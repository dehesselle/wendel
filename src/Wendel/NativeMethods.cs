// SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
//
// SPDX-License-Identifier: 0BSD

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wendel;

internal static class NativeMethods
{
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_APP = 0x8000;
    public const int MA_NOACTIVATE = 3;
    public const int ABN_FULLSCREENAPP = 2;

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint ABM_NEW = 0;
    private const uint ABM_REMOVE = 1;
    private const int APPMODE_FORCEDARK = 2;
    private const int APPMODE_FORCELIGHT = 3;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // Undocumented, exported by ordinal only (Windows 10 1903 and later).
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
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
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    /// <summary>
    /// Registers <paramref name="hwnd"/> as an appbar without reserving screen space, so it
    /// receives <see cref="ABN_FULLSCREENAPP"/> as <paramref name="callbackMessage"/>.
    /// </summary>
    public static void RegisterAppBar(IntPtr hwnd, int callbackMessage)
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA)),
            hWnd = hwnd,
            uCallbackMessage = (uint)callbackMessage,
        };
        SHAppBarMessage(ABM_NEW, ref data);
    }

    public static void UnregisterAppBar(IntPtr hwnd)
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA)),
            hWnd = hwnd,
        };
        SHAppBarMessage(ABM_REMOVE, ref data);
    }

    /// <summary>Effective DPI of the monitor containing <paramref name="point"/>, 96 on failure.</summary>
    public static int GetDpiAt(Point point)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTOPRIMARY);
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0)
                return (int)dpi;
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
        }
        return 96;
    }

    /// <summary>
    /// Makes the process's native menus light or dark to match the Windows app mode.
    /// </summary>
    public static void ApplyMenuTheme()
    {
        try
        {
            SetPreferredAppMode(IsAppModeLight() ? APPMODE_FORCELIGHT : APPMODE_FORCEDARK);
            FlushMenuThemes();
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
        }
    }

    private static bool IsAppModeLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
