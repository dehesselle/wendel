# Manual

This document covers everything wendel can do and how to configure it.

## Bar and panel

The bar is a semi-transparent strip on the primary screen, always on top, that never takes the
keyboard focus. It is amber when monitors were found, grey when none answers, and blue while wendel
is [logging monitor values](#collecting-data-about-a-monitor).

Rest the cursor on the bar for a moment and the panel slides out of it. Moving across the bar
quickly doesn't open it. The panel closes shortly after the cursor leaves it, but not while a slider
is being dragged.

The panel has one vertical slider per monitor, ordered left to right as the monitors are arranged in
the Windows display settings, with the brightness in percent above each one.

- **Click or drag** anywhere in a slider's column to set it. A drag can leave the panel. A click on
  the closed bar only opens the panel.
- **Scroll** over the panel to change the slider under the cursor by `WheelStep` percent per notch.
  Scrolling over the closed bar opens the panel at once.

A monitor that reports an ambient light sensor gets an **Auto** checkbox below its slider, which
switches the monitor's automatic brightness on or off. It can take a few seconds to appear. While it
is checked, the slider is grey and can't be changed, but still shows the brightness. If the monitor
ignores the switch, the checkbox goes back to what the monitor reports. Monitors without a sensor have
no checkbox, and neither do those that report it differently from the standard, until wendel knows
their model. wendel stores no state of its own; the setting stays in the monitor.

The brightness and the checkbox are read again each time the panel opens, so changes made with the
monitor's own buttons show up, and so do changes made by its sensor. They aren't updated while the
panel stays open. With no monitor, the panel says "No DDC/CI monitor" and looks for monitors again
each time it opens.

While an app is full screen, the bar is hidden, just like the taskbar.

## Tray menu

Right-click the tray icon. The menu follows the Windows light or dark app mode.

| Item | Effect |
| --- | --- |
| Position | Left edge, right edge, top edge, or a small square in the bottom-right corner of the primary screen. |
| Start with Windows | Adds or removes `wendel` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. |
| Rescan monitors | Looks for monitors again. This also happens by itself when the display setup changes and on waking up. |
| Log monitor values | Writes all that monitors report to the [log file](#log-file). The bar is blue until that's done. See [Collecting data about a monitor](#collecting-data-about-a-monitor). |
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
indented by two spaces. The log stays short. It records:

- start and exit of wendel, with its version
- failed writes to a monitor, with the Windows error
- displays that can't be opened
- unexpected errors (exceptions) with their stack trace

Monitor values only appear when you ask for them with "Log monitor values". Each click writes one
entry per monitor:

- its position, Windows' description, its model, and the wendel profile it gets
- its raw capabilities string
- whether wendel can switch its automatic brightness, and with which code
- the current value and maximum of each code it lists and of the manufacturer codes `E0` to `FF`, or
  of all codes if it has no list; `-` means the code doesn't answer
- the Windows error instead, if it doesn't answer over DDC/CI at all

At startup, a log larger than 1 MB is renamed to `wendel.old.log`, replacing the previous one.

## Collecting data about a monitor

To get automatic brightness working on a monitor that has no Auto checkbox, wendel needs to know which
code the monitor uses. Nothing here changes anything on the monitor except the step you do yourself:

1. Connect the monitor directly, with DDC/CI enabled in its on-screen menu, and start wendel.
2. Choose "Log monitor values" in the tray menu. The bar turns blue and the menu item is greyed out
   while wendel reads the values. Wait until the bar has its usual color again. This takes about half
   a minute per monitor, or a few minutes if a monitor doesn't list its codes. A notification may
   appear as well, unless Windows hides it, for example with Do Not Disturb.
3. Flip the automatic brightness setting in the monitor's menu, and close the menu.
4. Choose "Log monitor values" again and wait in the same way.
5. Keep a copy of `wendel.log`. The code that changed between the two entries, and its two values,
   are what's needed to add the monitor.

## Troubleshooting

For anything about a specific monitor, ["Log monitor values"](#collecting-data-about-a-monitor) shows
what it answers.

- **Grey bar, "No DDC/CI monitor"**: no monitor answered. In order of likelihood:
  - DDC/CI is switched off in the monitor's on-screen menu. On Dell monitors it is under "Others".
  - The monitor is connected through a dock, hub, KVM switch or USB display adapter that doesn't
    pass DDC/CI. Try a direct cable.
  - It is a built-in laptop panel, which normally doesn't support DDC/CI.
  - It runs in a virtual machine, whose display has no connection to a real monitor.
- **The slider moves but the brightness doesn't change**: the monitor accepts the value but ignores
  it. This is typical with HDR enabled, and with presets or features that lock the brightness, such
  as Dell's Dynamic Contrast or a uniformity mode. Try the standard preset.
- **No Auto checkbox, although the monitor has automatic brightness**: the monitor's model isn't known
  to wendel, and it doesn't follow the standard. [Collect its data](#collecting-data-about-a-monitor).
- **The Auto checkbox doesn't affect the monitor, or doesn't follow its menu**: the monitor reports
  the standard code `0x66` but uses another for its automatic brightness.
  [Collect its data](#collecting-data-about-a-monitor).
- **The Auto checkbox disappears for a while**: it needs the monitor's capabilities, which the monitor
  didn't deliver during the last scan. Choose "Rescan monitors".
- **A monitor is missing after waking up**: choose "Rescan monitors". Monitors can take a while to
  answer again.
- **The wheel does nothing**: turn on "Scroll inactive windows when I hover over them" under
  Settings > Bluetooth & devices > Mouse. It is on by default.
- **Second start does nothing**: wendel is already running; look for its tray icon, possibly in the
  overflow area.
