plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.adbcontrol.companion"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.adbcontrol.companion"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }
}

dependencies {
    implementation("org.chromium.net:cronet-embedded:143.7445.0")
}
