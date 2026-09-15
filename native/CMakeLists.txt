cmake_minimum_required(VERSION 3.16)
project(geometryengine_native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# A static CRT, unlike manifold.dll: this library is small, and not needing the VC++
# redistributable means one fewer way for the fast path to silently fall back.
if(MSVC)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

include(FetchContent)

# Both dependencies are header-only, so they are fetched rather than built and nothing is
# installed alongside the output. Pinned: libigl's signed-distance overloads have moved between
# releases, and a floating dependency would turn an upstream rename into a mystery build break.
set(GE_EIGEN_TAG "3.4.0" CACHE STRING "Eigen tag to build against")
set(GE_LIBIGL_TAG "v2.5.0" CACHE STRING "libigl tag to build against")

FetchContent_Declare(
  eigen
  GIT_REPOSITORY https://gitlab.com/libeigen/eigen.git
  GIT_TAG ${GE_EIGEN_TAG}
  GIT_SHALLOW TRUE
)
FetchContent_GetProperties(eigen)
if(NOT eigen_POPULATED)
  # Eigen's own CMake installs targets and pulls in tests; only the headers are wanted.
  FetchContent_Populate(eigen)
endif()

FetchContent_Declare(
  libigl
  GIT_REPOSITORY https://github.com/libigl/libigl.git
  GIT_TAG ${GE_LIBIGL_TAG}
  GIT_SHALLOW TRUE
)
FetchContent_GetProperties(libigl)
if(NOT libigl_POPULATED)
  FetchContent_Populate(libigl)
endif()

add_library(geometryengine_native SHARED
  src/geometryengine_native.cpp
)

target_include_directories(geometryengine_native
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${eigen_SOURCE_DIR}
    ${libigl_SOURCE_DIR}/include
)

# IGL_STATIC_LIBRARY is deliberately NOT defined. libigl's headers pull in their own .cpp with
# an #ifndef on that name, so defining it at all - even to 0 - drops every implementation and
# leaves the link with undefined references to the templates.
target_compile_definitions(geometryengine_native
  PRIVATE
    GEOMETRYENGINE_NATIVE_BUILD
)

if(MSVC)
  # Eigen and libigl lean on both of these; without them MSVC miscompiles the expression
  # templates or refuses the header depth.
  target_compile_options(geometryengine_native PRIVATE /bigobj /permissive- /EHsc /O2)
else()
  target_compile_options(geometryengine_native PRIVATE -fno-fast-math)
endif()

find_package(Threads REQUIRED)
target_link_libraries(geometryengine_native PRIVATE Threads::Threads)

if(UNIX AND NOT APPLE)
  # manifoldc is resolved by name at runtime rather than linked; see src/manifold_dynamic.hpp.
  target_link_libraries(geometryengine_native PRIVATE ${CMAKE_DL_LIBS})
endif()

set_target_properties(geometryengine_native PROPERTIES
  OUTPUT_NAME geometryengine_native
  # No lib prefix on the unix build: the managed resolver probes one set of names, and a flat
  # "geometryengine_native.so" beside the assembly matches the Windows layout.
  PREFIX ""
)
