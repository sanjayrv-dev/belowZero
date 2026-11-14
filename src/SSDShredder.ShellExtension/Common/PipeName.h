#pragma once
#include <string>

namespace belowzero {

// Mirrors SSDShredder.UI/Activation/ActivationRouter.ResolvePipeName exactly:
// a short fixed prefix plus the first 10 lowercase-hex characters of
// SHA-256(current user name). Kept in sync by hand across the two languages
// because there is no shared-contract build step yet (see
// docs/02_SYSTEM_ARCHITECTURE.md, "UI activation envelope"); if either side's
// algorithm changes, update the other and re-run both test suites.
//
// Returns just the pipe's short name, e.g. "bzo-ar1-3f9a2b7c1d" — callers
// prepend \\.\pipe\ for CreateFileW / CreateNamedPipeW.
std::wstring BuildActivationPipeName();

}  // namespace belowzero
