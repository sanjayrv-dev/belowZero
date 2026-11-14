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
├── contracts/                        reserved for v1 JSON schemas/examples (not yet populated)
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
Two integration seams are intentionally left for a Windows-equipped follow-up
rather than wired blind:

- **`SSDShredder.Worker` → `SSDShredder.Core`.** The worker currently runs
  against `Simulation.SimulatedCore`; wiring a real `IFileDisposalCore`
  adapter over Core's `TrimEngine`/`FileInspector` needs a Windows machine to
  verify against the fault-injection tests both projects already have.
- **`SSDShredder.UI` → `SSDShredder.Worker`.** The UI currently runs against
  `SimulatedWorkerClient`. `SSDShredder.Worker.exe` is a `win-x64` binary and
  can't be executed on macOS (not even under Rosetta — it's a Windows PE, not
  an x86-64 Mach-O), so a real process-launching client couldn't be verified
  here either.

Both simulated paths are clearly labeled as such in the UI and worker output,
so nothing in a demo or a test run can be mistaken for a real device result.
