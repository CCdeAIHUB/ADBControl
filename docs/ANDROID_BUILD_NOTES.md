# Android build notes

The Android module is Kotlin with Gradle Kotlin DSL.

The native layer is linked through CMake and expects an Android ABI-compatible QUIC library plus headers to be supplied by the build environment. No Cronet or HTTP/3 fallback is used.

Required CMake inputs:

- `MSQUIC_INCLUDE_DIR`
- `MSQUIC_LIBRARY`

A build without those inputs should fail clearly rather than silently using a mock transport.
