// A minimal, manual stand-in for "the installer" that docs/02_SYSTEM_ARCHITECTURE.md
// assumes exists ("Use an administrator-protected installation directory for
// binaries... Register only the product's COM classes, App Paths entry,
// shortcut and package declarations. Refresh shell association state after
// install/update and remove owned registrations on uninstall."). This tool
// is deliberately per-user (HKCU) and admin-free so Engineer 3 (and Windows
// teammates) can exercise install/repair/uninstall without an MSI/MSIX
// pipeline; a real installer should still own the final distributed version
// of this logic (likely calling the same Registration.h helpers from its own
// elevated/administrator-protected install step for an all-users install).
//
// Usage: SSDShredder.Installer.exe install|uninstall|repair
// Expects SSDShredder.DesktopBroker.exe, SSDShredder.ExplorerCommand.dll and
// SSDShredder.UI.exe to already sit in sibling directories next to this
// executable's own directory (matching the layout UiLauncher.cpp guesses).

#include <windows.h>
#include <shlobj.h>
#include <iostream>
#include <filesystem>
#include <string>

#include "Clsids.h"
#include "Registration.h"

using namespace belowzero;

namespace {

std::filesystem::path ThisExeDir() {
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    return std::filesystem::path(path).parent_path();
}

LSTATUS SetInstallDirValue(const wchar_t* valueName, const std::wstring& value) {
    HKEY key = nullptr;
    LSTATUS status =
        RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\belowZero\\InstallDir", 0, nullptr, 0, KEY_SET_VALUE, nullptr,
                         &key, nullptr);
    if (status != ERROR_SUCCESS) return status;
    status = RegSetValueExW(key, valueName, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
                             static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return status;
}

bool SelfRegisterExplorerCommandDll(const std::wstring& dllPath, bool registerIt) {
    HMODULE dll = LoadLibraryW(dllPath.c_str());
    if (!dll) {
        std::wcerr << L"Could not load " << dllPath << L" (GetLastError=" << GetLastError() << L")\n";
        return false;
    }
    using EntryPointFn = HRESULT(__stdcall*)();
    auto entry = reinterpret_cast<EntryPointFn>(
        GetProcAddress(dll, registerIt ? "DllRegisterServer" : "DllUnregisterServer"));
    HRESULT hr = entry ? entry() : E_FAIL;
    FreeLibrary(dll);
    if (FAILED(hr)) {
        std::wcerr << L"DllRegisterServer/DllUnregisterServer failed: 0x" << std::hex << hr << L"\n";
        return false;
    }
    return true;
}

int RunInstall() {
    auto dir = ThisExeDir();
    auto uiExe = dir.parent_path() / L"SSDShredder.UI" / L"SSDShredder.UI.exe";
    auto brokerExe = dir.parent_path() / L"SSDShredder.DesktopBroker" / L"SSDShredder.DesktopBroker.exe";
    auto explorerDll = dir.parent_path() / L"SSDShredder.ExplorerCommand" / L"SSDShredder.ExplorerCommand.dll";

    bool ok = true;
    ok &= SetInstallDirValue(L"UiExePath", uiExe.wstring()) == ERROR_SUCCESS;
    ok &= SetInstallDirValue(L"BrokerExePath", brokerExe.wstring()) == ERROR_SUCCESS;
    ok &= RegisterDesktopBrokerDropTarget(kDropTargetClsidString, brokerExe.wstring()) == ERROR_SUCCESS;
    ok &= SelfRegisterExplorerCommandDll(explorerDll.wstring(), /*registerIt=*/true);
    ok &= SUCCEEDED(CreateDesktopShortcut(brokerExe.wstring(), brokerExe.wstring()));

    // "Refresh shell association state after install/update."
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);

    std::wcout << (ok ? L"belowZero installed for the current user.\n" : L"Install completed with errors.\n");
    return ok ? 0 : 1;
}

int RunUninstall() {
    auto dir = ThisExeDir();
    auto brokerExe = dir.parent_path() / L"SSDShredder.DesktopBroker" / L"SSDShredder.DesktopBroker.exe";
    auto explorerDll = dir.parent_path() / L"SSDShredder.ExplorerCommand" / L"SSDShredder.ExplorerCommand.dll";

    SelfRegisterExplorerCommandDll(explorerDll.wstring(), /*registerIt=*/false);
    UnregisterDesktopBrokerDropTarget(kDropTargetClsidString, brokerExe.wstring());
    RemoveDesktopShortcut();
    RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\belowZero\\InstallDir");
    // Drop the now-empty parent too. RegDeleteKeyW is non-recursive, so it fails harmlessly
    // (and leaves the key) if anything else ever lives under Software\belowZero.
    RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\belowZero");

    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);

    std::wcout << L"belowZero's shell integration was removed for the current user.\n";
    return 0;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    // CreateDesktopShortcut uses CoCreateInstance(CLSID_ShellLink); COM must be initialised first.
    HRESULT comInit = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(comInit)) {
        std::wcerr << L"CoInitializeEx failed: 0x" << std::hex << comInit << L"\n";
        return 1;
    }
    std::wstring verb = argc >= 2 ? argv[1] : L"";
    if (verb == L"install" || verb == L"repair") {
        if (verb == L"repair") RunUninstall();  // Qualify repair as a clean uninstall followed by reinstall.
        return RunInstall();
    }
    if (verb == L"uninstall") {
        return RunUninstall();
    }

    std::wcerr << L"Usage: SSDShredder.Installer.exe install|uninstall|repair\n";
    return 2;
}
