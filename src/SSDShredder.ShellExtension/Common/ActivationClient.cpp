#include "ActivationClient.h"

#include "ActivationEnvelope.h"
#include "PipeName.h"

#include <string>
#include <vector>

namespace belowzero {
namespace {

constexpr size_t kMaxFrameBytes = 65536;

bool WriteFrame(HANDLE pipe, const std::string& payload) {
    if (payload.size() > kMaxFrameBytes) return false;

    DWORD written = 0;
    if (!WriteFile(pipe, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr) ||
        written != payload.size()) {
        return false;
    }
    const char newline = '\n';
    if (!WriteFile(pipe, &newline, 1, &written, nullptr) || written != 1) {
        return false;
    }
    FlushFileBuffers(pipe);
    return true;
}

// Reads bytes until '\n' (exclusive) or kMaxFrameBytes is exceeded.
bool ReadFrame(HANDLE pipe, std::string* outLine) {
    outLine->clear();
    char buffer[4096];
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr) || read == 0) {
            return false;
        }
        for (DWORD i = 0; i < read; ++i) {
            if (buffer[i] == '\n') {
                return outLine->size() <= kMaxFrameBytes;
            }
            outLine->push_back(buffer[i]);
            if (outLine->size() > kMaxFrameBytes) return false;
        }
    }
}

}  // namespace

bool SendActivationAndWaitForAck(const std::string& envelopeJson, DWORD timeoutMillis) {
    std::wstring pipePath = L"\\\\.\\pipe\\" + BuildActivationPipeName();

    // No owner listening yet (e.g. first launch of the session) is the
    // expected, non-error outcome here; WaitNamedPipeW times out quickly in
    // that case rather than blocking the shell callback.
    if (!WaitNamedPipeW(pipePath.c_str(), timeoutMillis)) {
        return false;
    }

    HANDLE pipe = CreateFileW(pipePath.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) {
        return false;
    }

    bool accepted = false;
    if (WriteFrame(pipe, envelopeJson)) {
        std::string ackJson;
        if (ReadFrame(pipe, &ackJson)) {
            accepted = ParseAckAccepted(ackJson);
        }
    }

    CloseHandle(pipe);
    return accepted;
}

}  // namespace belowzero
