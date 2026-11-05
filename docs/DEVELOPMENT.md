# Development

This document covers building, testing, licensing and CI.

## Layout

```text
Wendel.slnx
Directory.Build.props        # net48, latest C#, nullable
src/Wendel/                  # the app
tests/Wendel.Tests/          # xUnit tests (net48)
tools/IconGen/               # renders the exe icon during the build
.github/workflows/build.yml  # CI
justfile                     # devbox recipes
LICENSE                      # the license, with its copyright line filled in
LICENSES/                    # license texts, for REUSE
REUSE.toml                   # copyright and license of files without a header
```

## Building on a Windows devbox

No build tools are installed on the development machine. Code is edited locally and built on a
Windows VM reached over SSH (`.devbox/ssh_config`, alias `clouding-windows`, created with the
`clouding-devbox` skill). The VM needs the .NET 10 SDK:

```powershell
# on the devbox
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile C:/dotnet-install.ps1
C:/dotnet-install.ps1 -Channel 10.0 -InstallDir C:\dotnet   # then add C:\dotnet to the machine PATH and restart sshd
```

The .NET 10 SDK builds net48 projects; the Framework runtime is part of Windows.

Recipes, run locally:

```sh
just push    # replace C:/src/wendel on the devbox with the local sources
just build   # dotnet build -c Release -warnaserror, then dotnet test
just pull    # copy wendel.exe back into the project root
just all     # all three
```

The exe ends up in `src/Wendel/bin/Release/net48/wendel.exe` on the devbox and in `./wendel.exe`
locally (git-ignored).

## Tests

`dotnet test` covers the parts that don't need a desktop or a monitor:

- `BrightnessWorkerTests`: against the simulated backend, scanning and ordering, read retries,
  monitors that never answer, write coalescing and rate limit, independent monitors, conversion to
  a monitor's range, refreshes, the rescan after a failed write, detecting and switching automatic
  brightness through a profile, monitors that ignore a switch, what the log gets by default, and
  "Log monitor values".
- `CapabilitiesTests`: VCP codes and the model from capabilities strings, with nested values, without
  spaces, truncated, with other sections that look similar, and a real one from a U3225QE.
- `MonitorProfilesTests`: what each control accepts and rejects, and which model gets which control.
- `SliderLayoutTests`: cursor position to percent and back, clamping, column hit tests, the checkbox
  row, and scaling.
- `SettingsTests`: JSON round trip, defaults for missing members, clamping.
- `LogTests`: entry format and rotation.

### What a VM can't test

A VM's display has no connection to a real monitor, so `DdcBackend` can only fail there: the scan
logs "The request is not supported". A notebook's built-in panel doesn't get further: on a Dell XPS
17, the scan took about 2 s and logged an I2C transmission error. **A real brightness change needs a
physical machine with an external DDC/CI monitor.** Copy `wendel.exe` there; it needs no tools.

Start the exe from a logged-in desktop session. An SSH session has no real desktop, so the monitors
it sees aren't the real ones, and the app exits at once: setting the bar's opacity fails there with
"A device attached to the system is not functioning", which it logs. From SSH, register a scheduled
task with an interactive principal:

```powershell
$action = New-ScheduledTaskAction -Execute 'C:\path\to\wendel.exe'
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive
Register-ScheduledTask -TaskName wendel -Action $action -Principal $principal -Force
Start-ScheduledTask -TaskName wendel
```

On the machine with the monitor, check that:

- "Log monitor values" shows the monitor's model and profile, and the expected brightness and maximum
- the log otherwise has no monitor values
- dragging and scrolling change the brightness smoothly, and it ends where the slider ends
- a change through the monitor's own buttons shows up on the next opening
- the Auto checkbox follows the monitor's on-screen setting on the next opening, and the on-screen
  setting follows the checkbox
- it still works after sleep and wake, and after unplugging and replugging the monitor

### The UI with simulated monitors

`wendel.exe --simulate N` uses N simulated monitors instead of real ones (2 without a number, 0 for
the "No DDC/CI monitor" state). Only the first has an ambient light sensor, so one run shows a slider
with and one without a checkbox. This works on the devbox, in a desktop session: RDP in, or set
autologon on the VM and start the exe with the scheduled task above. In such a session, a script
can drive the app with `SetCursorPos` and `mouse_event` and take screenshots with
`Graphics.CopyFromScreen`. Keep a tray menu sequence in one process: a process starting while the
menu is open takes the foreground, which closes the menu.

Check that:

- passing quickly over the bar doesn't open it, resting on it does, and it closes after leaving
- the foreground window stays in the foreground through hover, click, drag and scroll
- a drag can leave the panel without closing it; releasing outside closes it
- the wheel changes the slider under the cursor, also with fractions of a notch
- checking Auto greys the slider and keeps its value, click, drag and wheel do nothing there, and
  unchecking enables it again, without touching the other slider
- all four positions work, including the tray menu's position items
- the bar hides during full screen and comes back
- all of this looks right at 150 % scaling (`LogPixels` 144 and `Win8DpiScaling` 1 under
  `HKCU\Control Panel\Desktop`, then log in again)
- the tray menu follows light and dark app mode, and autostart toggles
- "Log monitor values" writes an entry per monitor, with the bar blue and the menu item disabled until
  then, and shows its notification unless Windows hides it
- a second instance exits with code 1

## Adding a monitor

A monitor needs a profile only if its automatic brightness differs from the standard. Without one, it
gets the standard control, or no checkbox if it doesn't answer as the standard says.

1. Get two "Log monitor values" entries from it, as described in the
   [manual](MANUAL.md#collecting-data-about-a-monitor).
2. Compare them. The code that changed with the setting is the one to use; ignore unrelated codes
   that change between any two reads, by comparing a third entry if in doubt. The entry's `model` is
   the key for the profile.
3. Add a line to `Known` in `src/Wendel/MonitorProfiles.cs` with the code and the values for off and
   on. If the reported values have bits on top of those, add a `readMask`.
4. Add the differences to the table in [DESIGN.md](DESIGN.md#monitor-profiles), and a test to
   `MonitorProfilesTests`.
5. On the monitor, check that writing the values does switch the setting, as a monitor can report
   other values than it takes: the checkbox has to follow the menu and the menu the checkbox.

## Licensing

Every file states its copyright and license in the [REUSE](https://reuse.software) format: a header
where the format allows comments, otherwise an entry in `REUSE.toml`. The license text is in
`LICENSES/`.

```csharp
// SPDX-FileCopyrightText: 2026 René de Hesselle <dehesselle@web.de>
//
// SPDX-License-Identifier: 0BSD
```

A new file names its author and is `0BSD`. To check, without a devbox:

```sh
uvx --from 'reuse~=6.2.0' reuse lint
```

## CI

`.github/workflows/build.yml` runs for pushes, pull requests and manual runs. On `windows-latest`, it
builds with warnings as errors, tests, and uploads `wendel.exe` as the `wendel` artifact. A separate
job on `ubuntu-latest` checks REUSE compliance.

Pushing a tag that starts with a lowercase `v` makes a release:

```sh
git tag v1.2.3
git push origin v1.2.3
```

The tag build stamps the tag without the `v` as the exe's version (other builds use `<Version>` from
`Wendel.csproj`). It then creates a draft GitHub Release named after the tag, with `wendel.exe`
attached and generated release notes. Review the draft on GitHub and publish it manually.
