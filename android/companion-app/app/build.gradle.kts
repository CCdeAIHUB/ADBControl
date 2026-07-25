import org.gradle.api.tasks.Exec

plugins {
    id("com.android.application")
}

android {
    namespace = "com.adbcontrol.companion"
    compileSdk = 36
    ndkVersion = "27.2.12479018"

    defaultConfig {
        applicationId = "com.adbcontrol.companion"
        minSdk = 29
        targetSdk = 36
        versionCode = 11
        versionName = "0.11.0"

        ndk {
            abiFilters += listOf("arm64-v8a", "x86_64")
        }
    }

    buildFeatures {
        buildConfig = true
    }
}

dependencies {
    implementation(project(":scrcpy-server"))
}

val repositoryRoot = rootProject.projectDir.resolve("../..").canonicalFile
val nativeQuicManifest = repositoryRoot.resolve("crates/adbcontrol-quic-android/Cargo.toml")
val nativeQuicOutput = projectDir.resolve("src/main/jniLibs")
val androidSdkRoot = providers.environmentVariable("ANDROID_HOME").orNull
    ?: providers.environmentVariable("ANDROID_SDK_ROOT").orNull
    ?: file("${System.getProperty("user.home")}/AppData/Local/Android/Sdk").absolutePath
val androidNdkRoot = providers.environmentVariable("ANDROID_NDK_HOME").orNull
    ?: file("$androidSdkRoot/ndk/${android.ndkVersion}").absolutePath

val buildNativeQuic by tasks.registering(Exec::class) {
    group = "build"
    description = "Builds the Quinn Android JNI library for every packaged ABI."
    workingDir(repositoryRoot)
    environment("ANDROID_NDK_HOME", androidNdkRoot)
    commandLine(
        "cargo", "ndk",
        "-t", "arm64-v8a",
        "-t", "x86_64",
        "-P", "29",
        "-o", nativeQuicOutput.absolutePath,
        "--manifest-path", nativeQuicManifest.absolutePath,
        "build", "--release",
    )
    inputs.dir(repositoryRoot.resolve("crates/adbcontrol-quic-android/src"))
    inputs.file(nativeQuicManifest)
    outputs.file(nativeQuicOutput.resolve("arm64-v8a/libadbcontrol_quic.so"))
    outputs.file(nativeQuicOutput.resolve("x86_64/libadbcontrol_quic.so"))
}

tasks.named("preBuild").configure {
    dependsOn(buildNativeQuic)
}
