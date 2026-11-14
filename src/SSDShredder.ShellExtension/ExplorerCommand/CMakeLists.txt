if(WIN32)
    add_library(SSDShredder.ExplorerCommand SHARED
        ExplorerCommand.cpp
        ExplorerCommand.def
    )
    target_link_libraries(SSDShredder.ExplorerCommand PRIVATE ShellExtensionCommon ole32 shell32 shlwapi)
    set_target_properties(SSDShredder.ExplorerCommand PROPERTIES
        OUTPUT_NAME "SSDShredder.ExplorerCommand"
    )
endif()
