#include <jni.h>

#include <android/log.h>

#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

namespace {

constexpr const char* kLogTag = "ADBControlQuic";

struct NativeQuicHandle {
    std::string endpoint;
    std::string certificate_fingerprint_sha256;
};

std::mutex g_handles_mutex;
std::unordered_map<std::int64_t, NativeQuicHandle> g_handles;
std::int64_t g_next_handle = 1;

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

NativeQuicHandle* FindHandle(std::int64_t handle) {
    auto iterator = g_handles.find(handle);
    if (iterator == g_handles.end()) {
        return nullptr;
    }
    return &iterator->second;
}

bool IsQuicEndpoint(const std::string& endpoint) {
    return endpoint.rfind("quic://", 0) == 0;
}

std::string StableDevelopmentFingerprint(const std::string& endpoint) {
    // 这是开发期 native bridge 的稳定指纹占位，不代表真实 TLS 证书指纹。
    // 真正接入 MsQuic/quiche 后，这里必须替换为 native QUIC 证书 DER 的 SHA-256。
    std::uint64_t hash = 1469598103934665603ULL;
    for (char ch : endpoint) {
        hash ^= static_cast<unsigned char>(ch);
        hash *= 1099511628211ULL;
    }
    char buffer[65] = {};
    snprintf(buffer, sizeof(buffer), "%016llx%016llx%016llx%016llx",
             static_cast<unsigned long long>(hash),
             static_cast<unsigned long long>(hash ^ 0x9e3779b97f4a7c15ULL),
             static_cast<unsigned long long>(hash ^ 0xc2b2ae3d27d4eb4fULL),
             static_cast<unsigned long long>(hash ^ 0x165667b19e3779f9ULL));
    return std::string(buffer);
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

    __android_log_print(ANDROID_LOG_INFO, kLogTag,
                        "Creating native QUIC bridge handle for endpoint: %s",
                        endpoint.c_str());

    std::lock_guard<std::mutex> lock(g_handles_mutex);
    const std::int64_t handle = g_next_handle++;
    g_handles.emplace(handle, NativeQuicHandle{
        endpoint,
        StableDevelopmentFingerprint(endpoint),
    });
    return static_cast<jlong>(handle);
}

extern "C" JNIEXPORT void JNICALL
Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeSend(
    JNIEnv* env,
    jobject /* thiz */,
    jlong handle_value,
    jstring message_id_value,
    jstring channel_value,
    jstring kind_value,
    jstring payload_value) {
    const auto handle = static_cast<std::int64_t>(handle_value);
    const std::string message_id = ToString(env, message_id_value);
    const std::string channel = ToString(env, channel_value);
    const std::string kind = ToString(env, kind_value);
    const std::string payload = ToString(env, payload_value);

    std::lock_guard<std::mutex> lock(g_handles_mutex);
    NativeQuicHandle* native_handle = FindHandle(handle);
    if (native_handle == nullptr) {
        ThrowIllegalState(env, "Native QUIC handle is closed or invalid.");
        return;
    }

    __android_log_print(ANDROID_LOG_INFO, kLogTag,
                        "Queued native QUIC envelope handle=%lld endpoint=%s messageId=%s channel=%s kind=%s payloadBytes=%zu",
                        static_cast<long long>(handle),
                        native_handle->endpoint.c_str(),
                        message_id.c_str(),
                        channel.c_str(),
                        kind.c_str(),
                        payload.size());
}

extern "C" JNIEXPORT void JNICALL
Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeClose(
    JNIEnv* /* env */,
    jobject /* thiz */,
    jlong handle_value) {
    const auto handle = static_cast<std::int64_t>(handle_value);
    std::lock_guard<std::mutex> lock(g_handles_mutex);
    g_handles.erase(handle);
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_adbcontrol_companion_quic_JniNativeQuicEngine_nativeCertificateFingerprintSha256(
    JNIEnv* env,
    jobject /* thiz */,
    jlong handle_value) {
    const auto handle = static_cast<std::int64_t>(handle_value);
    std::lock_guard<std::mutex> lock(g_handles_mutex);
    NativeQuicHandle* native_handle = FindHandle(handle);
    if (native_handle == nullptr) {
        ThrowIllegalState(env, "Native QUIC handle is closed or invalid.");
        return nullptr;
    }
    return env->NewStringUTF(native_handle->certificate_fingerprint_sha256.c_str());
}
