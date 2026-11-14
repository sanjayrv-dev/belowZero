# SSDShredder.Worker (Engineer 2)

Command line, wire protocol, local IPC, and the vault lifecycle. Built on Windows 11 Home
against simulated Core and simulated virtual-disk/BitLocker providers. The real vault
provider is not written yet: it needs a disposable Windows 11 Pro, Enterprise or Education
install.

## Build and test

```
dotnet test tests/SSDShredder.Worker.Tests
```

There is no solution file here. The lead owns `SSDShredder.sln` and adds both projects.

## Try it (simulated)

Simulation is switched on only by an environment variable. It never opens, changes or
deletes a file, disk or BitLocker setting. It keeps its own state under
`%LOCALAPPDATA%\SSDShredder\sim` (or `SSDSHREDDER_SIM_ROOT`).

```
set SSDSHREDDER_SIMULATE=1
SSDShredder.Worker --inspect C:\path\file.bin
SSDShredder.Worker --shred C:\path\file.bin --confirm
SSDShredder.Worker --vault-init
SSDShredder.Worker --vault-status <vault-uuid>
SSDShredder.Worker --vault-close <vault-uuid> --confirm
```

| Variable | Effect |
| --- | --- |
| `SSDSHREDDER_SIM_SCENARIO` | File result: `eligible`, `usb`, `partial`, `busy`, `denied` |
| `SSDSHREDDER_SIM_FAULTS` | Vault faults, e.g. `quiesce:Busy,lock:RecoveryKeyRequired,protect:PolicyRefused`. Set it on `--vault-init`: the owner process inherits it |
| `SSDSHREDDER_SIM_OPTIN=1` | Recovery may retire a verified orphan |
| `SSDSHREDDER_SIM_DETACH_ROUTE=1` | Allow the detach/reattach close route |

Without the variable the build is in real mode: file verbs and vault verbs both end with
exit 4, and nothing is touched.

## Layout

| Folder | What it holds |
| --- | --- |
| `Cli/` | Exact-arity parser for the published grammar |
| `Protocol/` | Exit codes, strict request parser, frame reader, event emitter, evidence shapes |
| `CoreAdapter/` | The Worker's view of Core: the seam Engineer 1's Core plugs into |
| `Operations/` | Inspect/shred flow, 60-second single-use selection token, per-connection loop |
| `Vault/` | Journal, state machine, platform interface, placeholder real provider |
| `Ipc/` | Restricted pipe, peer checks, helper launcher (`ShellExecuteExW`) |
| `Hosting/` | CLI host, `--ipc` helper mode, session owner endpoint |
| `Simulation/` | Simulated Core and vault platform with fault injection |

## Integration seams

- **Core (Engineer 1).** Implement `CoreAdapter/IFileDisposalCore` over the real Core and
  return it from `WorkerEnvironment.Create()` with `CoreAvailable = true`. The Worker
  decides the exit code. Core reports facts: eligibility, trim status, ranges, entry outcome.
- **UI (Engineer 3).** For file disposal, create a pipe named `SSDShredder.v1.<32 hex>`,
  start `SSDShredder.Worker --ipc <32 hex> --parent-pid <ui pid>` unelevated, send `inspect`,
  then one `shred` with the returned `selectionToken` on the same connection. The pipe name
  is the same in simulated and real mode. The worker checks that the pipe server is its
  parent PID, the same user and the same session. In real mode it also requires the server's
  executable to sit in the worker's own folder, so the UI and the worker must be installed
  side by side; a simulated run skips that one rule. A refused connection exits 5 with the
  reason on stderr and no events. `EndToEndTests.DriveAsUi` is a working example of the
  server side. For vaults, run the published `--vault-*` commands and read stdout.

## Questions for the lead (contract)

1. The published shred example shows `trimming` then the result. This Worker also emits
   `unlinking` (it is in the phase list) before the result.
2. Orphan-retirement opt-in has no switch in the grammar. It defaults to off in real mode.
3. Request IDs and vault IDs on the wire must be lowercase canonical UUIDs. The CLI accepts
   any case and lowercases it.
4. Repeated request ID on a connection: the connection is closed without a reply.
5. A token mismatch or expiry answers code 5 and releases the held file.
6. Simulated receipts are indistinguishable on the wire from real ones (`"entry":"removed"`,
   `"conversion":"fullyEncrypted"`). The only marker is a stderr line, which a UI using
   `--ipc` does not see. The demo must never run with `SSDSHREDDER_SIMULATE=1`. Decide
   whether release builds should ignore the variable.
7. The contract gives the UI no field for handing `probe-volume` evidence to the ordinary
   worker that holds the file. The Worker answers `probe-volume` but the file flow does not
   consume it yet.
8. "Trusted installed image" is implemented as "same folder as the worker executable".
9. This branch adds a root `.gitignore` (bin, obj, .vs, TestResults). It is a shared file:
   keep, replace or drop it.

## Not done, needs the Pro machine

- `Vault/WindowsVaultPlatform.cs`: real VHDX create/attach, GPT/NTFS, BitLocker WMI, mount,
  lock, detach, identity checks. Every method currently returns "unsupported".
- A real UAC consent run (`runas`), including decline (1223) and a different-account prompt.
  An unelevated client reading an elevated owner's token user has not been exercised.
- Admin-only ACL on the real journal folder under `%ProgramData%\SSDShredder`.
- Qualifying `Lock(false)` versus the detach route on the target build and policy.
- The session owner's lifetime (8 hours) ends by exiting. Decide what a real expiry should do.
