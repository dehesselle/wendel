# wendel

A Windows tray app (C# WinForms, .NET Framework 4.8) that sets monitor brightness over DDC/CI from a slider panel at the screen edge.

- Read docs/DESIGN.md before architectural changes and respect its core principles.
- docs/MANUAL.md describes user-visible behavior and the settings file.

@docs/DEVELOPMENT.md

## Rules

- Never install build tools on this machine. Edit locally, build and test on the devbox (`just all`).
- Keep the code compatible with .NET Framework 4.8: no APIs from modern .NET, no C# features that need newer runtime types (ranges, `init`, records without polyfills).
- The README stays short. A change that affects design, usage or behavior updates the matching document in `docs/` in the same change. DESIGN.md is kept current and carries no project history.
- Every file states its copyright and license; see Licensing in docs/DEVELOPMENT.md before adding one.
