#include "UiLauncher.h"

#include "ActivationClient.h"

#include <windows.h>
#include <filesystem>

namespace belowzero {
namespace {

std::wstring ReadRegistryInstallDirUiPath() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\belowZero\\InstallDir", 0, KEY_READ, &key) != ERROR_SUCCESS) {
        return L"";
    }

    wchar_t buffer[MAX_PATH] = {};
    DWORD size = sizeof(buffer);
    std::wstring result;
    if (RegGetValueW(key, nullptr, L"UiExePath", RRF_RT_REG_SZ, nullptr, buffer, &size) == ERROR_SUCCESS) {
        result = buffer;
    }
    RegCloseKey(key);
    return result;
}

std::wstring GuessSiblingUiPath() {
    wchar_t selfPath[MAX_PATH] = {};
    if (GetModuleFileNameW(nullptr, selfPath, MAX_PATH) == 0) {
        return L"";
    }

    auto brokerDir = std::filesystem::path(selfPath).parent_path();
    auto candidate = brokerDir.parent_path() / L"SSDShredder.UI" / L"SSDShredder.UI.exe";
    return std::filesystem::exists(candidate) ? candidate.wstring() : L"";
}

}  // namespace

std::wstring ResolveInstalledUiExecutablePath() {
    auto fromRegistry = ReadRegistryInstallDirUiPath();
    if (!fromRegistry.empty()) return fromRegistry;
    return GuessSiblingUiPath();
}

bool SendActivationStartingUiIfNeeded(const std::string& envelopeJson) {
    if (SendActivationAndWaitForAck(envelopeJson, /*timeoutMillis=*/300)) {
        return true;
    }

    std::wstring uiPath = ResolveInstalledUiExecutablePath();
    if (uiPath.empty()) {
        return false;  // Nothing we can do without an installed/discoverable UI.
    }

    STARTUPINFOW startupInfo = {sizeof(startupInfo)};
    PROCESS_INFORMATION processInfo = {};
    // The UI always runs unelevated, as this broker does; never pass runas here.
    BOOL launched = CreateProcessW(uiPath.c_str(), nullptr, nullptr, nullptr, FALSE, 0, nullptr, nullptr,
                                    &startupInfo, &processInfo);
    if (!launched) {
        return false;
    }
    CloseHandle(processInfo.hThread);
    CloseHandle(processInfo.hProcess);

    return SendActivationAndWaitForAck(envelopeJson, /*timeoutMillis=*/5000);
}

}  // namespace belowzero
