# Engineer 1 · Observations (Core)

Observed results only. Expected behaviour lives in docs/01 and docs/02.

## Test machine 1 (developer laptop)
- Windows version: 10.0.26300.0 (build number is above 24H2/25H2 retail 26100/26200; likely a preview channel — unverified).
- Disk: Samsung BM9C1 1024 GB, NVMe, MediaType SSD (Get-PhysicalDisk). Volume C: is NTFS, 4096-byte clusters, page size 4096.
- `fsutil behavior query DisableDeleteNotify`: NTFS = 0, ReFS = 0 (TRIM allowed).
- BitLocker: `manage-bde -status C:` (elevated) shows C: fully decrypted, protection off, no key protectors, unlocked. So the 326 refusal is not caused by BitLocker on this machine.

## Results (`dotnet test`, 40/40 passing at commit c171aae; 62/62 at 3d91b47)
- Inspector verdict for a 12,903-byte temp file: eligible, bus=nvme, covered=12,288, tail=615 (IncursSeekPenalty=False, TrimEnabled=True, 1 disk).
- Real `FSCTL_FILE_LEVEL_TRIM` on that file (single range [0,12288)): **refused, Win32 error 326**, no ranges processed, file left in place.
  - Executor at that commit reported Indeterminate (exit 8); changed in 01cab4e to a clean Failed (exit 7) for 313/317/326.
  - Cause: Windows reports 326 as "Device does not support file-level TRIM" (see diagnostic below). Descriptor evidence (bus, seek penalty, TrimEnabled) therefore does not predict that file-level TRIM works.

## 326 diagnostic (TrimDiagnosticTests, this machine, %TEMP% on C:)
- 1 page, 3 pages, 1 MiB whole file; exclusive or shared read/write; with or without flush: all **326**, nothing returned, no ranges processed.
- Write-data-only access: 1224 (user-mapped section open), an access-rights artefact, not a TRIM verdict.
- Conclusion: the drive's driver stack refuses file-level TRIM regardless of request shape. NTFS and ReFS `DisableDeleteNotify` are 0, so this is specific to file-level TRIM. The machine has one physical disk, so D: cannot serve as a second device.

## Sizing experiment (SizingExperimentTests, 4096-byte cluster and page, NTFS)
- 1-600 bytes: resident (NoExtents). 700-4095: external extents but no full page (NoFullPage, tail only).
- 4096 and up: eligible (4097 -> 4096 covered + 1 tail; 12,903 -> 12,288 + 615).
- Resident to non-resident transition lies between 600 and 700 bytes; it never reverts when the file grows.

## Also covered by tests now
- Classifier edge cases (sparse, compressed, EFS, named stream, reparse, non-NTFS, unknown bus, null/multi-disk topology, rotational, TRIM disabled, failed/unsupported query, short coverage, empty, page-aligned) and WorkerFixtures consistency.

## Test machine 2
- Windows 10.0.26200 (Windows 11 Pro; `Get-ComputerInfo` reports "Windows 10 Pro"). One physical disk: PC SN5000S WD 1024GB, NVMe, SSD, 1,024,209,543,168 bytes. C: (NTFS, 1 TB) plus three unlettered recovery/support partitions. %TEMP% is on C: (disk 0). NTFS and ReFS `DisableDeleteNotify` = 0. BitLocker status not collected (shell not elevated). No second internal volume, so the second-volume diagnostic was skipped.
- `dotnet test`: 62/62 passing at 95daad7 (.NET SDK 9.0.321).
- **326 diagnostic:** 1 page, 3 pages (flush and no flush, exclusive and shared rw) and 1 MiB whole file all returned `win32=326 returned=0 processed=null`. No `win32=0`, so still no positive acknowledgement. Write-data-only access gave 326 in the full run but 1224 in a second run (user-mapped section, access artefact).
- Real submitter: `Eligible` (bus=nvme, covered=12288, tail=615), then `State = Failed ... trim refused by the stack (326); file left intact`, entry preserved.
- Sizing: len=600 already reported `NoFullPage` (non-resident, first non-resident length observed: 600), versus resident up to 600 on machine 1. 4096 and up eligible, same as machine 1. The resident threshold varies by machine.
- Conclusion: a second NVMe model (WD SN5000S) on a similar Windows build also refuses file-level TRIM with 326, matching machine 1 (Samsung BM9C1, 10.0.26300).

## Cross-machine summary and decision
- Two NVMe models (Samsung BM9C1, WD SN5000S) on Windows 10.0.26300 and 10.0.26200 both return 326 ("Device does not support file-level TRIM") for every request shape tried. The request layout matches docs/01; a malformed request would be expected to give 87, not 326.
- Decision (v1): **clean refusal.** When file-level TRIM is refused (313/317/326) the executor reports Failed (exit 7), leaves the file and entry in place, and nothing is unlinked. Workers and UI must treat this as an expected result on the two NVMe machines tested, not an error condition. `WorkerFixtures.Unsupported` and `Partial` model the refusal and partial cases.
- Open: whether any local stack accepts file-level TRIM. Update: a thin-provisioned VHDX does (see below); SATA SSD and Storage Spaces remain unverified.

