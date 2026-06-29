#include <jni.h>
#include <android/log.h>
#include <msquic.h>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
constexpr auto TAG = "ADBControlQuic";
const QUIC_API_TABLE* MsQuic = nullptr;

void Throw(JNIEnv* env, const char* type, const std::string& message) {
    jclass clazz = env->FindClass(type);
    env->ThrowNew(clazz, message.c_str());
}

void CheckStatus(JNIEnv* env, QUIC_STATUS status, const std::string& action) {
    if (QUIC_FAILED(status)) {
        Throw(env, "java/io/IOException", action + " failed with QUIC_STATUS=" + std::to_string(status));
    }
}

std::string JStringToString(JNIEnv* env, jstring value) {
    const char* chars = env->GetStringUTFChars(value, nullptr);
    std::string result(chars == nullptr ? "" : chars);
    env->ReleaseStringUTFChars(value, chars);
    return result;
}

struct QuicClient {
    HQUIC registration = nullptr;
    HQUIC configuration = nullptr;
    HQUIC connection = nullptr;
    std::mutex sendMutex;

    explicit QuicClient(const std::string& alpn) {
        QUIC_REGISTRATION_CONFIG regConfig{ "adbcontrol-android", QUIC_EXECUTION_PROFILE_LOW_LATENCY };
        QUIC_STATUS status = MsQuic->RegistrationOpen(&regConfig, &registration);
        if (QUIC_FAILED(status)) throw std::runtime_error("RegistrationOpen failed");

        QUIC_BUFFER alpnBuffer{
            static_cast<uint32_t>(alpn.size()),
            reinterpret_cast<uint8_t*>(const_cast<char*>(alpn.data()))
        };
        status = MsQuic->ConfigurationOpen(registration, &alpnBuffer, 1, nullptr, 0, nullptr, &configuration);
        if (QUIC_FAILED(status)) throw std::runtime_error("ConfigurationOpen failed");

        QUIC_CREDENTIAL_CONFIG credConfig{};
        credConfig.Type = QUIC_CREDENTIAL_TYPE_NONE;
        credConfig.Flags = QUIC_CREDENTIAL_FLAG_CLIENT;
        status = MsQuic->ConfigurationLoadCredential(configuration, &credConfig);
        if (QUIC_FAILED(status)) throw std::runtime_error("ConfigurationLoadCredential failed");
    }

    ~QuicClient() {
        if (connection != nullptr) MsQuic->ConnectionClose(connection);
        if (configuration != nullptr) MsQuic->ConfigurationClose(configuration);
        if (registration != nullptr) MsQuic->RegistrationClose(registration);
    }
};

_IRQL_requires_max_(DISPATCH_LEVEL)
_Function_class_(QUIC_CONNECTION_CALLBACK)
QUIC_STATUS QUIC_API ConnectionCallback(HQUIC, void*, QUIC_CONNECTION_EVENT* event) {
    __android_log_print(ANDROID_LOG_DEBUG, TAG, "connection event=%d", event->Type);
    return QUIC_STATUS_SUCCESS;
}

_IRQL_requires_max_(DISPATCH_LEVEL)
_Function_class_(QUIC_STREAM_CALLBACK)
QUIC_STATUS QUIC_API StreamCallback(HQUIC stream, void*, QUIC_STREAM_EVENT* event) {
    if (event->Type == QUIC_STREAM_EVENT_SEND_COMPLETE) {
        MsQuic->StreamClose(stream);
    }
    return QUIC_STATUS_SUCCESS;
}

QuicClient* Ptr(jlong handle) {
    return reinterpret_cast<QuicClient*>(handle);
}

