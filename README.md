# belowZero

A persistent Windows desktop "bin" and the terminal command `/bzo` for securely
disposing of a file via NTFS file-level TRIM (or routing it into an encrypted
vault when that isn't possible). Built for a hackathon by three engineers in
parallel; see [docs/](docs/) for the full research, architecture, wire
contract, and build plan.

## Layout

```text
SSDShredder.sln
├── src/
│   ├── SSDShredder.Core/             net9.0-windows · file handles, topology, extents, trim (Engineer 1)
│   ├── SSDShredder.Worker/           net9.0-windows · bzo.exe CLI, wire protocol, vault lifecycle (Engineer 2)
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
see [its own README](src/SSDShredder.ShellExtension/README.md), including an
explicit note that it hasn't been built or run yet (written without a
Windows machine available).

## Integration status

All three engineers' work is merged into `main` and builds as one solution.

- **`SSDShredder.Worker` → `SSDShredder.Core`: wired, not yet verified on real hardware.**
  `CoreFileDisposalAdapter` implements Worker's `IFileDisposalCore` over Core's
  real `TrimEngine`/`FileInspector` (via an additive decomposition of
  `HeldFile.Commit` into `RevalidateForCommit`/`TrimOnly`/`UnlinkOnly` — the
  original, already hardware-verified `Commit()` is untouched). `WorkerEnvironment.Create()`
  now uses it by default in real (non-simulated) mode. This was written and
  reviewed without a Windows machine: both projects' existing test counts are
  unchanged, the new tests for the decomposed methods fail the same
  environment-only way the existing Windows-only ones do, and the adapter's
  own pure mapping logic has 25 passing tests. **Run both test suites on real
  Windows, then a live GUI inspect-then-shred pass on a disposable file,
  before trusting this with real data.**
- **`SSDShredder.UI` → `SSDShredder.Worker`: still simulated.** The UI runs
  against `SimulatedWorkerClient`. The real protocol is precise and
  fully specified (`src/SSDShredder.Worker/README.md`'s "Integration seams"
  section, and now `contracts/v1/`): the UI must create a DACL'd named pipe
  `SSDShredder.v1.<32 hex>`, launch `SSDShredder.Worker.exe --ipc <32 hex>
  --parent-pid <ui pid>` unelevated, and send `inspect` then one `shred` with
  the returned `selectionToken` on that same connection. `SSDShredder.Worker.exe`
  is a `win-x64` binary and can't run on macOS at all (not even under
  Rosetta — it's a Windows PE, not an x86-64 Mach-O), so this couldn't be
  written *and verified* here; it's the one remaining integration seam.

Both simulated paths are clearly labeled as such in the UI and worker output,
so nothing in a demo or a test run can be mistaken for a real device result.
