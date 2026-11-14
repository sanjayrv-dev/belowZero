#include "Registration.h"

#include <shlobj.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <objbase.h>

#include <filesystem>

#pragma comment(lib, "shlwapi.lib")

namespace belowzero {
namespace {

std::wstring FileNameOf(const std::wstring& path) {
    return std::filesystem::path(path).filename().wstring();
}

LSTATUS SetStringValue(HKEY root, const std::wstring& subKey, const wchar_t* valueName, const std::wstring& value) {
    HKEY key = nullptr;
    LSTATUS status = RegCreateKeyExW(root, subKey.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS) return status;

    status = RegSetValueExW(key, valueName, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()),
                             static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return status;
}

}  // namespace

HRESULT CreateDesktopShortcut(const std::wstring& targetExePath, const std::wstring& iconPath,
                               const std::wstring& shortcutBaseName) {
    PWSTR desktopPath = nullptr;
    HRESULT hr = SHGetKnownFolderPath(FOLDERID_Desktop, 0, nullptr, &desktopPath);
    if (FAILED(hr)) return hr;
    std::wstring shortcutPath = std::wstring(desktopPath) + L"\\" + shortcutBaseName + L".lnk";
    CoTaskMemFree(desktopPath);

    IShellLinkW* shellLink = nullptr;
    hr = CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&shellLink));
    if (FAILED(hr)) return hr;

    hr = shellLink->SetPath(targetExePath.c_str());
    if (SUCCEEDED(hr)) hr = shellLink->SetIconLocation(iconPath.c_str(), 0);
    if (SUCCEEDED(hr)) hr = shellLink->SetDescription(L"belowZero — secure file disposal");

    if (SUCCEEDED(hr)) {
        IPersistFile* persistFile = nullptr;
        hr = shellLink->QueryInterface(IID_PPV_ARGS(&persistFile));
        if (SUCCEEDED(hr)) {
            hr = persistFile->Save(shortcutPath.c_str(), TRUE);
            persistFile->Release();
        }
    }

    shellLink->Release();
    return hr;
}

HRESULT RemoveDesktopShortcut(const std::wstring& shortcutBaseName) {
    PWSTR desktopPath = nullptr;
    HRESULT hr = SHGetKnownFolderPath(FOLDERID_Desktop, 0, nullptr, &desktopPath);
    if (FAILED(hr)) return hr;
    std::wstring shortcutPath = std::wstring(desktopPath) + L"\\" + shortcutBaseName + L".lnk";
    CoTaskMemFree(desktopPath);

    if (!DeleteFileW(shortcutPath.c_str()) && GetLastError() != ERROR_FILE_NOT_FOUND) {
        return HRESULT_FROM_WIN32(GetLastError());
    }
    return S_OK;
}

LSTATUS RegisterDesktopBrokerDropTarget(const std::wstring& clsidString, const std::wstring& brokerExePath) {
    LSTATUS status = SetStringValue(
        HKEY_CURRENT_USER, L"Software\\Classes\\CLSID\\" + clsidString + L"\\LocalServer32", nullptr, brokerExePath);
    if (status != ERROR_SUCCESS) return status;

    std::wstring appPathsKey =
        L"Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\" + FileNameOf(brokerExePath);
    status = SetStringValue(HKEY_CURRENT_USER, appPathsKey, nullptr, brokerExePath);
    if (status != ERROR_SUCCESS) return status;

    return SetStringValue(HKEY_CURRENT_USER, appPathsKey, L"DropTarget", clsidString);
}

LSTATUS UnregisterDesktopBrokerDropTarget(const std::wstring& clsidString, const std::wstring& brokerExePath) {
    RegDeleteTreeW(HKEY_CURRENT_USER, (L"Software\\Classes\\CLSID\\" + clsidString).c_str());
    return RegDeleteTreeW(
        HKEY_CURRENT_USER,
        (L"Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\" + FileNameOf(brokerExePath)).c_str());
}

LSTATUS RegisterExplorerCommand(const std::wstring& clsidString, const std::wstring& dllPath,
                                 const std::wstring& verbKey, const std::wstring& verbDisplayName) {
    LSTATUS status = SetStringValue(
        HKEY_CURRENT_USER, L"Software\\Classes\\CLSID\\" + clsidString + L"\\InprocServer32", nullptr, dllPath);
    if (status != ERROR_SUCCESS) return status;
    status = SetStringValue(
        HKEY_CURRENT_USER, L"Software\\Classes\\CLSID\\" + clsidString + L"\\InprocServer32", L"ThreadingModel",
        L"Apartment");
    if (status != ERROR_SUCCESS) return status;

    std::wstring shellVerbKey = L"Software\\Classes\\*\\shell\\" + verbKey;
    status = SetStringValue(HKEY_CURRENT_USER, shellVerbKey, nullptr, verbDisplayName);
    if (status != ERROR_SUCCESS) return status;
    return SetStringValue(HKEY_CURRENT_USER, shellVerbKey, L"ExplorerCommandHandler", clsidString);
}

LSTATUS UnregisterExplorerCommand(const std::wstring& clsidString, const std::wstring& verbKey) {
    RegDeleteTreeW(HKEY_CURRENT_USER, (L"Software\\Classes\\*\\shell\\" + verbKey).c_str());
    return RegDeleteTreeW(HKEY_CURRENT_USER, (L"Software\\Classes\\CLSID\\" + clsidString).c_str());
}

}  // namespace belowzero
