# Manual

This document covers everything wendel can do and how to configure it.

## Bar and panel

The bar is a semi-transparent strip on the primary screen, always on top, that never takes the
keyboard focus. It is amber when monitors were found and grey when none answers.

Rest the cursor on the bar for a moment and the panel slides out of it. Moving across the bar
quickly doesn't open it. The panel closes shortly after the cursor leaves it, but not while a slider
is being dragged.

The panel has one vertical slider per monitor, ordered left to right as the monitors are arranged in
the Windows display settings, with the brightness in percent above each one.

- **Click or drag** anywhere in a slider's column to set it. A drag can leave the panel. A click on
  the closed bar only opens the panel.
- **Scroll** over the panel to change the slider under the cursor by `WheelStep` percent per notch.
  Scrolling over the closed bar opens the panel at once.

The brightness is read again each time the panel opens, so changes made with the monitor's own
buttons show up. With no monitor, the panel says "No DDC/CI monitor" and looks for monitors again
each time it opens.

While an app is full screen, the bar is hidden, just like the taskbar.

## Tray menu

Right-click the tray icon. The menu follows the Windows light or dark app mode.

| Item | Effect |
| --- | --- |
| Position | Left edge, right edge, top edge, or a small square in the bottom-right corner of the primary screen. |
| Start with Windows | Adds or removes `wendel` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. |
| Rescan monitors | Looks for monitors again. This also happens by itself when the display setup changes and on waking up. |
| Open settings file | Opens `settings.json` in Notepad, creating it with defaults if needed. |
| Open log file | Opens the [log file](#log-file) in Notepad. |
| Exit | Quits wendel. |

Only one instance runs at a time; starting it again exits immediately with exit code 1.

## Settings file

`%APPDATA%\wendel\settings.json`. Changes are picked up as soon as the file is saved.

```json
{
  "Position": "Left",
  "Opacity": 0.5,
  "CollapsedThickness": 8,
  "WheelStep": 5
}
```

| Key | Meaning | Default |
| --- | --- | --- |
| `Position` | `Left`, `Right`, `Top` or `BottomRightCorner`. | `Left` |
| `Opacity` | Opacity of the closed bar, 0.1 to 1. | `0.5` |
| `CollapsedThickness` | Thickness of the closed bar in pixels at 100 % scaling, 2 to 64. | `8` |
| `WheelStep` | Percentage points per notch of the mouse wheel, 1 to 50. | `5` |

A broken settings file is reported in a tray notification and the defaults are used.

## Log file

`%LOCALAPPDATA%\wendel\wendel.log`, also reachable through "Open log file" in the tray menu. Each
entry starts with a line holding the time, `INFO` or `ERROR`, and a message; details follow on lines
indented by two spaces. The log records:

- start and exit of wendel, with its version
- every scan: each monitor's position, Windows' description, and its brightness and maximum, or the
  Windows error if it doesn't answer
- failed reads and writes, with the Windows error
- unexpected errors (exceptions) with their stack trace

At startup, a log larger than 1 MB is renamed to `wendel.old.log`, replacing the previous one.

## Troubleshooting

Look at the [log file](#log-file) first; it shows what each monitor answered.

- **Grey bar, "No DDC/CI monitor"**: no monitor answered. In order of likelihood:
    - DDC/CI is switched off in the monitor's on-screen menu. On Dell monitors it is under "Others".
    - The monitor is connected through a dock, hub, KVM switch or USB display adapter that doesn't
      pass DDC/CI. Try a direct cable.
    - It is a built-in laptop panel, which normally doesn't support DDC/CI.
    - It runs in a virtual machine, whose display has no connection to a real monitor.
- **The slider moves but the brightness doesn't change**: the monitor accepts the value but ignores
  it. This is typical with HDR enabled, and with presets or features that lock the brightness, such
  as Dell's Dynamic Contrast or a uniformity mode. Try the standard preset.
- **A monitor is missing after waking up**: choose "Rescan monitors". Monitors can take a while to
  answer again.
- **The wheel does nothing**: turn on "Scroll inactive windows when I hover over them" under
  Settings > Bluetooth & devices > Mouse. It is on by default.
- **Second start does nothing**: wendel is already running; look for its tray icon, possibly in the
  overflow area.
