cmake_minimum_required(VERSION 3.22.1)

project(adbcontrol_quic LANGUAGES CXX)

add_library(adbcontrol_quic SHARED
    adbcontrol_quic_jni.cpp
)

target_compile_features(adbcontrol_quic PRIVATE cxx_std_17)
target_compile_options(adbcontrol_quic PRIVATE -Wall -Wextra -Werror)

target_link_libraries(adbcontrol_quic
    android
    log
)
