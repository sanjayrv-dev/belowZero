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
# Sources are UTF-8 without a BOM (e.g. the em dash in the shortcut description); MSVC would
# otherwise read them as the ANSI code page and write mojibake.
if(MSVC)
    add_compile_options(/utf-8)
endif()

add_subdirectory(Common)
add_subdirectory(DesktopBroker)
add_subdirectory(ExplorerCommand)
add_subdirectory(Installer)

# Installer.cpp and UiLauncher.cpp locate siblings as <installer dir>/../SSDShredder.X/.
# Give every artifact the layout  out/<Config>/<TargetName>/  so those lookups resolve
# (the default Visual Studio layout is <Target>/<Config>/, which they do not match).
# Publish SSDShredder.UI into out/<Config>/SSDShredder.UI/ as well.
if(WIN32)
    foreach(tgt SSDShredder.DesktopBroker SSDShredder.ExplorerCommand SSDShredder.Installer)
        set_target_properties(${tgt} PROPERTIES
            RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out/$<CONFIG>/${tgt}")
    endforeach()
endif()
