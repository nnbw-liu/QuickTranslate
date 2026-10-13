cmake_minimum_required(VERSION 3.20)
project(NativeCore LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(NativeCore SHARED
    NativeCore.cpp
    OcrCppWinrt.cpp
)

# ---- 定位 Windows SDK 的 cppwinrt 头文件目录 ----
set(_winsdk_base "C:/Program Files (x86)/Windows Kits/10/Include")
file(GLOB _winsdk_versions "${_winsdk_base}/*")
list(SORT _winsdk_versions ORDER DESCENDING)
set(CPPWINRT_INCLUDE_DIR "")
foreach(_v IN LISTS _winsdk_versions)
    if(EXISTS "${_v}/cppwinrt/winrt/Windows.Foundation.h")
        set(CPPWINRT_INCLUDE_DIR "${_v}/cppwinrt")
        break()
    endif()
endforeach()
if(NOT CPPWINRT_INCLUDE_DIR)
    message(FATAL_ERROR "Windows SDK cppwinrt headers not found under ${_winsdk_base}")
endif()
message(STATUS "cppwinrt include: ${CPPWINRT_INCLUDE_DIR}")

target_include_directories(NativeCore PRIVATE "${CPPWINRT_INCLUDE_DIR}")
target_compile_definitions(NativeCore PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(NativeCore PRIVATE /EHsc /O2 /W3 /Zc:__cplusplus /utf-8)
target_link_libraries(NativeCore PRIVATE user32.lib gdi32.lib)

# 输出到 build/bin/<Config>/NativeCore.dll
set_target_properties(NativeCore PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
