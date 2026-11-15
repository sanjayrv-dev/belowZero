# belowZero

A persistent Windows desktop "bin" and the terminal command `/bzo` for securely
disposing of a file via NTFS file-level TRIM (or routing it into an encrypted
vault when that isn't possible). Built for a hackathon by three engineers in
parallel; see [docs/](docs/) for the full research, architecture, wire
contract, and build plan.

## Status for this submission (prototype, feature-frozen)

- **The UI panel may be running against a simulated worker.** `SimulatedWorkerClient`
  is the UI's default; a real worker connection exists in code but is opt-in and
  unconfirmed (see below). Any recording of the Avalonia panel should be labeled
  as such unless `SSDSHREDDER_REAL_WORKER=1` has actually been verified working.
- **Physical NVMe file-level TRIM was refused on every physical drive tested —
  two independent machines, two different NVMe models.** Both returned Win32
  error 326 ("device does not support file-level TRIM"). The real Worker's
  response to that refusal is **exit 7, and the target file is left in place** —
  a verified, intentional "clean refusal," not a bug. A thin-provisioned VHDX
  did acknowledge the raw request, but that only shows the request layout is
  correct, not that any block was actually deallocated — it says nothing about
  physical media. See `docs/ENGINEER1_OBSERVATIONS.md` for the full readout.
- **`/bzo` is not a working launcher yet.** Only the PowerShell/cmd alias
  *installer templates* exist (`src/SSDShredder.Worker/Install/`); nothing
  calls them during install, no executable is named `bzo.exe`, and there is no
  no-argument "open the UI" behavior wired up. Don't record `/bzo` as if it runs.
- **The real BitLocker vault is unfinished.** `WindowsVaultPlatform` is still a
  stub in every build on `main`; the in-progress real implementation lives on
  the unmerged `feat/vhdx-vault` branch, kept separate deliberately. Don't
  record `--vault-init`/`--vault-close` as doing anything real.

## Layout

```text
SSDShredder.sln
├── src/
│   ├── SSDShredder.Core/             net9.0-windows · file handles, topology, extents, trim (Engineer 1)
│   ├── SSDShredder.Worker/           net9.0-windows · SSDShredder.Worker.exe CLI, wire protocol, vault lifecycle (Engineer 2)
│   │                                  (publishes as SSDShredder.Worker.exe, not bzo.exe — see "Status" above)
│   ├── SSDShredder.UI/               net9.0 · Avalonia bin panel, confirmation, status, picker (Engineer 3)
│   └── SSDShredder.ShellExtension/   native C++ (CMake) · desktop broker, Explorer command, installer (Engineer 3)
├── tests/                            one *.Tests project per src project above
├── contracts/v1/                     frozen JSON schemas, exit-code map, and schema-checked examples for the wire protocol
└── docs/                             research, architecture, build plan, demo playbook, Engineer 1's hardware notes
```

## Build and test

```sh
dotnet build SSDShredder.sln
dotnet test SSDShredder.sln
```

`SSDShredder.Core` and `SSDShredder.Worker` target `net9.0-windows` but build
on macOS/Linux too, via `EnableWindowsTargeting` in `Directory.Build.props`.
Most of their tests need real Windows behavior (native file handles,
`WindowsIdentity`, actually running a `win-x64` binary) and only run
meaningfully on Windows — on macOS/Linux you'll see the pure-logic tests pass
(classifier, trim-plan math, protocol parsing, path policy, …) and the
handle/process-dependent ones fail with clearly OS-related errors (not
regressions). `SSDShredder.UI` targets plain `net9.0` and fully builds, runs
and tests cross-platform.

`SSDShredder.ShellExtension` is native C++ and builds separately via CMake —
see [its own README](src/SSDShredder.ShellExtension/README.md). Confirmed
clean on real Windows 11 Pro: Debug and Release, 0 errors, 0 warnings.
Installing, registering, and the Explorer/drop-target acceptance checks are
still open.

## Integration status (2026-11)

As of the last combined real-Windows run (Windows 11 Pro 26300.9974, before
the `/bzo` installer-template PR landed): **Core 73/73, Worker 235/235, UI
25/25**, solution build with 0 warnings. Since then, `main` gained 6 more
Worker tests (`ShellInstallTests`, reported 6/6 passing separately on real
Windows PowerShell) and 46 more UI tests (`WireMappingTests`, pure logic, no
Windows dependency, verified on macOS). **The full current count — 241
Worker, 71 UI — has not yet been re-run as one combined pass on Windows;**
what's stated above is each piece's own last confirmed run, not a single
fresh number.

- **`SSDShredder.Worker` → `SSDShredder.Core`: wired and confirmed.**
  `CoreFileDisposalAdapter` implements Worker's `IFileDisposalCore` over Core's
  real `TrimEngine`/`FileInspector`, via an additive decomposition of
  `HeldFile.Commit` into `RevalidateForCommit`/`TrimOnly`/`UnlinkOnly` (the
  original `Commit()` is untouched). A real `--inspect`/`--shred --confirm` on
  a disposable file correctly reports exit 7 (clean file-level-TRIM refusal) —
  see `docs/ENGINEER1_OBSERVATIONS.md`'s "Test machine 2" for the full
  readout. The *positive* path (a drive that actually accepts file-level
  TRIM) still has zero coverage; two independent machines have now refused
  it cleanly.
- **The UI's own single-instance pipe bug is fixed and confirmed.**
  `ActivationRouter.CreateServerStream` combined `PipeOptions.CurrentUserOnly`
  with an explicit `PipeSecurity`, which Windows rejects. Fixed by dropping
  the custom `PipeSecurity` entirely and relying on `CurrentUserOnly` alone.
  On real Windows: all `ActivationRouterTests` pass, and a second launch
  hands off to the first and exits in ~340ms.
- **`SSDShredder.UI` → `SSDShredder.Worker`: written, not yet verified.**
  `ProcessWorkerClient` launches `SSDShredder.Worker.exe --ipc <suffix>
  --parent-pid <ui pid>` unelevated and speaks the real protocol over a
  DACL'd pipe it creates first (mirroring Worker's own
  `PipeEndpoints.CreateServer` exactly — not the `PipeOptions.CurrentUserOnly`
  pattern that caused the ActivationRouter bug above). It's **opt-in**,
  behind `SSDSHREDDER_REAL_WORKER=1` on Windows; the default stays
  `SimulatedWorkerClient` until this is confirmed on real hardware.
  `SSDShredder.Worker.exe` is a `win-x64` binary that can't run on macOS even
  under Rosetta, so this couldn't be written *and verified* here — the pure
  wire-mapping logic (46 tests, `WireMappingTests.cs`) is fully verified; the
  pipe/process plumbing is not. **Needs real mode's peer check too:
  `SSDShredder.UI.exe` and `SSDShredder.Worker.exe` must be installed in the
  same directory** (Worker's own peer verification requires it) — on a dev
  build with separate `bin/Debug` output folders, set `SSDSHREDDER_WORKER_PATH`
  to point at the Worker executable directly.

Both simulated paths are clearly labeled as such in the UI and worker output,
so nothing in a demo or a test run can be mistaken for a real device result.