## Refusal paths on disk (machine 1, real Worker CLI, throwaway files in %TEMP%, 12,903 B unless noted)
| Case | `--inspect` | `--shred --confirm` |
|---|---|---|
| baseline | eligible, BusTypeNvme, 12288 + 615 | exit 7 failed (326), entry preserved |
| sparse | streams | exit 4 unsupported, file kept |
| hard link | links | exit 4 unsupported, file kept |
| named stream | streams | exit 4 unsupported, file kept |
| compressed | streams | not run |
| 100 B / empty | noExtents (resident here) | exit 4 unsupported, file kept (100 B) |
| ACL deny write/delete | exit 5 denied | exit 5 denied, file kept |
| exclusively opened | exit 6 busy | exit 6 busy, file kept |
| missing file | exit 4 unsupported | not run |
| relative, UNC, `file:stream` path | exit 2, no result line | not run |
- Not run: symlink (needs privilege) and USB/unknown bridge (no USB drive on this machine).
- Possible contract questions for the lead: a missing file reports exit 4 (unsupported); path-policy rejections exit 2 without a result event.

## Thin-provisioned VHDX (machine 1, 2026-11-14)
- Throwaway 256 MB expandable VHDX (`diskpart`), NTFS, 4096-byte cluster. `Get-Disk`: BusType `File Backed Virtual`, ProvisioningType `Thin`. NTFS `DisableDeleteNotify` = 0.
- **Raw request acknowledged (not proof of erasure).** `TrimDiagnosticTests` run unelevated with `BELOWZERO_DIAG_DIR=Z:\`: all six request shapes (1 page, 3 pages with/without flush, shared rw, 1 MiB whole file, write-data-only access) returned `win32=0 returned=4 processed=1`. The same request shape gives 326 on both NVMe disks, so the request layout is not the cause of those refusals.
- Scope of the claim: the stack *accepts* the request on a virtual disk. This does not show that blocks were deallocated or anything about a physical SSD.
- The real Worker cannot exercise it: `--inspect` on that volume reports `eligible:false, reason:topology, bus:null` (the classifier does not recognise a file-backed virtual disk), and `--shred` refuses to run from an elevated prompt (exit 5). The VHDX had to be attached elevated and the diagnostic run unelevated; the volume was visible to the unelevated session.
- Cleaned up afterwards (VHDX detached and deleted).
- Still open: a SATA SSD or Storage Spaces; whether the classifier should ever accept virtual-disk topology (not changed).

## Not yet run
- Positive TRIM acknowledgement on a physical device (only a thin VHDX has acknowledged the raw request, and that does not prove erasure; try a SATA SSD).
- Unknown-bridge/USB fallback on real hardware, symlink fixture (needs privilege).

## Test machine 2 (first Windows run of the full integrated repo, main @ d1c4b61)
- Windows 11 Pro 26300.9974, .NET SDK 9.0.321.
- `dotnet test`: Core 73/73, Worker 204/204 (both suites' full counts, for the first time on real hardware).
- Real `--inspect` on a 12,903-byte file via the newly wired `CoreFileDisposalAdapter`: eligible, `BusTypeNvme`, 12,288 covered, 615 tail — matches test machine 1's own inspector verdict on an identically-sized file exactly.
- Real `--shred --confirm` on this machine's drive: **exit 7, `rangesProcessed: 0`, entry preserved** — the same clean file-level-TRIM refusal as test machine 1's error 326, on a second, independent machine. File untouched.
- **New fact this machine adds: `C:` reports BitLocker protection ON.** Test machine 1 above left BitLocker "not determined." Two real machines with file-level TRIM refused is itself a data point: the positive (TRIM actually acknowledged) path still has zero real coverage and needs a third, different drive/controller.
- Other real-Core cases exercised, all leaving the target file untouched: a 300-byte file reports `noExtents` (exit 4); a file held open elsewhere reports exit 6 (busy); a non-interactive shred without `--confirm` reports exit 2.
- Real UAC accept case: `--vault-status` reached the elevated helper and returned a correct answer (run from the `vhdx-vault` build, before its latest 3 commits were merged into `main`).

### Re-run at main @ 8207fef (after merging vhdx-vault's 3 commits and fixing the UI's ActivationRouter pipe bug)
- `dotnet test`: Core 73/73, Worker 235/235, UI 25/25 — full green, 0 warnings on the solution build. The 14 new Worker tests that only exercise Windows-only code (added by the merged commits) all pass here; the 3 `ActivationRouterTests` that caught the pipe bug now pass too.
- UI standalone, live: a second launch hands off to the first and exits 0 in ~340ms; one instance stays running. Confirms the `PipeOptions.CurrentUserOnly` + explicit `PipeSecurity` fix.
- `SSDShredder.ShellExtension`: clean rebuild, Debug and Release, 0 errors and 0 warnings (the C4244 from PR #1 is gone).
