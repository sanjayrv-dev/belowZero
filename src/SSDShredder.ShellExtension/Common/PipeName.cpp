#include "PipeName.h"

#include <windows.h>
#include <bcrypt.h>
#include <string>
#include <vector>

#pragma comment(lib, "bcrypt.lib")

namespace belowzero {
namespace {

constexpr wchar_t kPipeNamePrefix[] = L"bzo-ar1-";
constexpr int kHashHexChars = 10;  // 5 bytes of SHA-256, matching ActivationRouter.cs.

std::wstring ToLowerHex(const std::vector<BYTE>& bytes, int maxChars) {
    static const wchar_t kHex[] = L"0123456789abcdef";
    std::wstring out;
    out.reserve(static_cast<size_t>(maxChars));
    for (BYTE b : bytes) {
        if (static_cast<int>(out.size()) >= maxChars) break;
        out.push_back(kHex[(b >> 4) & 0xF]);
        if (static_cast<int>(out.size()) >= maxChars) break;
        out.push_back(kHex[b & 0xF]);
    }
    return out.substr(0, static_cast<size_t>(maxChars));
}

// SHA-256 over the UTF-8 bytes of userName, matching
// System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(userName)).
std::vector<BYTE> Sha256Utf8(const std::wstring& userName) {
    int utf8Len = WideCharToMultiByte(CP_UTF8, 0, userName.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::vector<char> utf8(static_cast<size_t>(utf8Len > 0 ? utf8Len : 1));
    if (utf8Len > 0) {
        WideCharToMultiByte(CP_UTF8, 0, userName.c_str(), -1, utf8.data(), utf8Len, nullptr, nullptr);
    }
    // utf8Len includes the null terminator; exclude it from the hashed length.
    ULONG dataLen = utf8Len > 0 ? static_cast<ULONG>(utf8Len - 1) : 0;

    std::vector<BYTE> digest(32, 0);
    BCRYPT_ALG_HANDLE alg = nullptr;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0 || alg == nullptr) {
        return digest;  // all-zero digest on failure; still produces a stable (if degenerate) pipe name.
    }
    BCryptHash(alg, nullptr, 0, reinterpret_cast<PUCHAR>(utf8.data()), dataLen, digest.data(),
               static_cast<ULONG>(digest.size()));
    BCryptCloseAlgorithmProvider(alg, 0);
    return digest;
}

std::wstring GetCurrentUserNameW() {
    wchar_t buffer[256];
    DWORD size = ARRAYSIZE(buffer);
    if (::GetUserNameW(buffer, &size)) {
        return std::wstring(buffer, size > 0 ? size - 1 : 0);  // GetUserNameW includes the null terminator in size.
    }
    return L"unknown-user";
}

}  // namespace

std::wstring BuildActivationPipeName() {
    auto digest = Sha256Utf8(GetCurrentUserNameW());
    return std::wstring(kPipeNamePrefix) + ToLowerHex(digest, kHashHexChars);
}

}  // namespace belowzero
