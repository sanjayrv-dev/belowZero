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
  docs/02 and `contracts/v1/` specify. `WorkerEnvironment.Create()`'s real (non-simulated) mode
  now uses `CoreAdapter.CoreFileDisposalAdapter` (real Core) instead of `Simulation.SimulatedCore`.
  This was wired without a Windows machine to verify it on — run `SSDShredder.Core.Tests` and
  `SSDShredder.Worker.Tests` on real Windows, then a live GUI inspect-then-shred pass on a
  disposable file, before trusting it with real data. `SSDSHREDDER_SIMULATE=1` remains available
  as a safe fallback if the real path misbehaves.
- UI rules (`SSDShredder.UI`): Avalonia, cross-platform by design. Windows-only shell integration
  lives behind `OperatingSystem.IsWindows()` checks, never behind the TFM. Still talks to
  `SimulatedWorkerClient` — this is the one remaining integration seam. The real protocol is
  fully specified in `src/SSDShredder.Worker/README.md`'s "Integration seams" section and
  `contracts/v1/`: create a DACL'd named pipe `SSDShredder.v1.<32 hex>`, launch
  `SSDShredder.Worker.exe --ipc <32 hex> --parent-pid <ui pid>` unelevated, send `inspect` then
  one `shred` with the returned `selectionToken` on that same connection. Not written here:
  `SSDShredder.Worker.exe` is a `win-x64` binary and can't run on macOS even under Rosetta, so
  this couldn't be written *and verified* without a Windows machine.
- Windows-only .NET API gotcha (UI): `PipeOptions.CurrentUserOnly` cannot be combined with an
  explicit `PipeSecurity` — `NamedPipeServerStreamAcl.Create` throws `ArgumentException`. Use
  `PipeOptions.Asynchronous` plus the explicit current-user DACL instead (fixed in
  `ActivationRouter.CreateServerStream`; it only surfaced when `SSDShredder.UI.Tests` ran on real
  Windows). Apply the same rule to the real worker-pipe client/server when wiring it. Run
  `dotnet test` on Windows before merging UI pipe/ACL code.
- Wire contract: `contracts/v1/` holds frozen JSON schemas, the exit-code map, and
  schema-checked examples (see `contracts/v1/README.md` for how they were checked and why
  `worker-event.schema.json` alone can't tell you which evidence shape applies).
- Never write to physical-sector addresses or NTFS metadata, anywhere in this repo.
