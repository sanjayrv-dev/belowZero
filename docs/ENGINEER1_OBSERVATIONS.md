# Engineer 1 · Observations (Core)

Observed results only. Expected behaviour lives in docs/01 and docs/02.

## Test machine 1 (developer laptop)
- Windows version: 10.0.26300.0 (build number is above 24H2/25H2 retail 26100/26200; likely a preview channel — unverified).
- Disk: Samsung BM9C1 1024 GB, NVMe, MediaType SSD (Get-PhysicalDisk). Volume C: is NTFS, 4096-byte clusters, page size 4096.
- `fsutil behavior query DisableDeleteNotify`: NTFS = 0, ReFS = 0 (TRIM allowed).
- BitLocker status: not determined (`manage-bde -status C:` needs an elevated shell).

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

## Not yet run
- Positive TRIM acknowledgement on real hardware (needs a device whose stack accepts file-level TRIM; try another NVMe or a SATA SSD).
- Unknown-bridge/USB fallback on real hardware, ACL-denied target, sparse/linked fixtures on disk.
