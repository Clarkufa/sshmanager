cmake_minimum_required(VERSION 3.25)
project(SshManagerRdp LANGUAGES C CXX)

set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded$<$<CONFIG:Debug>:Debug>)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "")
set(WITH_SERVER OFF CACHE BOOL "")
set(WITH_CLIENT OFF CACHE BOOL "")
set(WITH_SAMPLE OFF CACHE BOOL "")
set(WITH_WINPR_TOOLS OFF CACHE BOOL "")
set(WITH_PROXY OFF CACHE BOOL "")
set(WITH_FFMPEG OFF CACHE BOOL "")
set(WITH_SWSCALE OFF CACHE BOOL "")
set(WITH_WEBVIEW OFF CACHE BOOL "")
set(WITH_OPENH264 OFF CACHE BOOL "")
set(CHANNEL_URBDRC OFF CACHE BOOL "" FORCE)
set(WITH_INTERNAL_MD4 ON CACHE BOOL "" FORCE)
set(WITH_INTERNAL_RC4 ON CACHE BOOL "" FORCE)
set(OPENSSL_USE_STATIC_LIBS ON)

add_subdirectory("${FREERDP_SOURCE}" freerdp EXCLUDE_FROM_ALL)
add_executable(SshManagerRdp worker.c)
target_compile_features(SshManagerRdp PRIVATE c_std_11)
# FreeRDP's Windows headers use this definition for static consumers as well.
target_compile_definitions(SshManagerRdp PRIVATE FREERDP_EXPORTS)
target_include_directories(SshManagerRdp PRIVATE
    "${FREERDP_SOURCE}/include" "${FREERDP_SOURCE}/winpr/include"
    "${CMAKE_CURRENT_BINARY_DIR}/freerdp/include"
    "${CMAKE_CURRENT_BINARY_DIR}/freerdp/winpr/include")
target_link_libraries(SshManagerRdp PRIVATE freerdp-client freerdp winpr)
