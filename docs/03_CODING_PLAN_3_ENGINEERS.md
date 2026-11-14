# Parallel build plan for the four-person team

Treat the time boxes as a suggested eight-hour build. The pitch lead owns the shared baseline and integration. The three engineers each use their own checkout or Git worktree, branch and Claude Code session. Use only a disposable, owner-authorized test volume for Windows integration.

## Hour 0–1 · Freeze the interfaces

1. The lead lands one baseline commit containing the solution, empty project and test scaffolds, build settings, packaging placeholders and these five specifications. Record the commit ID. All branches start there. Assign changes to the solution, shared settings and cross-project contracts to the lead throughout the event.
2. Read [the architecture](02_SYSTEM_ARCHITECTURE.md) together. Freeze command spelling, protocol version, event fields, exit codes, the vault session lifetime and a successful and failed transcript. Bind contract changes to a reviewed change in that document and update all three consumers together.
3. Allocate a physical Windows 11 NTFS test SSD to Engineer 1 and a disposable BitLocker-capable Windows installation to Engineer 2. Engineer 3 can develop Avalonia on macOS and needs a Windows machine for Explorer and UAC integration. A VM validates Windows behavior within its virtual storage layer.

## Hours 1–5 · Build three independent slices

### Engineer 1 · `feat/kernel-trim-core`

Own `SSDShredder.Core` and its tests. First deliver a read-only capability decision showing which observation made a file eligible or ineligible. Then deliver the single-handle trim operation through the frozen interface. Decode retrieval pointers defensively, including continuation buffers. Treat their VCN/LCN pairs as allocation diagnostics. File-level TRIM takes logical file byte offsets.

Demonstrate a page-aligned file, one with a trailing partial page, a sub-page stream, indeterminate allocation, sparse/linked inputs, an unknown bridge and a short output buffer. Report eligible bytes and trailing bytes separately. Unlink only after all planned ranges succeed. Inject a partial completion and verify that the original name remains. In a separate sizing experiment, grow a synthetic scratch file, flush and re-query its extents. Record the observed transition and NTFS geometry. Resident streams and files with no complete pages stay ineligible.

**Claude Code prompt**

```text
Work on feat/kernel-trim-core. Read CLAUDE.md and docs/01_DEEP_RESEARCH_AND_SPECS.md and docs/02_SYSTEM_ARCHITECTURE.md. Change only Core and its tests. Implement read-only classification first, then handle-bound execution for eligible local NTFS files. Check bus, capability, coverage, topology, identity and eligibility again before submission. Validate SDK struct sizes, checked arithmetic, driver counts and native errors. Use a disposable fixture for padding observations. Never write to physical-sector addresses or NTFS metadata. Supply the worker team a fixture for eligible, unsupported and partial results. Report the Windows build, device class and tests actually run.
```

### Engineer 2 · `feat/worker-vhdx-vault`

Own `SSDShredder.Worker` and its tests. Deliver the CLI and simulated Core adapter first. Bind elevated sessions to the initiating user and the confirmed object. Check the user's effective rights independently of the elevated token. Preserve the distinction between target authorization, confirmation of data loss and Windows' elevation consent.

Next provision a private, letterless NTFS data volume in a fresh VHDX. The worker owning the session retains its attachment handle. Gate access on verified protection, encryption, protector inventory and policy. Qualify passphrase-only locking with an empty disposable workspace. Exercise open-file lock refusal, worker termination during provisioning, two simultaneous close requests, stale mount identity and interrupted disposal. Cleanup uses the owned journal and verified identities. Keep mismatches pending for review.

**Claude Code prompt**

```text
Work on feat/worker-vhdx-vault. Read CLAUDE.md and docs/01_DEEP_RESEARCH_AND_SPECS.md and docs/02_SYSTEM_ARCHITECTURE.md. Own Worker and worker tests. Implement --inspect, --shred, --vault-init, --vault-status and --vault-close using the published grammar, events and exit codes. Implement JIT elevation and authenticated local IPC. Preserve the initiating user's rights. Enforce new-image ownership before formatting and publish a folder only after BitLocker readiness. Keep the session alive, verify lock and detach, and reconcile interrupted transitions idempotently. Never persist the session passphrase or change BitLocker policy. Exercise policy refusal, UAC refusal, failed lock and interrupted cleanup.
```

### Engineer 3 · `feat/ui-and-shell`

Own `SSDShredder.UI`, `SSDShredder.ShellExtension`, their tests and shell-specific package declarations. Deliver radial actions, keyboard/focus navigation and progress states with a fake worker before connecting IPC. Explorer invocation hands one selected regular file to the unelevated UI and returns promptly. Run the installed packaged command on Windows. Verify same-file selection, cancellation and clean package removal.

**Claude Code prompt**

```text
Work on feat/ui-and-shell. Read CLAUDE.md and docs/02_SYSTEM_ARCHITECTURE.md. Change only UI, ShellExtension, their tests and assigned shell declarations. Implement the Avalonia drop-zone. Show the target, planned trim coverage, uncovered tail and whole-file unlink in the confirmation and receipt. Offer a new vault for future work when direct trim is ineligible. Preserve the existing source. Build and exercise fake success, denied, partial and disconnected states on macOS. On Windows validate IExplorerCommand registration, quick handoff, UAC cancellation and terminal status. Label mock observations in your handoff.
```

## Hours 5–8 · Integrate, rehearse and freeze

- **Hour 5.** The lead merges Core, Worker, then UI/Shell. Each engineer updates their branch against the shared baseline before integration and reruns the agreed transcripts. Compare direct CLI and Explorer results for the same disposable fixtures.
- **Hour 6.** Run the physical SSD positive case and unknown-bridge fallback. Include an ACL-denied target, a canceled confirmation, a non-page-aligned file, a busy vault and a crash with an orphaned image. Verify that fallback leaves the source intact. Record expected and observed outcomes separately.
- **Hours 7–8.** The pitch lead follows [the demo playbook](04_PITCH_DECK_AND_DEMO_PLAYBOOK.md), captures actual build and hardware identifiers, checks claims against the observations and prepares a labeled recording for hardware failure. Freeze the demonstrated commit. Each engineer records outstanding failures and untested environments before handoff.
