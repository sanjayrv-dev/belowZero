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

## Boundary cases (reviewed 2026-11, lead ruling — docs/02's prose is unchanged)

Engineer 1 raised two cases where the real `CoreFileDisposalAdapter` made a previously
theoretical mapping observable for the first time, and asked for a ruling rather than
changing either behavior unilaterally. Both are **already correct as implemented; no code
changed.**

**1. A missing file reports `4 unsupported`, not `2 invalid` or `7 failed`.**
The request schema only requires `target` to be *syntactically* an absolute local path
("File verbs take an absolute local path") — it says nothing about the path existing, so a
missing file is not an arguments/schema problem (`2`). It's also not the `0 completed`
case docs/02 calls out ("includes an inspect operation finding the target ineligible"):
that sentence covers `CapabilityClassifier.Decide` actually running and finding the file
ineligible (wrong streams, topology, etc.); a missing file never reaches that classifier at
all — there's nothing to classify. `4 unsupported` ("preflight failed") is the better fit:
whatever has to be true before eligibility can even be assessed didn't hold, same bucket as
"vault platform unavailable." This was Engineer 2's existing `FileOperations.MapAccess`
(`AccessStatus.NotFound → ExitCode.Unsupported`), already exercised by `SimulatedCore`'s
"no fixture for this path" case — `CoreFileDisposalAdapter` just makes it observable
against a real, no-longer-simulated filesystem for the first time.

**2. A relative/UNC/`file:stream` path exits `2` with no result event — but only from the
CLI's own argument parser, never from a connection.** These are two different layers and
they're correctly supposed to differ:
- `SSDShredder.Worker --inspect <bad-path>` is rejected by `CommandLine.TryParse` *before*
  any request id or `RequestEmitter` exists (`Program.Main` writes straight to stderr and
  returns exit 2). There is structurally nothing to attach a result event to — this is the
  CLI-argument-boundary case docs/02 means by "Enforce exact arity, reject unknown
  switches and use OS argument boundaries," extended to the point *before* a request
  exists, same as any OS CLI tool rejecting bad arguments with no session ever opening.
- The same bad path sent as a well-formed wire frame (`{"v":1,"id":"...","op":"inspect",
  "target":"relative\\path", ...}`) is **not** treated this way: `RequestParser.
  ValidateSemantics` already calls `Validation.IsAbsoluteLocalFilePath` and returns
  `ParseStatus.Reject`, which `ConnectionSession.Run` answers with `emit.Result(ExitCode.
  Invalid, null)` — exit 2, *with* a result event, and the connection stays open
  (`continue`, not `return`). This is docs/02's "The frame carries a trustworthy request ID
  but breaks the schema: answer with code 2," already implemented exactly as written.
  "No result event" is reserved for frames with no trustworthy id at all ("Invalid
  protocol frames with no trustworthy request ID close the connection without executing an
  operation") — a different, narrower case than a well-formed frame with a bad `target`.

If Engineer 1's test was against a connection (not the CLI) and still saw no result event,
that's a real bug worth a fresh report with the exact request sent — the two should not
behave the same way, by design.
