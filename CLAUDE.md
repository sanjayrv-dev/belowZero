# belowZero / SSD Shredder — conventions

- Specs: docs/01 (Windows storage/vault), docs/02 (architecture + wire contract v1), docs/03 (build plan), docs/04 (demo).
- Solution: `SSDShredder.sln` at the repo root covers the four .NET projects and their tests:
  `SSDShredder.Core`, `SSDShredder.Worker`, `SSDShredder.UI`, plus `*.Tests` for each.
  `SSDShredder.ShellExtension` (native C++, Windows COM) builds separately via CMake —
  see `src/SSDShredder.ShellExtension/README.md`.
- Build/test: `dotnet build` / `dotnet test` from the repo root.
  - `SSDShredder.Core` / `SSDShredder.Worker` target `net9.0-windows` and build on macOS/Linux
    via `EnableWindowsTargeting` (Directory.Build.props), but most of their tests need real
    Windows APIs (native handles, `WindowsIdentity`, actual `win-x64` process execution) and
    only run meaningfully on Windows. `SSDShredder.UI` targets plain `net9.0` and fully builds
    and runs cross-platform.
  - `TreatWarningsAsErrors` is on repo-wide (Directory.Build.props) — this includes the xUnit
    analyzers (e.g. no blocking `.Result`/`.Wait()` in test methods; use `async Task` + `await`).
- Core rules (`SSDShredder.Core`): checked arithmetic, validate SDK struct sizes and driver counts,
  capture `Marshal.GetLastPInvokeError()` immediately, never write physical sectors or NTFS
  metadata, unlink only after all planned trim ranges succeed.
- Worker rules (`SSDShredder.Worker`): own the NDJSON wire contract and exit codes exactly as
  docs/02 specifies; currently runs against `Simulation.SimulatedCore` — wiring a real
  `IFileDisposalCore` adapter over `SSDShredder.Core`'s `TrimEngine`/`FileInspector` is the next
  integration step (needs a Windows machine to verify; see the note in
  `src/SSDShredder.Worker/CoreAdapter/IFileDisposalCore.cs`).
- UI rules (`SSDShredder.UI`): Avalonia, cross-platform by design. Windows-only shell integration
  lives behind `OperatingSystem.IsWindows()` checks, never behind the TFM. Currently talks to a
  `SimulatedWorkerClient`; wiring a real client that launches `bzo.exe` and speaks the NDJSON
  protocol is the next integration step.
- Never write to physical-sector addresses or NTFS metadata, anywhere in this repo.
