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
  uses `CoreAdapter.CoreFileDisposalAdapter` (real Core) instead of `Simulation.SimulatedCore` —
  **confirmed on real Windows 11 Pro**: Core 73/73, Worker 204/204, real `--inspect`/`--shred`
  both correct (see `docs/ENGINEER1_OBSERVATIONS.md`, "Test machine 2"). `SSDSHREDDER_SIMULATE=1`
  remains available as a fallback. BitLocker/vault (`WindowsVaultPlatform`) is still a stub —
  don't expect `--vault-init` to do anything real.
- UI rules (`SSDShredder.UI`): Avalonia, cross-platform by design. Windows-only shell integration
  lives behind `OperatingSystem.IsWindows()` checks, never behind the TFM.
  `ActivationRouter.CreateServerStream` combined `PipeOptions.CurrentUserOnly` with an explicit
  `PipeSecurity`, which Windows rejects (found by a real Windows run; fixed by dropping the
  custom `PipeSecurity` and relying on `CurrentUserOnly` alone — needs a Windows re-run to
  confirm). The UI itself still talks to `SimulatedWorkerClient` — this is the one remaining
  integration seam. The real protocol is fully specified in
  `src/SSDShredder.Worker/README.md`'s "Integration seams" section and `contracts/v1/`: create a
  DACL'd named pipe `SSDShredder.v1.<32 hex>`, launch `SSDShredder.Worker.exe --ipc <32 hex>
  --parent-pid <ui pid>` unelevated, send `inspect` then one `shred` with the returned
  `selectionToken` on that same connection. Not written here: `SSDShredder.Worker.exe` is a
  `win-x64` binary and can't run on macOS even under Rosetta, so this couldn't be written *and
  verified* without a Windows machine.
- ShellExtension: first real build succeeded after PR #1's fixes (destructor `override` with no
  virtual base, a malformed `.rc` manifest line, missing `shlwapi.h`/`shlobj.h` includes, a
  `.def` LIBRARY name mismatch, `/MANIFEST:NO` for the linker's duplicate-manifest conflict).
  Build only — install/register/Explorer/drop-target checks are still open.
- Wire contract: `contracts/v1/` holds frozen JSON schemas, the exit-code map, and
  schema-checked examples (see `contracts/v1/README.md` for how they were checked and why
  `worker-event.schema.json` alone can't tell you which evidence shape applies).
- Never write to physical-sector addresses or NTFS metadata, anywhere in this repo.
