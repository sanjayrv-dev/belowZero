cmake_minimum_required(VERSION 3.24)
project(SSDShredder.ShellExtension LANGUAGES CXX)

if(NOT WIN32)
    message(WARNING
        "SSDShredder.ShellExtension targets Win32 COM (IDropTarget, IExplorerCommand, "
        "IShellLinkW) and native pipe security. Configuring here is only useful to "
        "look at the file layout; Engineer 3's own build/run loop is SSDShredder.UI.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Never request elevation from here. bzo/ActivationRouter runs the UI unelevated;
# the broker and the Explorer command DLL both run unelevated too (see
# docs/02_SYSTEM_ARCHITECTURE.md). Only the worker's own helper, launched later by
# Engineer 2's project, ever calls ShellExecuteExW(runas).
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)

add_subdirectory(Common)
add_subdirectory(DesktopBroker)
add_subdirectory(ExplorerCommand)
add_subdirectory(Installer)
