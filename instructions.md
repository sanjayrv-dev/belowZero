# Instructions for Engineer 3 (feat/ui-and-shell)

Run these on your laptop. If you use Claude Code, tell it: "Read instructions.md and do it."

## Why
`ActivationRouter.CreateServerStream()` passed both `PipeOptions.CurrentUserOnly` and an explicit
`PipeSecurity` to `NamedPipeServerStreamAcl.Create`. On Windows that throws `ArgumentException`, so
3 `ActivationRouterTests` failed and desktop-drop activation would have broken. It never showed on
macOS because that code path only runs on Windows.

Fixed in commit `ce89ea0` on `feat/kernel-trim-core`. It touches only
`src/SSDShredder.UI/Activation/ActivationRouter.cs` and `CLAUDE.md` (a new note under the UI rules).

## Steps
1. Make sure your work is committed: `git status` should be clean.
2. `git fetch origin`
3. Merge the fix into your branch: `git merge origin/feat/kernel-trim-core`
   (or `git cherry-pick ce89ea0`). If `ActivationRouter.cs` conflicts, keep your version and apply
   the one change below by hand.
4. Run `dotnet test tests/SSDShredder.UI.Tests` from the repo root.
   - macOS/Linux: should pass. This is a sanity check only, because the Windows branch of the fix
     does not run there.
   - Windows: should pass 25/25. This is the real check. Do it before you merge UI pipe or ACL code.
5. Push your branch.

## The one change, if you have to apply it by hand
In `CreateServerStream()`, Windows branch, change `PipeOptions.CurrentUserOnly` to
`PipeOptions.Asynchronous`. Keep `pipeSecurity: CreateCurrentUserOnlyPipeSecurity()`. The explicit
DACL is what restricts the pipe to the current user.

## When you wire the real worker pipe
- Same rule: never combine `CurrentUserOnly` with `PipeSecurity` on Windows.
- Protocol is in `src/SSDShredder.Worker/README.md` ("Integration seams") and `contracts/v1/`.
- File-level TRIM is refused (Win32 error 326) on both NVMe machines tested. The worker returns
  exit 7 / `failed`, leaves the file intact and preserves the entry. Show that as an expected
  outcome, not an error. Details: `docs/ENGINEER1_OBSERVATIONS.md`.

## Delete this file
Once merged and the tests pass, delete `instructions.md`. It is a one-off.
