#pragma once
#include <string>

namespace belowzero {

// "action=open|select, source=desktop|explorer|terminal" per
// docs/02_SYSTEM_ARCHITECTURE.md ("UI activation envelope"). Native callers
// only ever produce "desktop" (DesktopBroker's IDropTarget / default launch)
// or "explorer" (ExplorerCommand::Invoke) envelopes; "terminal" belongs to
// Engineer 2's bzo.exe.
enum class ActivationSource { Desktop, Explorer };

// Builds the exact UTF-8 JSON line (no trailing newline) for an "open"
// activation: {"v":1,"id":"<uuid>","action":"open","source":"...","paths":[]}
std::string BuildOpenEnvelopeJson(ActivationSource source, std::string* outId);

// Builds the exact UTF-8 JSON line for a "select" activation with exactly
// one absolute local path. The path is JSON-escaped (backslashes and quotes)
// since Windows paths contain backslashes.
std::string BuildSelectEnvelopeJson(ActivationSource source, const std::wstring& absolutePath, std::string* outId);

// Scans a UTF-8 JSON line for {"...,"accepted":true/false,...}. Returns
// false (never accepted) if the field is missing or the line is malformed —
// never crash on a reply the server shouldn't be sending in the first place.
bool ParseAckAccepted(const std::string& json);

}  // namespace belowzero
