cmake_minimum_required(VERSION 3.22.1)
project(adbcontrol_quic LANGUAGES CXX)

find_path(MSQUIC_INCLUDE_DIR msquic.h REQUIRED)
find_library(MSQUIC_LIBRARY msquic REQUIRED)

add_library(adbcontrol_quic SHARED native_quic_engine.cpp)
target_include_directories(adbcontrol_quic PRIVATE ${MSQUIC_INCLUDE_DIR})
target_link_libraries(adbcontrol_quic PRIVATE ${MSQUIC_LIBRARY} log)
target_compile_features(adbcontrol_quic PRIVATE cxx_std_20)
