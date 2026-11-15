# SSDShredder.ShellExtension

Engineer 3's native Windows half: `SSDShredder.DesktopBroker` (local COM
`IDropTarget` server + default "open the bin panel" launcher),
`SSDShredder.ExplorerCommand` (the `IExplorerCommand` context-menu DLL), and
a minimal per-user `SSDShredder.Installer` tying both into a desktop
shortcut and registry registration. See
[docs/02_SYSTEM_ARCHITECTURE.md](../../docs/02_SYSTEM_ARCHITECTURE.md) for
the activation contract these three implement against.

## Status: builds, installs and uninstalls on Windows 11; real-mouse checks still open

The code was written on macOS and first built and exercised on Windows 11
Pro (MSVC 19.44, Windows SDK 10.0.26100, Debug). Verified so far:
install, repair and uninstall (idempotent; repair is identical to a fresh
install), the desktop shortcut surviving an Explorer restart, the
`IExplorerCommand` verb appearing and launching the UI, COM activation of the
drop-target broker, cold-start hand-off to the UI, and duplicate activation
focusing the one existing window.

**Not verified:** a real mouse drag onto the desktop icon (programmatic drops
through the shell's own drop target did not complete), the sign-out/sign-in
persistence check, UAC refusal, and that the panel visibly starts inspection
(window existence was checked, its contents were not). Treat those as open.

What *is* verified: the wire protocol this native code speaks
(`bzo-ar1-<10-hex>` pipe name, newline-delimited JSON frames, the exact
`v`/`id`/`action`/`source`/`paths` envelope and `v`/`id`/`accepted` ack) is
the same protocol `SSDShredder.UI`'s `ActivationRouter` implements and has
passing unit/integration tests for (`tests/SSDShredder.UI.Tests`). If the
two ever drift, that's a bug in whichever side changed last — re-run the UI
tests and update `Common/PipeName.cpp` / `Common/ActivationEnvelope.cpp` to
match `ActivationRouter.cs` and `ActivationEnvelope.cs`.

## Build (on Windows, once a teammate has a machine for this)

```powershell
cmake -G "Visual Studio 17 2022" -A x64 -B build
cmake --build build --config Debug
```

Or open the `SSDShredder.ShellExtension` folder directly in Visual Studio
2022 with the "Desktop development with C++" workload (its CMake integration
picks up `CMakeLists.txt` automatically). Needs the Windows 11 SDK for
`shobjidl_core.h`'s `IExplorerCommand` and `winioctl.h`-adjacent headers.

## Manual install/uninstall for testing

Artifacts are laid out as `build/out/<Config>/<TargetName>/`, which is the
sibling layout the installer and `UiLauncher.cpp` resolve. Publish the UI into
the same tree first (framework-dependent, needs the .NET 9 SDK):

```powershell
dotnet publish ..\SSDShredder.UI -c Debug -r win-x64 --self-contained false -o build\out\Debug\SSDShredder.UI
```

Then, from `build/out/<Config>/SSDShredder.Installer/`:

```powershell
.\SSDShredder.Installer.exe install     # creates the desktop shortcut, registers COM classes
.\SSDShredder.Installer.exe uninstall   # removes everything the installer owns
.\SSDShredder.Installer.exe repair      # uninstall, then reinstall
```

This installer is deliberately per-user (`HKCU`) and requests no elevation,
so it is *not* the eventual administrator-protected installer the
architecture doc describes — it exists so install/repair/uninstall can be
exercised today without an MSI/MSIX pipeline. It assumes
`SSDShredder.DesktopBroker.exe`, `SSDShredder.ExplorerCommand.dll` and
`SSDShredder.UI.exe` (published separately via `dotnet publish` from
`src/SSDShredder.UI`) sit in sibling build-output directories next to it;
see `ResolveInstalledUiExecutablePath` / `ResolveInstalledBrokerExecutablePath`
for the exact fallback paths, and `Common/Registration.h`'s doc comment for
the gap between this and the packaged-identity (`desktop4:`/`desktop5:`
manifest extension) registration the architecture doc also describes —
`ExplorerCommand/PackageManifestSnippet.xml` is the reference fragment for
adopting that later without touching `ExplorerCommand.cpp`.

## What's deliberately out of scope here

`SSDShredder.Worker` (Engineer 2's project) does not exist in this
checkout, so there is no real inspect/trim/shred behind any of this yet.
`SSDShredder.UI`'s `SimulatedWorkerClient` mocks that for the UI's own
demo loop; this native code never talks to a worker at all — it only ever
forwards an activation envelope to the UI, which grants no mutation
authority by itself.
