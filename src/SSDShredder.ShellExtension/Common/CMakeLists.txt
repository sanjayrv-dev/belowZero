add_library(ShellExtensionCommon STATIC
    Clsids.h
    PipeName.h
    PipeName.cpp
    ActivationEnvelope.h
    ActivationEnvelope.cpp
    ActivationClient.h
    ActivationClient.cpp
    UiLauncher.h
    UiLauncher.cpp
    Registration.h
    Registration.cpp
)

target_include_directories(ShellExtensionCommon PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

if(WIN32)
    target_link_libraries(ShellExtensionCommon PUBLIC bcrypt rpcrt4 shlwapi ole32 shell32)
endif()
