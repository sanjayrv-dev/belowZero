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
  reason on stderr and no events. `EndToEndTests.DriveAsUi` (in `IpcTests.cs`) is a working
  example of the server side. Inspecting again on the same connection, the same file
  included, replaces the held selection and its token. For vaults, run the published `--vault-*` commands and read stdout.

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
10. `--vault-status` answers from the journal alone. If the owner process died, a vault
    still recorded as `ready` is reported as `ready` with its mount path and encryption
    facts, although nobody holds it. Should status observe the platform and report
    `orphan` or `unknown` there? It changes nothing today until a close reconciles it.
11. Once the owner has exited, a real-mode `--vault-status` or repeated `--vault-close`
    needs an elevated helper, so the user gets a UAC prompt just to read a receipt. The
    journal is meant to be administrator-only. Accept the prompt, or keep a user-readable
    receipt?
12. Nothing checks that the worker's own folder is administrator-only before it asks for
    elevation of its own executable. Should the Worker refuse to elevate from a
    user-writable folder, or is that the installer's guarantee alone?
13. `--inspect` runs when elevated; only `--shred` is refused. It then classifies files
    the ordinary user might not be able to open. Refuse it too?
14. A command line that does not parse exits 2 with usage on stderr and no result event on
    stdout. The contract says the CLI writes its events to stdout. Emit a result there?
15. A vault verb sent to an unelevated helper is answered with code 2 (invalid). Code 5
    (denied) may describe it better.

## Requirements on the real provider

The state machine relies on these. The simulator already behaves this way, so the tests
cannot catch a real provider that does not.

- `Quiesce` and `Unmount` are re-entrant. A resumed close calls both again on a volume
  that is still attached, already quiesced and no longer mounted, and both must answer Ok.
- `Observe` throws, or reports unknown facts as null, when it cannot look. Recovery retires
  a record that shows no backing file and no mount, so "could not look" must never read as
  "nothing is left". The placeholder throws.
- Disposing an attachment twice is harmless.

## Not done, needs the Pro machine

- `Vault/WindowsVaultPlatform.cs`: real VHDX create/attach, GPT/NTFS, BitLocker WMI, mount,
  lock, detach, identity checks. Every method currently returns "unsupported".
- A real UAC consent run (`runas`), including decline (1223) and a different-account prompt.
  An unelevated client reading an elevated owner's token user has not been exercised end
  to end. A read-only check on Windows 11 Home 26200 (one admin account, split token)
  found that the limited and the elevated token carry the same logon SID, and that a
  limited process can read the user of a fully elevated process of the same account. So
  the pipe ACL and the peer check are expected to work across elevation, but only the
  real `runas` run proves it.
- Admin-only ACL on the real journal folder under `%ProgramData%\SSDShredder`.
- Qualifying `Lock(false)` versus the detach route on the target build and policy.
- The session owner's lifetime (8 hours) ends by exiting. Decide what a real expiry should do.
