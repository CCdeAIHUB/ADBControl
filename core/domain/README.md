# Core domain skeleton

This folder contains a small executable domain model for the first ADBControl Core boundary.

It is intentionally transport-free:

- no fake QUIC engine;
- no HTTP/3/Cronet fallback;
- no direct device-control side effect;
- no Android JNI implementation placeholder pretending to be native code.

The goal is to make the locked behavior testable before platform-specific adapters are added.
