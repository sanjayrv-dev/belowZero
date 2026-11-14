#pragma once
#include <windows.h>
#include <string>

namespace belowzero {

// Speaks the same framing as SSDShredder.UI.Activation.ActivationRouter:
// one UTF-8 JSON object per line (payload + '\n', flushed), capped at 65,536
// bytes excluding the newline. Connects to the per-user pipe
// BuildActivationPipeName() names, waits briefly for the UI to be listening,
// sends one envelope, and returns whether the UI acknowledged it with
// accepted=true for the same request id embedded in envelopeJson.
//
// Returns false if no owner is listening within the timeout, the pipe
// rejects the connection, or the UI declines the activation — callers (the
// DesktopBroker) treat any false as "try starting/focusing the UI failed",
// never as license to act with elevated or destructive authority: this
// acknowledgement grants no mutation rights, matching the architecture spec.
bool SendActivationAndWaitForAck(const std::string& envelopeJson, DWORD timeoutMillis = 3000);

}  // namespace belowzero
