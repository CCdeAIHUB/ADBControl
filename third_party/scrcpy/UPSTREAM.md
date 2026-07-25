# scrcpy upstream

- Project: scrcpy
- Upstream: https://github.com/Genymobile/scrcpy
- Version: 4.0
- Commit: 2322868e9e256eb5fce0b3d659ab2a409f29bae1
- License: Apache License 2.0 (see `LICENSE`)

ADBControl vendors the scrcpy Android server source directly. The Android build compiles these classes into the ADBControl companion APK. When ADB is available, the desktop application uses the installed `base.apk` as the `app_process` classpath and consumes the server's video and control sockets inside the existing WinUI device preview. A temporary `/data/local/tmp` server artifact is retained only as a fallback when the companion APK is not installed. ADBControl does not launch or require the standalone scrcpy desktop application.

The vendored files under this directory retain their upstream copyright and license terms. ADBControl-specific C# integration code lives outside this directory.
