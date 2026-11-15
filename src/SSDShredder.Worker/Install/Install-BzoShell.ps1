<#
.SYNOPSIS
  Installs, removes or reports the belowZero-owned `/bzo` shell entries.

.DESCRIPTION
  PowerShell: a marked block in each edition's $PROFILE.CurrentUserAllHosts (Windows PowerShell 5.1 and
  PowerShell 7, resolved independently) containing
      Set-Alias -Name '/bzo' -Value '<ExePath>' -Scope Global
  Command Prompt: a DOSKEY macro file rendered from bzo.doskey.template. Wiring it into cmd is
  `doskey /exename=cmd.exe /macrofile="<MacroFile>"` (printed on install; see README.md).

  Only the marked block / the macro file this script wrote is ever changed or removed. Existing profile
  content is preserved byte-for-byte outside the block. A target that already defines `/bzo` elsewhere is
  reported as a conflict and left alone.

  Exit codes: 0 ok, 1 error, 3 at least one target skipped because of a conflict or a modified block.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File Install-BzoShell.ps1 -Action Install -ExePath 'C:\Program Files\belowZero\bzo.exe'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Install', 'Uninstall', 'Status')][string]$Action,
    # Absolute path of the installed bzo.exe. Required for Install.
    [string]$ExePath,
    # Override the profile files, '|'-separated (default: both editions' CurrentUserAllHosts). Used by tests;
    # '|' because powershell -File cannot pass an array and '|' is illegal in Windows paths.
    [string]$ProfilePath,
    # Override the DOSKEY macro file (default: %LOCALAPPDATA%\belowZero\bzo.doskey).
    [string]$MacroFile
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$BeginMarker = '# >>> belowZero /bzo alias (managed by belowZero; do not edit) >>>'
$EndMarker = '# <<< belowZero /bzo alias <<<'
$AliasLinePattern = "^Set-Alias -Name '/bzo' -Value '(?:[^']|'')*' -Scope Global$"
# Any other definition of /bzo in a profile: alias cmdlets, function, or Alias: drive. Comments are skipped.
$ConflictPattern = '(?i)^\s*(?!#).*?\b(set-alias|new-alias|sal|nal|function|alias:)\b.*(?<![\w/])/bzo(?!\w)'
$MacroLinePattern = '^/bzo="[^"]*" \$\*$'

$conflicts = 0
$failures = 0

function Get-AnsiEncoding {
    try { [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance) } catch { }
    [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage)
}

function Get-OemEncoding {
    try { [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance) } catch { }
    [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
}

# Detect how an existing profile is encoded so it can be rewritten unchanged. New files get UTF-8 with BOM,
# which both PowerShell editions read correctly.
function Read-TextFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @{ Exists = $false; Text = ''; Encoding = (New-Object Text.UTF8Encoding($true)); AsciiOnly = $false }
    }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $enc = $null; $skip = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $enc = New-Object Text.UTF8Encoding($true); $skip = 3
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $enc = New-Object Text.UnicodeEncoding($false, $true); $skip = 2
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $enc = New-Object Text.UnicodeEncoding($true, $true); $skip = 2
    } else {
        $enc = Get-AnsiEncoding
        if (@($bytes | Where-Object { $_ -ge 0x80 }).Count -gt 0) {
            try {
                $null = (New-Object Text.UTF8Encoding($false, $true)).GetString($bytes)
                $enc = New-Object Text.UTF8Encoding($false)
            } catch { }
        }
    }
    $ascii = @($bytes | Where-Object { $_ -ge 0x80 }).Count -eq 0
    @{ Exists = $true; Text = $enc.GetString($bytes, $skip, $bytes.Length - $skip); Encoding = $enc; AsciiOnly = ($skip -eq 0 -and $ascii) }
}

