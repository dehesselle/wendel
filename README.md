# wendel

A small Windows tray app that sets the brightness of external monitors over DDC/CI, so you
don't have to reach for the buttons on the monitor. It keeps a thin bar at the edge of the
screen. Hover over the bar and a small panel appears with one vertical slider per monitor; drag
a slider or scroll the mouse wheel over it.

It targets .NET Framework 4.8, which is part of Windows 10 (1903 and later) and Windows 11, so
there's nothing to install.

## Usage

1. Put `wendel.exe` somewhere permanent and start it.
2. Hover over the bar at the left screen edge until the panel opens.
3. Drag a slider or scroll over it. The sliders run left to right in the order of your monitors.
4. Right-click the tray icon to choose the bar's position or enable "Start with Windows".

A monitor needs DDC/CI enabled in its on-screen menu. Built-in laptop panels normally don't
support DDC/CI and get no slider.

## Documentation

- [docs/MANUAL.md](docs/MANUAL.md): the panel, tray menu, settings file, log file, troubleshooting.
- [docs/DESIGN.md](docs/DESIGN.md): architecture, design decisions and core principles.
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md): building and testing on a Windows devbox, CI.
