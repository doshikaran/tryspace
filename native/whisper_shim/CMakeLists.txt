# Builds libtryscape_whisper (shim + whisper.cpp + ggml, statically linked, CPU only) for one platform.
# Usage: scripts/build_whisper_plugin.sh (pins the whisper.cpp commit and copies outputs into the Unity project).
cmake_minimum_required(VERSION 3.20)
project(tryscape_whisper C CXX)

set(WHISPER_SOURCE_DIR "" CACHE PATH "whisper.cpp source tree (pinned commit)")
if(NOT EXISTS "${WHISPER_SOURCE_DIR}/include/whisper.h")
  message(FATAL_ERROR "WHISPER_SOURCE_DIR must point at a whisper.cpp checkout")
endif()

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(GGML_METAL OFF CACHE BOOL "" FORCE)
set(GGML_BLAS OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
add_subdirectory("${WHISPER_SOURCE_DIR}" whisper EXCLUDE_FROM_ALL)

add_library(tryscape_whisper SHARED tryscape_whisper.c)
target_link_libraries(tryscape_whisper PRIVATE whisper)
set_target_properties(tryscape_whisper PROPERTIES C_VISIBILITY_PRESET hidden C_STANDARD 11)
if(ANDROID)
  target_link_options(tryscape_whisper PRIVATE "-Wl,-z,max-page-size=16384" "-Wl,--gc-sections")
  target_link_libraries(tryscape_whisper PRIVATE log)
endif()
