// SSDShredder.DesktopBroker: the small native local COM server behind the
// belowZero desktop shortcut (docs/02_SYSTEM_ARCHITECTURE.md, "DesktopBroker").
//
// Three launch shapes, all unelevated:
//   (no args)                      -> direct shortcut double-click: send an
//                                      "open" activation (source=desktop).
//   -Embedding                      -> Explorer's COM activation for a drop
//                                      on the desktop icon: register the
//                                      DropTarget CLSID and service IDropTarget.
//   --from-explorer <pipe-handle>   -> spawned by ExplorerCommand::Invoke,
//                                      which inherited one read end of an
//                                      anonymous pipe only to this child;
//                                      read one bounded path from it and
//                                      forward it as source=explorer.
//
// This file never deletes, trims or otherwise mutates user data: it only
// extracts a path and a drop/launch intent and hands both to the UI's
// ActivationRouter, which itself grants no mutation authority (the worker
// still performs its own inspect/confirm flow).

#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <objbase.h>

#include <atomic>
#include <cstdlib>
#include <string>

#include "ActivationEnvelope.h"
#include "ActivationClient.h"
#include "Clsids.h"
#include "Registration.h"
#include "UiLauncher.h"

using namespace belowzero;

namespace {

std::atomic<LONG> g_lockCount{0};
std::atomic<LONG> g_objectCount{0};

// ---- IDropTarget ------------------------------------------------------

class DropTargetImpl : public IDropTarget {
public:
    DropTargetImpl() { g_objectCount.fetch_add(1); }
    virtual ~DropTargetImpl() { g_objectCount.fetch_sub(1); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_IDropTarget) {
            *ppv = static_cast<IDropTarget*>(this);
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

    HRESULT STDMETHODCALLTYPE DragEnter(IDataObject* dataObject, DWORD /*keyState*/, POINTL /*pt*/,
                                         DWORD* effect) override {
        *effect = IsSingleFileOffer(dataObject) ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DragOver(DWORD /*keyState*/, POINTL /*pt*/, DWORD* effect) override {
        // Never widen beyond what DragEnter already decided for this data object.
        *effect = (*effect != DROPEFFECT_NONE) ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DragLeave() override { return S_OK; }

    HRESULT STDMETHODCALLTYPE Drop(IDataObject* dataObject, DWORD /*keyState*/, POINTL /*pt*/,
                                    DWORD* effect) override {
        std::wstring path;
        if (!TryExtractSingleFilePath(dataObject, &path)) {
            *effect = DROPEFFECT_NONE;
            return S_OK;
        }

        std::string id;
        std::string envelope = BuildSelectEnvelopeJson(ActivationSource::Desktop, path, &id);
        bool accepted = SendActivationStartingUiIfNeeded(envelope);

        // A desktop drop is always a non-destructive handoff: never MOVE, and
        // never tell the drag source it may delete its original regardless
        // of whether the UI accepted the activation.
        *effect = accepted ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return S_OK;
    }

private:
    static bool IsSingleFileOffer(IDataObject* dataObject) {
        std::wstring unused;
        return TryExtractSingleFilePath(dataObject, &unused);
    }

    static bool TryExtractSingleFilePath(IDataObject* dataObject, std::wstring* outPath) {
        FORMATETC format = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        STGMEDIUM medium = {};
        if (FAILED(dataObject->GetData(&format, &medium))) return false;

        bool ok = false;
        HDROP hDrop = reinterpret_cast<HDROP>(medium.hGlobal);
        if (hDrop != nullptr) {
            UINT count = DragQueryFileW(hDrop, 0xFFFFFFFF, nullptr, 0);
            if (count == 1) {
                wchar_t buffer[MAX_PATH];
                UINT len = DragQueryFileW(hDrop, 0, buffer, ARRAYSIZE(buffer));
                if (len > 0 && len < ARRAYSIZE(buffer)) {
                    *outPath = std::wstring(buffer, len);
                    ok = true;
                }
            }
        }
        ReleaseStgMedium(&medium);
        return ok;
    }

    std::atomic<ULONG> refCount_{0};
};

// ---- IClassFactory -----------------------------------------------------

class DropTargetClassFactory : public IClassFactory {
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
        auto* dropTarget = new DropTargetImpl();
        HRESULT hr = dropTarget->QueryInterface(riid, ppv);
        dropTarget->Release();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) {
            g_lockCount.fetch_add(1);
        } else {
            g_lockCount.fetch_sub(1);
        }
        return S_OK;
    }

private:
    std::atomic<ULONG> refCount_{1};
};

// ---- Launch paths --------------------------------------------------------

int RunDefaultOpenLaunch() {
    std::string id;
    std::string envelope = BuildOpenEnvelopeJson(ActivationSource::Desktop, &id);
    return SendActivationStartingUiIfNeeded(envelope) ? 0 : 1;
}

// Reads one bounded, newline-terminated absolute path from the inherited
// pipe handle ExplorerCommand::Invoke passed, then forwards it as a
// "select" activation with source=explorer.
int RunFromExplorerHandoff(HANDLE inheritedReadHandle) {
    constexpr DWORD kMaxPathFrame = 32 * 1024;  // generous bound; real paths are far shorter.
    std::string line;
    char buffer[4096];
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(inheritedReadHandle, buffer, sizeof(buffer), &read, nullptr) || read == 0) {
            CloseHandle(inheritedReadHandle);
            return 1;  // ExplorerCommand's write end closed without a complete frame.
        }
        bool done = false;
        for (DWORD i = 0; i < read; ++i) {
            if (buffer[i] == '\n') {
                done = true;
                break;
            }
            line.push_back(buffer[i]);
            if (line.size() > kMaxPathFrame) {
                CloseHandle(inheritedReadHandle);
                return 1;
            }
        }
        if (done) break;
    }
    CloseHandle(inheritedReadHandle);