function Write-TextFile([string]$Path, [string]$Text, [Text.Encoding]$Encoding) {
    if ($Encoding.GetString($Encoding.GetBytes($Text)) -cne $Text) {
        throw "The path contains characters that cannot be stored in this file's encoding; use a UTF-8-with-BOM profile or an ASCII install path."
    }
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force }
    [IO.File]::WriteAllBytes($Path, [byte[]]($Encoding.GetPreamble() + $Encoding.GetBytes($Text)))
}

# Splits the profile into lines around our block. Returns the block's line range and whether it is intact.
function Find-Block([string[]]$Lines) {
    $begin = [Array]::IndexOf($Lines, $BeginMarker)
    if ($begin -lt 0) { return $null }
    $end = -1
    for ($i = $begin + 1; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -eq $EndMarker) { $end = $i; break } }
    if ($end -lt 0) { return @{ Begin = $begin; End = $Lines.Count - 1; Intact = $false } }
    $body = @($Lines[($begin + 1)..($end - 1)] | Where-Object { $_ -ne $null })
    if ($end -eq $begin + 1) { $body = @() }
    @{ Begin = $begin; End = $end; Intact = ($body.Count -eq 1 -and $body[0] -cmatch $AliasLinePattern) }
}

function Remove-Block([string[]]$Lines, $Block) {
    $from = $Block.Begin
    # Also drop the blank separator line Install added in front of the block.
    if ($from -gt 0 -and $Lines[$from - 1] -eq '') { $from-- }
    $keep = @()
    if ($from -gt 0) { $keep += $Lines[0..($from - 1)] }
    if ($Block.End + 1 -lt $Lines.Count) { $keep += $Lines[($Block.End + 1)..($Lines.Count - 1)] }
    , $keep
}

function Update-Profile([string]$Label, [string]$Path) {
    $file = Read-TextFile $Path
    $nl = if ($file.Text.Contains("`n") -and -not $file.Text.Contains("`r`n")) { "`n" } else { "`r`n" }
    $lines = [string[]]($file.Text -split "`r?`n")
    $block = Find-Block $lines

    if ($block -and -not $block.Intact) {
        Write-Warning "[$Label] $Path : the belowZero block was edited by hand; leaving it alone."
        $script:conflicts++; return
    }
    if ($Action -eq 'Status') {
        $state = if ($block) { 'installed' } elseif ($file.Exists) { 'not installed' } else { 'no profile file' }
        Write-Host "[$Label] $state ($Path)"; return
    }

    [string[]]$rest = if ($block) { Remove-Block $lines $block } else { $lines }
    if ($Action -eq 'Install') {
        $other = @($rest | Where-Object { $_ -match $ConflictPattern })
        if ($other.Count -gt 0) {
            Write-Warning "[$Label] $Path already defines /bzo ($($other[0].Trim())); not overwriting. Remove it or uninstall the other entry, then run Install again."
            $script:conflicts++; return
        }
        $aliasLine = "Set-Alias -Name '/bzo' -Value '" + $ExePath.Replace("'", "''") + "' -Scope Global"
        $end = $rest.Count
        while ($end -gt 0 -and $rest[$end - 1] -eq '') { $end-- }
        $new = New-Object 'Collections.Generic.List[string]'
        for ($i = 0; $i -lt $end; $i++) { $new.Add($rest[$i]) }
        if ($new.Count -gt 0) { $new.Add('') }
        $new.AddRange([string[]]@($BeginMarker, $aliasLine, $EndMarker, ''))
        $text = $new -join $nl
    } else {
        if (-not $block) { Write-Host "[$Label] nothing to remove ($Path)"; return }
        $text = $rest -join $nl
    }

    if ($Action -eq 'Uninstall' -and $text.Trim() -eq '') {
        Remove-Item -LiteralPath $Path -Force   # the block was all this file held
    } else {
        $enc = $file.Encoding
        # PowerShell 7 reads a BOM-less profile as UTF-8 and 5.1 as ANSI; a BOM makes a non-ASCII path safe in both.
        # Existing content is pure ASCII here, so adding the BOM does not change its meaning.
        if ($file.AsciiOnly -and $text -cmatch '[^\x00-\x7F]') { $enc = New-Object Text.UTF8Encoding($true) }
        Write-TextFile $Path $text $enc
    }
    Write-Host "[$Label] $($Action.ToLower())ed $Path"
}

