#pragma once
#include <string>

namespace belowzero {

// "Start the installed UI if absent and wait for an acknowledgement"
// (docs/02_SYSTEM_ARCHITECTURE.md, ActivationRouter). Tries the activation
// pipe first (cheap, covers the common case where the UI is already
// running); only if that fails does it resolve and launch the installed
// SSDShredder.UI executable, then retries with a longer timeout while it
// starts up and opens its own end of the pipe.
//
// Returns true only once some UI instance has acknowledged envelopeJson.
bool SendActivationStartingUiIfNeeded(const std::string& envelopeJson);

// Resolves the installed SSDShredder.UI executable's path. Installer-owned
// in the finished product (expected under HKCU\Software\belowZero\InstallDir
// or an equivalent App Paths entry); falls back to a sibling-directory guess
// next to this broker binary so the scaffold is exercisable before an
// installer exists. Returns an empty string if neither resolves.
std::wstring ResolveInstalledUiExecutablePath();

}  // namespace belowzero
