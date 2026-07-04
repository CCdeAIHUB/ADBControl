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
        ThrowIllegalState(env, "原生 QUIC 引擎仅接受 quic:// 地址。");
        return 0;
    }

    __android_log_print(
        ANDROID_LOG_ERROR,
        kLogTag,
        "原生 QUIC 后端未链接，连接地址：%s",
        endpoint.c_str());
    ThrowIllegalState(
        env,
        "ADBCONTROL_NATIVE_QUIC_BACKEND_NOT_LINKED：JNI 桥接层已编译，"
        "但尚未链接 MsQuic 或 quiche 等真实 IETF QUIC 后端，"
        "因此拒绝伪造 QUIC 连接。");
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
    ThrowIllegalState(env, "原生 QUIC 后端未链接，无法发送消息。");
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
    ThrowIllegalState(env, "原生 QUIC 后端未链接，无法获取证书指纹。");
    return nullptr;
}
