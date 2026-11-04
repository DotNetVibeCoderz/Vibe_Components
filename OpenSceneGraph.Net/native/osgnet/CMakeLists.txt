cmake_minimum_required(VERSION 3.16)
project(osgnet VERSION 0.1.0 LANGUAGES CXX)

# Path to an OpenSceneGraph install (include/, lib/, bin/). Built by native/build-osg.ps1.
set(OSG_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../third_party/osg-install" CACHE PATH "OpenSceneGraph install prefix")

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
find_package(OpenGL REQUIRED)

add_library(osgnet SHARED osgnet.cpp osgnet.h)
target_include_directories(osgnet PRIVATE "${OSG_ROOT}/include")
target_link_directories(osgnet PRIVATE "${OSG_ROOT}/lib")
target_link_libraries(osgnet PRIVATE osg osgDB osgUtil osgGA osgViewer OpenThreads OpenGL::GL)
if(MSVC)
  target_compile_definitions(osgnet PRIVATE _HAS_STD_BYTE=0 NOMINMAX)
  target_compile_options(osgnet PRIVATE /W3 /EHsc)
endif()

install(TARGETS osgnet RUNTIME DESTINATION bin LIBRARY DESTINATION lib)