void SendBytes(JNIEnv* env, QuicClient* client, jbyteArray bytes, bool endOfStream) {
    if (client == nullptr || client->connection == nullptr) {
        Throw(env, "java/lang/IllegalStateException", "QUIC connection is not established");
        return;
    }

    const jsize size = env->GetArrayLength(bytes);
    std::vector<uint8_t> payload(static_cast<size_t>(size));
    env->GetByteArrayRegion(bytes, 0, size, reinterpret_cast<jbyte*>(payload.data()));

    HQUIC stream = nullptr;
    QUIC_STATUS status = MsQuic->StreamOpen(client->connection, QUIC_STREAM_OPEN_FLAG_NONE, StreamCallback, nullptr, &stream);
    CheckStatus(env, status, "StreamOpen");
    if (env->ExceptionCheck()) return;

    status = MsQuic->StreamStart(stream, QUIC_STREAM_START_FLAG_IMMEDIATE);
    CheckStatus(env, status, "StreamStart");
    if (env->ExceptionCheck()) return;

    QUIC_BUFFER buffer{ static_cast<uint32_t>(payload.size()), payload.data() };
    auto* heapBuffer = new QUIC_BUFFER(buffer);
    status = MsQuic->StreamSend(
        stream,
        heapBuffer,
        1,
        endOfStream ? QUIC_SEND_FLAG_FIN : QUIC_SEND_FLAG_NONE,
        heapBuffer
    );
    CheckStatus(env, status, "StreamSend");
}
} // namespace

extern "C" JNIEXPORT jlong JNICALL
Java_com_adbcontrol_app_transport_NativeQuicEngine_nativeCreateClient(JNIEnv* env, jobject, jstring alpn) {
    if (MsQuic == nullptr) {
        CheckStatus(env, MsQuicOpen2(&MsQuic), "MsQuicOpen2");
        if (env->ExceptionCheck()) return 0;
    }

    try {
        auto client = std::make_unique<QuicClient>(JStringToString(env, alpn));
        return reinterpret_cast<jlong>(client.release());
    } catch (const std::exception& error) {
        Throw(env, "java/io/IOException", error.what());
        return 0;
    }
}

extern "C" JNIEXPORT void JNICALL
Java_com_adbcontrol_app_transport_NativeQuicEngine_nativeConnect(JNIEnv* env, jobject, jlong handle, jstring host, jint port) {
    auto* client = Ptr(handle);
    if (client == nullptr) {
        Throw(env, "java/lang/IllegalStateException", "native handle is closed");
        return;
    }

    const std::string hostString = JStringToString(env, host);
    QUIC_STATUS status = MsQuic->ConnectionOpen(client->registration, ConnectionCallback, client, &client->connection);
    CheckStatus(env, status, "ConnectionOpen");
    if (env->ExceptionCheck()) return;

    status = MsQuic->ConnectionStart(
        client->connection,
        client->configuration,
        QUIC_ADDRESS_FAMILY_UNSPEC,
        hostString.c_str(),
        static_cast<uint16_t>(port)
    );
    CheckStatus(env, status, "ConnectionStart");
}

extern "C" JNIEXPORT void JNICALL
Java_com_adbcontrol_app_transport_NativeQuicEngine_nativeSendControlMessage(JNIEnv* env, jobject, jlong handle, jbyteArray bytes) {
    auto* client = Ptr(handle);
    std::scoped_lock lock(client->sendMutex);
    SendBytes(env, client, bytes, false);
}

extern "C" JNIEXPORT void JNICALL
Java_com_adbcontrol_app_transport_NativeQuicEngine_nativeSendStreamChunk(JNIEnv* env, jobject, jlong handle, jstring, jbyteArray bytes, jboolean endOfStream) {
    auto* client = Ptr(handle);
    std::scoped_lock lock(client->sendMutex);
    SendBytes(env, client, bytes, endOfStream == JNI_TRUE);
}

extern "C" JNIEXPORT void JNICALL
Java_com_adbcontrol_app_transport_NativeQuicEngine_nativeClose(JNIEnv*, jobject, jlong handle) {
    delete Ptr(handle);
}
