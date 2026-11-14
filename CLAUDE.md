# belowZero / SSD Shredder — conventions

- Specs: docs/01 (Windows storage), docs/02 (architecture + wire contract v1), docs/03 (build plan).
- Branch: all work happens on `feat/kernel-trim-core`. Never push to `main` unless explicitly told.
- Engineer 1 scope: `src/SSDShredder.Core` and `tests/SSDShredder.Core.Tests` only.
- Core rules: checked arithmetic, validate SDK struct sizes and driver counts, capture
  `Marshal.GetLastPInvokeError()` immediately, never write physical sectors or NTFS metadata,
  unlink only after all planned trim ranges succeed.
- Build/test: `dotnet build` / `dotnet test` (Windows needed to actually run; Linux/macOS can build
  via EnableWindowsTargeting). Windows-only integration tests need a disposable NTFS volume.
