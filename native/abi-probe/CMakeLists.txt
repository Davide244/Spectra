cmake_minimum_required(VERSION 3.20)
project(box3d_abi_probe C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# The probe links nothing: it only needs the headers and one call into the
# library for the version line. Building it against the SAME source tree and the
# SAME precision flag as box3d.dll is the whole point — a manifest generated
# against different headers would certify a layout nobody runs.
set(BOX3D_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../external/box3d" CACHE PATH "Box3D submodule root")

if(NOT EXISTS "${BOX3D_ROOT}/include/box3d/box3d.h")
    message(FATAL_ERROR
        "Box3D headers not found under '${BOX3D_ROOT}'. "
        "The submodule is not checked out — run: git submodule update --init --recursive")
endif()

add_executable(abi_probe abi_probe.c)
target_include_directories(abi_probe PRIVATE "${BOX3D_ROOT}/include")

# Import the already-built import library rather than rebuilding the world.
find_library(BOX3D_IMPORT_LIB
    NAMES box3d
    PATHS "${BOX3D_BUILD_DIR}/src/Release" "${BOX3D_BUILD_DIR}/src/Debug" "${BOX3D_BUILD_DIR}/src"
    NO_DEFAULT_PATH)

if(NOT BOX3D_IMPORT_LIB)
    message(FATAL_ERROR
        "box3d import library not found under '${BOX3D_BUILD_DIR}'. Build box3d first.")
endif()

target_link_libraries(abi_probe PRIVATE "${BOX3D_IMPORT_LIB}")
