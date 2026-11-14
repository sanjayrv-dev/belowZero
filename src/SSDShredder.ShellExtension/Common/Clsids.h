#pragma once
#include <windows.h>

namespace belowzero {

// Fixed, product-owned CLSIDs. Generated once for this scaffold with
// uuidgen; never regenerate them later — registration, activation and
// uninstall all depend on these staying stable across versions.

// DesktopBroker's DropTarget CLSID (App Paths "DropTarget" value / LocalServer32).
// A plain header-only inline constexpr GUID avoids the DEFINE_GUID/INITGUID
// single-definition dance across translation units.
inline constexpr GUID CLSID_BelowZeroDropTarget = {
    0x8f2b6c40, 0x5e0d, 0x4a7c, {0x9b, 0x3e, 0x7d, 0x41, 0x2c, 0x6a, 0x8e, 0x01}};
inline constexpr wchar_t kDropTargetClsidString[] = L"{8F2B6C40-5E0D-4A7C-9B3E-7D412C6A8E01}";

// SSDShredder.ExplorerCommand's IExplorerCommand CLSID.
inline constexpr GUID CLSID_BelowZeroExplorerCommand = {
    0xc1a9d472, 0x3b8e, 0x4f61, {0xae, 0x02, 0x5c, 0x9f, 0x3d, 0x71, 0xb0, 0x44}};
inline constexpr wchar_t kExplorerCommandClsidString[] = L"{C1A9D472-3B8E-4F61-AE02-5C9F3D71B044}";

inline constexpr wchar_t kExplorerVerbKey[] = L"belowZero.Shred";
inline constexpr wchar_t kExplorerVerbDisplayName[] = L"Securely dispose with belowZero";

}  // namespace belowzero
