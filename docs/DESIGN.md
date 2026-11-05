# Design

This document describes how wendel is built and why.

## DDC/CI

DDC/CI is the channel between a computer and a monitor, running over I2C on the video cable. VESA
specifies the transport (DDC/CI) and the commands (MCCS, the Monitor Control Command Set). There is
no RFC.

wendel doesn't implement any of this. Windows does, in the Monitor Configuration API in
`dxva2.dll`, with no driver and no admin rights. All wendel knows of the protocol are two VCP codes
(see `Vcp` in `MonitorBackend.cs`) and the format of the capabilities string:

| Code | MCCS name | Values |
| --- | --- | --- |
| `0x10` | Luminance | 0 to the maximum the monitor reports. |
| `0x66` | Ambient Light Sensor | `01h` disabled, `02h` enabled. A maximum of `00h` means not supported. |

Monitors can differ from the standard. The Dell U3225QE, which has this sensor, reports a maximum of
1 rather than 2. After a change from its on-screen menu it reports `81h` or `82h`; it takes `01h` and
`02h` when written and then reports those. wendel therefore ignores the high bit when reading and
writes plain values. The U3225QE's `0x10` reports the brightness the sensor chose.

## Architecture

| File | Role |
| --- | --- |
| `Program.cs` | Single-instance mutex, picks the backend, starts the WinForms message loop with `TrayContext`. |
| `TrayContext.cs` | `ApplicationContext` connecting the tray icon, menu, bar, worker, settings and the settings file watcher. |
| `BarForm.cs` | Borderless topmost tool window; bar, panel, animation, mouse input, painting, hiding during full screen. |
| `SliderLayout.cs` | Geometry of the panel: columns, tracks, cursor position to percent. |
| `BrightnessWorker.cs` | Background thread that owns the monitor connections: scans, reads, rate-limited writes. |
| `Capabilities.cs` | Gets the VCP codes out of a capabilities string. |
| `MonitorBackend.cs` | The interfaces between the worker and a source of monitors, and the VCP codes. |
| `DdcBackend.cs` | Real monitors, through `dxva2.dll`. |
| `SimulatedBackend.cs` | Monitors in memory, for the tests and `--simulate`. |
| `Settings.cs` | JSON settings via `DataContractJsonSerializer`. |
| `Log.cs` | Appends entries to the log file. |
| `Artwork.cs`, `NativeMethods.cs` | The icon drawn by code and the few Win32 calls needed. |
| `tools/IconGen` | Build-time tool that renders the app icon from `Artwork` into the `.ico` compiled into the exe. |

Flow: the cursor rests on the bar → `BarForm` opens and raises `Opening` → the worker reads the
monitors again → the user drags or scrolls → `BarForm` shows the new value at once and raises
`PercentRequested` → the worker writes it → the worker reports the monitors through `Changed`,
which `TrayContext` marshals to the form with `BeginInvoke`.

## Decisions

- **.NET Framework 4.8 instead of modern .NET.** It ships with Windows and is serviced by Windows
  Update, so the app is a single small exe with nothing to install and no bundled runtime to keep
  patched.
- **VCP calls instead of `GetMonitorBrightness`.** The high-level API first needs the monitor's
  capabilities string, which is slow to fetch and unreliable on some monitors, and offers nothing
  here that reading VCP `0x10` doesn't. A monitor is simply probed by reading the value.
- **All monitor calls on one background thread.** A DDC/CI call blocks for tens of milliseconds,
  which would make the UI stutter. One thread rather than one per monitor keeps the connections
  free of locking; the delay this adds between monitors doesn't matter, as a person moves one
  slider at a time.
- **Latest value wins, writes rate limited to one per 100 ms.** A drag produces far more values
  than the bus can take and a queue would make the brightness trail behind the cursor. The worker
  keeps one pending value per monitor, so the last value always arrives.
- **The panel shows what the user asked for until things settle.** Reports from the worker lag
  behind a drag by the rate limit, so painting them would make the thumb jump back. If a write
  fails, the slider returns to the monitor's real value after 600 ms.
- **Three attempts per read.** DDC/CI reads fail now and then on a healthy monitor. A monitor that
  fails all three during a scan gets no slider; one that fails during a refresh keeps its last
  value, and a later write will reveal whether it is gone.
- **Rescan on display changes, on resume, and after a failed write.** Monitor handles go stale when
  the display setup changes. The delay of 1.5 s gives a waking monitor time to answer and merges the
  several events one change raises. If no monitor was found, every opening of the panel rescans
  too, which covers a monitor that was off during the last scan and raised no event since.
