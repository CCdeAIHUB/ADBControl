import org.gradle.api.tasks.Exec

plugins {
    id("com.android.application")
}

dependencies {
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
}

android {
    namespace = "com.adbcontrol.remote"
    compileSdk = 36
    ndkVersion = "27.2.12479018"

    defaultConfig {
        applicationId = "com.adbcontrol.remote"
        minSdk = 29
        targetSdk = 36
        versionCode = 14
        versionName = "0.3.5"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
    }

    buildFeatures { buildConfig = true }
}

val nativeManifest = rootProject.projectDir.resolve("native/Cargo.toml")
val nativeOutput = projectDir.resolve("src/main/jniLibs")
val androidSdkRoot = providers.environmentVariable("ANDROID_HOME").orNull
    ?: providers.environmentVariable("ANDROID_SDK_ROOT").orNull
    ?: file("${System.getProperty("user.home")}/AppData/Local/Android/Sdk").absolutePath
val androidNdkRoot = providers.environmentVariable("ANDROID_NDK_HOME").orNull
    ?: file("$androidSdkRoot/ndk/${android.ndkVersion}").absolutePath

val buildNativeQuic by tasks.registering(Exec::class) {
    group = "build"
    description = "Builds the pinned-certificate QUIC client for Android."
    workingDir(rootProject.projectDir)
    environment("ANDROID_NDK_HOME", androidNdkRoot)
    commandLine(
        "cargo", "ndk", "-t", "arm64-v8a", "-t", "x86_64", "-P", "29",
        "-o", nativeOutput.absolutePath, "--manifest-path", nativeManifest.absolutePath,
        "build", "--release",
    )
    inputs.dir(rootProject.projectDir.resolve("native/src"))
    inputs.file(nativeManifest)
    outputs.file(nativeOutput.resolve("arm64-v8a/libadbcontrol_remote_quic.so"))
    outputs.file(nativeOutput.resolve("x86_64/libadbcontrol_remote_quic.so"))
}

tasks.named("preBuild").configure { dependsOn(buildNativeQuic) }

tasks.register<JavaExec>("testContracts") {
    dependsOn("compileDebugUnitTestKotlin")
    classpath = files(
        layout.buildDirectory.dir("intermediates/built_in_kotlinc/debugUnitTest/compileDebugUnitTestKotlin/classes"),
        layout.buildDirectory.dir("intermediates/built_in_kotlinc/debug/compileDebugKotlin/classes"),
    ) + configurations.getByName("debugUnitTestRuntimeClasspath").incoming.artifactView {
        attributes.attribute(org.gradle.api.attributes.Attribute.of("artifactType", String::class.java), "jar")
    }.files
    mainClass.set("com.adbcontrol.remote.ContractTests")
    // 契约测试是纯逻辑断言，用小堆 + 串行 GC，避免低内存机器上 JVM 虚拟空间预留失败。
    jvmArgs("-Xms16m", "-Xmx96m", "-XX:+UseSerialGC", "-Xss512k", "-XX:MaxMetaspaceSize=96m")
}
