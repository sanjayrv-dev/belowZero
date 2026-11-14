#pragma once
#include <windows.h>
#include <string>

namespace belowzero {

// All registration here is per-user (HKCU) and reversible, matching
// "Register only the product's COM classes, App Paths entry, shortcut and
// package declarations ... remove owned registrations on uninstall"
// (docs/02_SYSTEM_ARCHITECTURE.md). None of it requests elevation.
//
// This scaffold registers IExplorerCommand the classic, unpackaged way
// (HKCU\...\shell\<verb>\ExplorerCommandHandler), which an installer can
// exercise and a developer can self-register today without MSIX tooling.
// The architecture doc also describes a packaged-identity registration
// (com:SurrogateServer/com:Class, desktop4:FileExplorerContextMenus,
// desktop5:ItemType/Verb) for when this ships as an MSIX/sparse package;
// see PackageManifestSnippet.xml in SSDShredder.ShellExtension/ExplorerCommand
// for that fragment, kept as reference until the installer owner adopts it.

// Creates (or overwrites) belowZero.lnk on the current user's real desktop,
// targeting targetExePath with the given icon, via IShellLinkW + IPersistFile.
HRESULT CreateDesktopShortcut(const std::wstring& targetExePath, const std::wstring& iconPath,
                               const std::wstring& shortcutBaseName = L"belowZero");

// Removes belowZero.lnk from the desktop if present. No-ops (returns S_OK)
// if it was never created.
HRESULT RemoveDesktopShortcut(const std::wstring& shortcutBaseName = L"belowZero");

// Registers clsid's LocalServer32 as brokerExePath and marks brokerExePath's
// App Paths entry with DropTarget=clsid, so Explorer treats drops on a
// shortcut to that exe as a COM drop target activation.
LSTATUS RegisterDesktopBrokerDropTarget(const std::wstring& clsidString, const std::wstring& brokerExePath);
LSTATUS UnregisterDesktopBrokerDropTarget(const std::wstring& clsidString, const std::wstring& brokerExePath);

// Registers the IExplorerCommand DLL's CLSID (InprocServer32) and the
// classic HKCU\...\shell\<verbKey>\ExplorerCommandHandler verb that makes
// Explorer show it on the "*" item-type context menu.
LSTATUS RegisterExplorerCommand(const std::wstring& clsidString, const std::wstring& dllPath,
                                 const std::wstring& verbKey, const std::wstring& verbDisplayName);
LSTATUS UnregisterExplorerCommand(const std::wstring& clsidString, const std::wstring& verbKey);

}  // namespace belowzero
