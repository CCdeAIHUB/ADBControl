# FFmpeg runtime notice

ADBControl's Windows desktop video decoder uses these NuGet packages:

- `FFmpeg.AutoGen` 7.1.1: managed FFmpeg bindings, MIT license.
- `Sdcb.FFmpeg.runtime.windows-x64` 7.1.0: Windows x64 FFmpeg native runtime, package license expression `GPL-3.0-only`.

The native runtime DLLs are copied into the desktop output under `ffmpeg/` during build. Distributing those binaries requires compliance with GPL-3.0-only and all applicable FFmpeg component licenses.

Upstream and package sources:

- https://github.com/Ruslan-B/FFmpeg.AutoGen
- https://github.com/sdcb/FFmpeg.AutoGen
- https://ffmpeg.org/
- https://www.nuget.org/packages/FFmpeg.AutoGen/7.1.1
- https://www.nuget.org/packages/Sdcb.FFmpeg.runtime.windows-x64/7.1.0
