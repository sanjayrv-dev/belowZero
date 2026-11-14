# Engineer 1 · Observations (Core)

Observed results only. Expected behaviour lives in docs/01 and docs/02.

## Test machine 1 (developer laptop)
- Windows version: 10.0.26300.0 (build number is above 24H2/25H2 retail 26100/26200; likely a preview channel — unverified).
- Disk: Samsung BM9C1 1024 GB, NVMe, MediaType SSD (Get-PhysicalDisk). Volume C: is NTFS, 4096-byte clusters, page size 4096.
- `fsutil behavior query DisableDeleteNotify`: NTFS = 0, ReFS = 0 (TRIM allowed).
- BitLocker status: not determined (`manage-bde -status C:` needs an elevated shell).

## Results (`dotnet test`, 40/40 passing, commit c171aae)
- Inspector verdict for a 12,903-byte temp file: eligible, bus=nvme, covered=12,288, tail=615 (IncursSeekPenalty=False, TrimEnabled=True, 1 disk).
- Real `FSCTL_FILE_LEVEL_TRIM` on that file (single range [0,12288)): **refused, Win32 error 326**, no ranges processed, file left in place.
  - Executor at that commit reported Indeterminate (exit 8); changed in 01cab4e to a clean Failed (exit 7) for 313/317/326.
  - Cause not yet identified. Descriptor evidence (bus, seek penalty, TrimEnabled) therefore does not predict that file-level TRIM works.

## Not yet run
- Positive TRIM acknowledgement on real hardware (needs a volume where file-level TRIM is accepted).
- Unknown-bridge/USB fallback, ACL-denied target, sparse/linked fixtures on disk, sizing experiment (resident → external transition).
