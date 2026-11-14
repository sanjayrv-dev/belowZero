if(WIN32)
    add_executable(SSDShredder.DesktopBroker WIN32
        DesktopBroker.cpp
        DesktopBroker.rc
    )
    target_link_libraries(SSDShredder.DesktopBroker PRIVATE ShellExtensionCommon ole32 uuid shell32)
    set_target_properties(SSDShredder.DesktopBroker PROPERTIES
        OUTPUT_NAME "SSDShredder.DesktopBroker"
    )
endif()
