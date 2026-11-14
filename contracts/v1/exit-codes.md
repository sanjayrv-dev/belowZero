# Worker exit codes / result codes v1

Source of truth: `SSDShredder.Worker.Protocol.Wire.ExitCode` and `Wire.StateName`, matching
docs/02_SYSTEM_ARCHITECTURE.md's "Worker contract v1" section exactly. The process exit code
and the terminal result event's `code`/`state` are the same value, just as an OS exit code
and as the wire's int/string pair.

| code | state           | Meaning |
| ---- | --------------- | ------- |
| 0    | `completed`     | Includes an inspect operation finding the target ineligible — "completed" describes the *operation*, not that a file was removed. Check `evidence.entry` for a shred. |
| 2    | `invalid`       | Arguments/schema problem. |
| 3    | `cancelled`     | User declined before mutation, including OS error 1223 (UAC decline) synthesized by the launcher. |
| 4    | `unsupported`   | Preflight failed (e.g. ineligible file, vault platform unavailable). |
| 5    | `denied`        | Authority/identity failed (wrong user, identity changed since selection, token mismatch/expiry). |
| 6    | `busy`          | Exclusive access was unavailable before mutation. Nothing was touched. |
| 7    | `failed`        | Verified pre-mutation failure (e.g. trim cleanly refused by the storage stack; file untouched). |
| 8    | `indeterminate` | File mutation is possible: partial trim, failed unlink, or a lost completion after a confirmed request was sent. The entry is left in place when feasible and retries are suppressed. |
| 9    | `recovery`      | An initiated vault transition lacks a verified terminal state; query the journal. A repeated close of an already-retired vault returns its durable receipt under the new request id. |

## Never replay a confirmed file operation on transport loss

If a confirmed `shred` or `vault-close` request's result event never arrives (connection
lost, process killed), the worker-side caller must not resend the same mutation — see `8
indeterminate` / `9 recovery` above. This is a hard safety rule, not just a style note.
