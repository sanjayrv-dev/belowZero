// SSDShredder.ExplorerCommand: the IExplorerCommand DLL that puts belowZero
// on the "*" item-type context menu (docs/02_SYSTEM_ARCHITECTURE.md,
// "ExplorerCommand"). GetState does only a cheap selection-count check;
// Invoke starts the installed DesktopBroker and hands it exactly one
// bounded activation through an anonymous pipe whose read end is inherited
// only by that one child process — this DLL never talks to the UI's
// ActivationRouter pipe directly, and never touches the selected file
// itself. The broker validates the handoff and forwards it to the UI
// labeled source=explorer.

#include <windows.h>
#include <shlobj.h>
#include <shobjidl_core.h>

#include <atomic>
#include <filesystem>
#include <string>
#include <vector>

#include "Clsids.h"
#include "Registration.h"
#include "UiLauncher.h"

using namespace belowzero;

namespace {

std::atomic<LONG> g_dllRefCount{0};
HMODULE g_thisModule = nullptr;

std::wstring ResolveInstalledBrokerExecutablePath() {
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\belowZero\\InstallDir", 0, KEY_READ, &key) == ERROR_SUCCESS) {
        wchar_t buffer[MAX_PATH] = {};
        DWORD size = sizeof(buffer);
        if (RegGetValueW(key, nullptr, L"BrokerExePath", RRF_RT_REG_SZ, nullptr, buffer, &size) == ERROR_SUCCESS) {
            RegCloseKey(key);
            return buffer;
        }
        RegCloseKey(key);
    }

    wchar_t selfPath[MAX_PATH] = {};
    if (g_thisModule && GetModuleFileNameW(g_thisModule, selfPath, MAX_PATH) != 0) {
        auto dllDir = std::filesystem::path(selfPath).parent_path();
        auto candidate = dllDir.parent_path() / L"SSDShredder.DesktopBroker" / L"SSDShredder.DesktopBroker.exe";
        if (std::filesystem::exists(candidate)) return candidate.wstring();
    }
    return L"";
}

// Spawns brokerExePath with --from-explorer <inherited-handle>, where only
// that one read handle (not the whole inheritable-handle table) is passed
// down, per MS's "Protecting Against Inheritance Side-Channel Attacks"
// guidance for exactly this kind of one-shot handoff.
bool HandOffPathToBroker(const std::wstring& brokerExePath, const std::wstring& selectedPath) {
    HANDLE readHandle = nullptr;
    HANDLE writeHandle = nullptr;
    if (!CreatePipe(&readHandle, &writeHandle, nullptr, 0)) return false;
    if (!SetHandleInformation(readHandle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) {
        CloseHandle(readHandle);
        CloseHandle(writeHandle);
        return false;
    }

    SIZE_T attrListSize = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attrListSize);
    std::vector<BYTE> attrListBuffer(attrListSize);
    auto* attrList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attrListBuffer.data());
    bool ok = InitializeProcThreadAttributeList(attrList, 1, 0, &attrListSize) != 0;
    HANDLE inheritList[1] = {readHandle};
    ok = ok && UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inheritList,
                                          sizeof(inheritList), nullptr, nullptr);

    STARTUPINFOEXW startupInfoEx = {};
    startupInfoEx.StartupInfo.cb = sizeof(startupInfoEx);
    startupInfoEx.lpAttributeList = ok ? attrList : nullptr;

    std::wstring commandLine =
        L"\"" + brokerExePath + L"\" --from-explorer " + std::to_wstring(reinterpret_cast<intptr_t>(readHandle));

    PROCESS_INFORMATION processInfo = {};
    BOOL launched = ok && CreateProcessW(brokerExePath.c_str(), commandLine.data(), nullptr, nullptr,
                                          /*bInheritHandles=*/TRUE, EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr,
                                          &startupInfoEx.StartupInfo, &processInfo);

    if (ok) DeleteProcThreadAttributeList(attrList);
    CloseHandle(readHandle);  // The child's duplicate (same numeric value) is what it reads from.

    if (!launched) {
        CloseHandle(writeHandle);
        return false;
    }
    CloseHandle(processInfo.hThread);
    CloseHandle(processInfo.hProcess);

    int utf8Len =
        WideCharToMultiByte(CP_UTF8, 0, selectedPath.c_str(), static_cast<int>(selectedPath.size()), nullptr, 0,
                             nullptr, nullptr);
    std::string utf8(static_cast<size_t>(utf8Len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, selectedPath.c_str(), static_cast<int>(selectedPath.size()), utf8.data(),
                         utf8Len, nullptr, nullptr);
    utf8.push_back('\n');

    DWORD written = 0;
    bool wroteOk = WriteFile(writeHandle, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr) &&
                   written == utf8.size();
    CloseHandle(writeHandle);
    return wroteOk;
}