- **Read again on every opening.** The brightness can change through the monitor's own buttons,
  and there is no notification for that. Polling would keep the bus busy for no benefit while the
  panel is closed.
- **Support for automatic brightness decided by reading `0x66`, not from the capabilities string.**
  The read answers exactly what matters, in the terms the standard defines, and takes milliseconds
  where the string can take seconds. A monitor that doesn't report support gets no checkbox, because
  Microsoft warns that codes a monitor doesn't implement can cause undefined behavior.
- **The checkbox shows what the monitor reports.** After a switch, the worker reads the code back,
  since monitors can accept a write and do nothing. It reads the brightness too, as the switch may
  change it. The state lives in the monitor, so wendel stores nothing and buttons on the monitor stay
  in charge.
- **A disabled slider stays visible.** The value is still useful to see while the sensor is in
  charge; a brightness written then would only fight the sensor, so the UI refuses it, and switching
  on discards one still waiting.
- **Capabilities logged once per monitor and run, after the sliders are up.** The request can take
  several seconds, which is too long to hold up the sliders or to repeat on every wake-up. A failed
  request is retried on the next scan. The list of codes is there for monitors that use something
  other than `0x66` for automatic brightness, such as a code from the manufacturer range `0xE0` to
  `0xFF`.
- **Sliders ordered by screen position, without names.** Windows' description is often only
  "Generic PnP Monitor", and two monitors of one model would look alike even with real names.
  The order on the desk tells them apart. The descriptions go to the log.
- **One window for bar and panel, painted at the panel's final position.** The window grows from
  the bar while revealing the finished panel, which gives the slide without a second window. All
  cursor positions are computed against the panel's final bounds, so input is right during the
  animation as well.
- **Cursor checked on a timer instead of leave messages.** The window changes size under the cursor,
  and a missed `WM_MOUSELEAVE` would leave the panel open. The timer only runs from the first hover
  until the panel has closed, so an idle bar costs nothing.
- **Wheel messages without a global hook.** Windows delivers them to the window under the cursor
  with the default "Scroll inactive windows when I hover over them" setting. A low-level mouse hook
  would make every mouse event in the system pass through this process, just to support turning
  that setting off.
- **Left edge by default.** It stays out of the way of tools that use the right edge, such as
  drop2mpv.
- **Per-monitor DPI via `app.manifest`, scaling done by hand.** WinForms on .NET Framework only
  rescales automatically with an `App.config` opt-in, which would add a second file. The form uses
  `AutoScaleMode.None`, paints itself and computes all sizes from the monitor DPI.
- **Bar on the primary screen's working area.** Simple and predictable; it moves with the taskbar
  and relayouts on display and DPI changes.
- **Hidden during full screen via `ABN_FULLSCREENAPP`.** The form registers as an appbar (`ABM_NEW`
  only, so it reserves no space) and gets the same signal the taskbar hides on. This needs no
  polling and no heuristics. It registers again on `TaskbarCreated` after Explorer restarts. An
  open panel stays until it closes, then the bar hides.
- **A log file for the details.** It lives in `%LOCALAPPDATA%` because it is machine-local, unlike
  the roaming settings. Each entry opens, appends and closes the file under a lock, so the file is
  never held open. One rotation step at startup keeps it bounded. A failure to log is ignored.
- **Native `ContextMenu` instead of `ContextMenuStrip`.** `ContextMenuStrip` paints itself and
  ignores the system theme. Native menus only turn dark through the undocumented uxtheme exports
  `SetPreferredAppMode` (ordinal 135) and `FlushMenuThemes` (136). Without these exports the menu
  just stays light.
- **Exe icon rendered at build time.** A Win32 icon has to be compiled in, so the build runs
  `tools/IconGen`, which links `Artwork.cs` and writes a multi-size `.ico` to `obj/`. The exe and
  tray icons come from the same drawing code.

## Core principles

1. **The bar never takes focus** (`WS_EX_NOACTIVATE`, `ShowWithoutActivation`, `MA_NOACTIVATE`),
   because adjusting the brightness must not disturb the window being worked in.
2. **Only `BrightnessWorker`'s thread touches monitor connections**, because the calls block and
   the connections aren't synchronized.
3. **DDC/CI calls only at startup, on a user action or on a display event.** Nothing polls, because
   the bus is slow and has no need to be busy.
4. **The sliders' percentages are converted through each monitor's reported maximum**, because MCCS
   doesn't guarantee a range of 0 to 100.
5. **The repository has no binary assets**; icons are drawn by code, at runtime for the tray and at
   build time for the exe.
