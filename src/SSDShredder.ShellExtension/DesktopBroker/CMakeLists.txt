if(WIN32)
    add_executable(SSDShredder.DesktopBroker WIN32
        DesktopBroker.cpp
        DesktopBroker.rc
    )
    target_link_libraries(SSDShredder.DesktopBroker PRIVATE ShellExtensionCommon ole32 uuid shell32)
    # The manifest is embedded by DesktopBroker.rc. Stop the linker adding a second one (CVT1100).
    target_link_options(SSDShredder.DesktopBroker PRIVATE /MANIFEST:NO)
    set_target_properties(SSDShredder.DesktopBroker PROPERTIES
        OUTPUT_NAME "SSDShredder.DesktopBroker"
    )
endif()