// ---- IExplorerCommand ----------------------------------------------------

class ExplorerCommandImpl : public IExplorerCommand {
public:
    ExplorerCommandImpl() { g_dllRefCount.fetch_add(1); }
    ~ExplorerCommandImpl() override { g_dllRefCount.fetch_sub(1); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_IExplorerCommand) {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refCount_; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG remaining = --refCount_;
        if (remaining == 0) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, LPWSTR* name) override {
        return SHStrDupW(kExplorerVerbDisplayName, name);
    }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, LPWSTR* icon) override {
        *icon = nullptr;
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, LPWSTR* tip) override {
        *tip = nullptr;
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* commandName) override {
        *commandName = CLSID_BelowZeroExplorerCommand;
        return S_OK;
    }

    // "GetState performs cheap selection checks."
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* selection, BOOL /*okToBeSlow*/,
                                        EXPCMDSTATE* state) override {
        DWORD count = 0;
        if (selection) selection->GetCount(&count);
        *state = (count == 1) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override {
        *flags = ECF_DEFAULT;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** enumCommands) override {
        *enumCommands = nullptr;
        return E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* selection, IBindCtx*) override {
        if (selection == nullptr) return E_INVALIDARG;

        DWORD count = 0;
        selection->GetCount(&count);
        if (count != 1) return S_OK;  // Arity is one file per activation, per the spec.

        IShellItem* item = nullptr;
        if (FAILED(selection->GetItemAt(0, &item)) || item == nullptr) return S_OK;

        LPWSTR path = nullptr;
        HRESULT hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
        item->Release();
        if (FAILED(hr) || path == nullptr) return S_OK;

        std::wstring selectedPath(path);
        CoTaskMemFree(path);

        std::wstring brokerPath = ResolveInstalledBrokerExecutablePath();
        if (!brokerPath.empty()) {
            HandOffPathToBroker(brokerPath, selectedPath);
        }
        return S_OK;  // Invoke's own failure must never look like a confirmed file operation.
    }

private:
    std::atomic<ULONG> refCount_{0};
};

class ExplorerCommandClassFactory : public IClassFactory {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refCount_; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG remaining = --refCount_;
        if (remaining == 0) delete this;
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        auto* command = new ExplorerCommandImpl();
        HRESULT hr = command->QueryInterface(riid, ppv);
        command->Release();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) override { return S_OK; }

private:
    std::atomic<ULONG> refCount_{1};
};

}  // namespace

// ---- DLL exports ----------------------------------------------------------

extern "C" BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) g_thisModule = module;
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    if (clsid != CLSID_BelowZeroExplorerCommand) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new ExplorerCommandClassFactory();
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return g_dllRefCount.load() == 0 ? S_OK : S_FALSE; }

extern "C" HRESULT __stdcall DllRegisterServer() {
    wchar_t dllPath[MAX_PATH] = {};
    GetModuleFileNameW(g_thisModule, dllPath, MAX_PATH);
    LSTATUS status = RegisterExplorerCommand(kExplorerCommandClsidString, dllPath, kExplorerVerbKey,
                                              kExplorerVerbDisplayName);
    return status == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(status);
}

extern "C" HRESULT __stdcall DllUnregisterServer() {
    LSTATUS status = UnregisterExplorerCommand(kExplorerCommandClsidString, kExplorerVerbKey);
    return status == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(status);
}