function Update-Macro {
    $existing = $null
    if (Test-Path -LiteralPath $MacroFile -PathType Leaf) {
        $existing = ([IO.File]::ReadAllText($MacroFile, (Get-OemEncoding))).Trim()
    }
    $owned = $existing -and $existing -cmatch $MacroLinePattern
    switch ($Action) {
        'Status' {
            $state = if ($owned) { 'installed' } elseif ($existing) { 'present but not belowZero-owned' } else { 'not installed' }
            Write-Host "[cmd] $state ($MacroFile)"
        }
        'Uninstall' {
            if ($owned) { Remove-Item -LiteralPath $MacroFile -Force; Write-Host "[cmd] removed $MacroFile" }
            elseif ($existing) { Write-Warning "[cmd] $MacroFile is not belowZero-owned; leaving it alone."; $script:conflicts++ }
            else { Write-Host "[cmd] nothing to remove ($MacroFile)" }
        }
        'Install' {
            if ($existing -and -not $owned) {
                Write-Warning "[cmd] $MacroFile exists and is not belowZero-owned; not overwriting."
                $script:conflicts++; return
            }
            $template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'bzo.doskey.template'))
            $text = $template.Replace('{{BZO_EXE}}', $ExePath).TrimEnd() + "`r`n"
            $enc = Get-OemEncoding   # doskey reads macro files in the console's OEM code page
            if ($enc.GetString($enc.GetBytes($text)) -cne $text) {
                throw "The install path cannot be represented in the OEM code page ($($enc.WebName)); doskey cannot load it."
            }
            $dir = Split-Path -Parent $MacroFile
            if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force }
            [IO.File]::WriteAllText($MacroFile, $text, $enc)
            Write-Host "[cmd] installed $MacroFile"
            Write-Host "      load it in a cmd session / startup script with:"
            Write-Host "      doskey /exename=cmd.exe /macrofile=`"$MacroFile`""
        }
    }
}

try {
    if ($Action -eq 'Install') {
        if (-not $ExePath -or -not [IO.Path]::IsPathRooted($ExePath) -or $ExePath -match '["\r\n]' -or
            -not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
            throw "-ExePath must be the absolute path of an existing bzo.exe (no double quotes or line breaks)."
        }
        $ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
    }
    if (-not $MacroFile) { $MacroFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'belowZero\bzo.doskey' }
    $targets = [ordered]@{}
    if ($ProfilePath) {
        $n = 0; foreach ($p in $ProfilePath.Split('|')) { $targets["profile$((++$n))"] = $p }
    } else {
        $docs = [Environment]::GetFolderPath('MyDocuments')   # honours a redirected/OneDrive Documents folder
        $targets['Windows PowerShell'] = Join-Path $docs 'WindowsPowerShell\profile.ps1'
        $targets['PowerShell 7'] = Join-Path $docs 'PowerShell\profile.ps1'
    }

    foreach ($label in $targets.Keys) {
        try { Update-Profile $label $targets[$label] }
        catch { Write-Error "[$label] $($targets[$label]): $($_.Exception.Message)" -ErrorAction Continue; $failures++ }
    }
    try { Update-Macro }
    catch { Write-Error "[cmd] $($_.Exception.Message)" -ErrorAction Continue; $failures++ }

    if ($Action -ne 'Uninstall') {
        $policy = Get-ExecutionPolicy
        if ($policy -in 'Restricted', 'AllSigned') {
            Write-Warning "Execution policy here is ${policy}: PowerShell will not load an unsigned profile, so /bzo will not resolve in such sessions. Allow profile scripts (e.g. Set-ExecutionPolicy -Scope CurrentUser RemoteSigned) or call bzo.exe directly."
        }
    }
} catch {
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 1
}

if ($failures -gt 0) { exit 1 }
if ($conflicts -gt 0) { exit 3 }
exit 0
