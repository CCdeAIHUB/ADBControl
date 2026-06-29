#include <jni.h>

#include <android/log.h>

#include <string>

namespace {

constexpr const char* kLogTag = "ADBControlQuic";

std::string ToString(JNIEnv* env, jstring value) {
    if (value == nullptr) {
        return {};
    }
    const char* chars = env->GetStringUTFChars(value, nullptr);
    if (chars == nullptr) {
        return {};
    }
    std::string result(chars);
    env->ReleaseStringUTFChars(value, chars);
    return result;
}

void ThrowIllegalState(JNIEnv* env, const std::string& message) {
    jclass klass = env->FindClass("java/lang/IllegalStateException");
    if (klass != nullptr) {
        env->ThrowNew(klass, message.c_str());
    }
}

bool IsQuicEndpoint(const std::string& endpoint) {
    return endpoint.rfind("quic://", 0) == 0;
}

}  // namespace

extern "C" JNIEXPORT jlong JNICALL
Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeConnect(
    JNIEnv* env,
    jobject /* thiz */,
    jstring endpoint_value) {
    const std::string endpoint = ToString(env, endpoint_value);
    if (!IsQuicEndpoint(endpoint)) {
        ThrowIllegalState(env, "Native QUIC engine only accepts quic:// endpoints.");
        return 0;
    }

    __android_log_print(
        ANDROID_LOG_ERROR,
        kLogTag,
        "Native QUIC backend is not linked for endpoint: %s",
        endpoint.c_str());
    ThrowIllegalState(
        env,
        "ADBCONTROL_NATIVE_QUIC_BACKEND_NOT_LINKED: JNI bridge is compiled, "
        "but no real IETF QUIC backend such as MsQuic or quiche is linked yet. "
        "Refusing to fake a QUIC connection.");
    return 0;
}

extern "C" JNIEXPORT void JNICALL
Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeSend(
    JNIEnv* env,
    jobject /* thiz */,
    jlong /* handle_value */,
    jstring /* message_id_value */,
    jstring /* channel_value */,
    jstring /* kind_value */,
    jstring /* payload_value */) {
    ThrowIllegalState(env, "Native QUIC backend is not linked; send is unavailable.");
}

extern "C" JNIEXPORT void JNICALL
Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeClose(
    JNIEnv* /* env */,
    jobject /* thiz */,
    jlong /* handle_value */) {
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeCertificateFingerprintSha256(
    JNIEnv* env,
    jobject /* thiz */,
    jlong /* handle_value */) {
    ThrowIllegalState(env, "Native QUIC backend is not linked; certificate fingerprint is unavailable.");
    return nullptr;
}
