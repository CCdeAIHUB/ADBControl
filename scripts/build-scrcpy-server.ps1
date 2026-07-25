param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$scrcpyRoot = Join-Path $repositoryRoot "third_party\scrcpy"
$gradleTask = if ($Configuration -eq "Debug") { ":server:assembleDebug" } else { ":server:assembleRelease" }

if (-not $env:JAVA_HOME) {
    $androidStudioJdk = "C:\Program Files\Android\Android Studio\jbr"
    if (Test-Path (Join-Path $androidStudioJdk "bin\java.exe")) {
        $env:JAVA_HOME = $androidStudioJdk
    }
}

if (-not $env:ANDROID_HOME) {
    $defaultSdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
    if (Test-Path $defaultSdk) {
        $env:ANDROID_HOME = $defaultSdk
        $env:ANDROID_SDK_ROOT = $defaultSdk
    }
}

Push-Location $scrcpyRoot
try {
    & (Join-Path $scrcpyRoot "gradlew.bat") $gradleTask
    if ($LASTEXITCODE -ne 0) {
        throw "scrcpy server build failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