    if (line.empty()) return 1;
    int wideLen = MultiByteToWideChar(CP_UTF8, 0, line.c_str(), -1, nullptr, 0);
    std::wstring widePath(static_cast<size_t>(wideLen > 0 ? wideLen - 1 : 0), L'\0');
    if (wideLen > 0) {
        MultiByteToWideChar(CP_UTF8, 0, line.c_str(), -1, widePath.data(), wideLen);
    }

    std::string id;
    std::string envelope = BuildSelectEnvelopeJson(ActivationSource::Explorer, widePath, &id);
    return SendActivationStartingUiIfNeeded(envelope) ? 0 : 1;
}

int RunComEmbeddingServer() {
    DropTargetClassFactory* factory = new DropTargetClassFactory();
    DWORD regCookie = 0;
    HRESULT hr = CoRegisterClassObject(CLSID_BelowZeroDropTarget, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE,
                                        &regCookie);
    if (FAILED(hr)) {
        factory->Release();
        return 1;
    }

    // Simple idle-shutdown message loop: once nobody holds a lock and no
    // DropTargetImpl instance is alive, exit. A production build should also
    // bound total idle lifetime; this scaffold favors clarity over that polish.
    SetTimer(nullptr, 0, 2000, nullptr);
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0)) {
        if (msg.message == WM_TIMER && g_lockCount.load() <= 0 && g_objectCount.load() <= 0) {
            PostQuitMessage(0);
            continue;
        }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    CoRevokeClassObject(regCookie);
    factory->Release();
    return 0;
}

}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc = 0;
    PWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);

    HRESULT comInit = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    int exitCode = 1;

    if (argc >= 2 && std::wstring(argv[1]) == L"-Embedding") {
        exitCode = RunComEmbeddingServer();
    } else if (argc >= 3 && std::wstring(argv[1]) == L"--from-explorer") {
        auto handleValue = static_cast<HANDLE>(reinterpret_cast<void*>(static_cast<intptr_t>(_wtoi64(argv[2]))));
        exitCode = RunFromExplorerHandoff(handleValue);
    } else {
        exitCode = RunDefaultOpenLaunch();
    }

    if (SUCCEEDED(comInit)) CoUninitialize();
    if (argv) LocalFree(argv);
    return exitCode;
}
