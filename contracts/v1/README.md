# belowZero wire contract v1

Frozen artifacts for the protocol docs/02_SYSTEM_ARCHITECTURE.md describes in prose and the
code already implements. Nothing here changes behavior — this directory exists so "what
the wire actually looks like" has one authoritative, machine-checkable location instead of
being re-derived from source every time, per the architecture tree's own
`contracts/ v1 JSON schemas, examples, exit-code map` entry.

## Files

| File | What it is | Implemented by |
| --- | --- | --- |
| `activation-envelope.schema.json` | UI activation request (`open`/`select`) | `SSDShredder.UI.Activation.ActivationEnvelope`, `SSDShredder.ShellExtension/Common/ActivationEnvelope.{h,cpp}` |
| `activation-ack.schema.json` | UI activation acknowledgement | `SSDShredder.UI.Activation.ActivationAck` |
| `worker-request.schema.json` | One request sent to the worker | `SSDShredder.Worker.Protocol.WireRequest`/`RequestParser` |
| `worker-event.schema.json` | One `progress` or `result` line the worker emits | `SSDShredder.Worker.Protocol.RequestEmitter` |
| `evidence/inspect-evidence.schema.json` | `result` evidence for `op=inspect` | `InspectEvidence` |
| `evidence/shred-evidence.schema.json` | `result` evidence for `op=shred` | `ShredEvidence` |
| `evidence/vault-evidence.schema.json` | `result` evidence for `op=vault-*` | `VaultEvidence` |
| `evidence/probe-evidence.schema.json` | `result` evidence for the private `op=probe-volume` | `ProbeEvidence` |
| `exit-codes.md` | The 9-value exit-code map | `Wire.ExitCode`/`Wire.StateName` |
| `examples/*.{json,ndjson}` | Real, schema-validated example traffic | — |

## Why `worker-event.schema.json` can't pick the evidence shape for you

A `result` event carries no `op` field — only `id`. To know which of the four evidence
schemas applies, a consumer must remember which op it sent under that request `id` (the
worker assumes the caller already knows, since it's always the caller who sent the
request). `examples/` pairs each request with its event(s) in one file for exactly this
reason — read the first line to know which evidence schema the last line should validate
against.

## How these were checked

There's no JSON Schema validator dependency in this repo (kept out deliberately — this is a
docs-only addition, not a new runtime dependency). Every file in `examples/` was checked
against its schema with a throwaway, dependency-free validator covering the subset of
JSON Schema actually used here (type/const/enum/required/properties/additionalProperties/
pattern/format/minimum/minItems/maxItems/allOf/if-then/oneOf/dependentRequired), plus a
handful of deliberately-broken documents to confirm the schemas actually reject bad input
and aren't just accepting everything. `examples/shred.ndjson` and `examples/vault-close.ndjson`
are copied verbatim from docs/02_SYSTEM_ARCHITECTURE.md's own worked examples. If you change
a schema or add an example, re-verify the same way before trusting it.
