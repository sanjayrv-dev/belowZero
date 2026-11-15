# `/bzo` shell integration templates

Owned by Worker; consumed by the ShellExtension installer (docs/02 "/bzo command and shell integration").

| File | Purpose |
| --- | --- |
| `Install-BzoShell.ps1` | Install / Uninstall / Status for the PowerShell alias and the cmd DOSKEY macro |
| `bzo.doskey.template` | The macro line, `/bzo="{{BZO_EXE}}" $*`; the script renders it |

The Worker build copies this folder next to the Worker output (and to publish output). Install it to
`<install dir>\Install\`, beside `bzo.exe`.

## Installer contract

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<install>\Install\Install-BzoShell.ps1" ^
    -Action Install -ExePath "<install>\bzo.exe"
... -Action Uninstall
... -Action Status
```

Run it as the interactive user, unelevated: the targets are per-user.

| Exit | Meaning |
| --- | --- |
| 0 | Done |
| 1 | Error (bad `-ExePath`, unwritable file, a path the target encoding cannot hold) |
| 3 | Finished, but at least one target was skipped: another `/bzo` definition, a hand-edited block, or a macro file belowZero does not own. Surface the warning text; do not retry blindly |

## What it touches

- **Windows PowerShell 5.1** `<Documents>\WindowsPowerShell\profile.ps1` and **PowerShell 7**
  `<Documents>\PowerShell\profile.ps1` (`$PROFILE.CurrentUserAllHosts`), resolved independently from the
  shell-folder `Documents` (so OneDrive-redirected Documents works). Both are written whether or not
  PowerShell 7 is installed yet.
- A block fenced by `# >>> belowZero /bzo alias ... >>>` / `# <<< belowZero /bzo alias <<<` holding
  `Set-Alias -Name '/bzo' -Value '<ExePath>' -Scope Global`. Content outside the block is never rewritten.
  Re-running Install replaces the block (so a moved install path is repaired).
- `%LOCALAPPDATA%\belowZero\bzo.doskey`, written in the console's OEM code page (what doskey reads).
- Uninstall removes only the block and the macro file, and only if they still match what Install writes.
  A profile left empty after removal is deleted.

## Wiring the cmd macro

Install prints the command; run it in an interactive cmd session, a startup script, or a dedicated Windows
Terminal cmd profile's command line:

```
doskey /exename=cmd.exe /macrofile="%LOCALAPPDATA%\belowZero\bzo.doskey"
```

Macros work only at the start of an interactive prompt. Batch files call `bzo.exe` directly. The script does
not set cmd's global `AutoRun`; that is an installer decision.

## Known limits

- A profile with a non-ASCII install path is written as UTF-8 with BOM when it was BOM-less ASCII; an existing
  ANSI profile with non-ASCII content stays ANSI, and the script refuses (exit 1) if the path cannot be stored in it.
- The script cannot see other profiles (AllUsers, host-specific) that define `/bzo`; it warns only about the
  `/bzo` alias visible in its own session.
- If the execution policy is `Restricted` or `AllSigned` an unsigned profile never loads. Install warns, since
  it cannot change policy; `RemoteSigned` for the current user is enough.
- `-ProfilePath a|b` / `-MacroFile` exist for tests; the installer should omit them.
- Windows PowerShell 5.1 only was exercised so far; the PowerShell 7 profile path was resolved and written but
  not loaded by a real `pwsh`.
