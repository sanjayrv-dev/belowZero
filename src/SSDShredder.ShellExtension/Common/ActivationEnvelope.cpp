#include "ActivationEnvelope.h"

#include <windows.h>
#include <objbase.h>
#include <rpc.h>

#pragma comment(lib, "rpcrt4.lib")

namespace belowzero {
namespace {

const char* SourceLiteral(ActivationSource source) {
    return source == ActivationSource::Desktop ? "desktop" : "explorer";
}

std::string NewLowercaseUuid() {
    UUID uuid;
    UuidCreate(&uuid);
    RPC_WSTR wstr = nullptr;
    UuidToStringW(&uuid, &wstr);
    std::wstring w(reinterpret_cast<wchar_t*>(wstr));
    RpcStringFreeW(&wstr);

    // UuidToStringW already yields lowercase, hyphenated digits — the same
    // shape as .NET's default Guid.ToString(), but convert explicitly rather
    // than trust that across SDK versions.
    std::string out(w.begin(), w.end());
    for (char& c : out) c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
    return out;
}

std::string Utf16ToUtf8(const std::wstring& wide) {
    if (wide.empty()) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(len), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), static_cast<int>(wide.size()), out.data(), len, nullptr, nullptr);
    return out;
}

// Escapes a string for embedding inside a JSON double-quoted literal.
// Windows paths only ever need backslash/quote handling here, but this
// covers the full minimal set (control chars included) so a stray
// unprintable byte can never break the frame.
std::string JsonEscape(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 8);
    for (unsigned char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    snprintf(buf, sizeof(buf), "\\u%04x", c);
                    out += buf;
                } else {
                    out.push_back(static_cast<char>(c));
                }
        }
    }
    return out;
}

}  // namespace

std::string BuildOpenEnvelopeJson(ActivationSource source, std::string* outId) {
    std::string id = NewLowercaseUuid();
    if (outId) *outId = id;

    return std::string("{\"v\":1,\"id\":\"") + id + "\",\"action\":\"open\",\"source\":\"" +
           SourceLiteral(source) + "\",\"paths\":[]}";
}

std::string BuildSelectEnvelopeJson(ActivationSource source, const std::wstring& absolutePath, std::string* outId) {
    std::string id = NewLowercaseUuid();
    if (outId) *outId = id;

    std::string escapedPath = JsonEscape(Utf16ToUtf8(absolutePath));
    return std::string("{\"v\":1,\"id\":\"") + id + "\",\"action\":\"select\",\"source\":\"" +
           SourceLiteral(source) + "\",\"paths\":[\"" + escapedPath + "\"]}";
}

bool ParseAckAccepted(const std::string& json) {
    const std::string needle = "\"accepted\"";
    auto pos = json.find(needle);
    if (pos == std::string::npos) return false;

    auto colon = json.find(':', pos + needle.size());
    if (colon == std::string::npos) return false;

    auto truePos = json.find("true", colon);
    auto falsePos = json.find("false", colon);
    auto commaOrBrace = json.find_first_of(",}", colon);
    if (commaOrBrace == std::string::npos) return false;

    return truePos != std::string::npos && truePos < commaOrBrace &&
           (falsePos == std::string::npos || falsePos > commaOrBrace);
}

}  // namespace belowzero
