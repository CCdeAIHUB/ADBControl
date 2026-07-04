package com.adbcontrol.companion.features

import android.content.Context
import com.adbcontrol.companion.core.CompanionCommandContext
import com.adbcontrol.companion.core.CompanionCommandResult
import java.io.File

class FileSandboxFeatureHandler(private val context: Context) : FeatureCommandHandler {
    override val capabilityIds: Set<String> = setOf("android.file.read", "android.file.write")
    override val operations: Set<String> = setOf("file.read", "file.write")

    override fun handle(context: CompanionCommandContext): CompanionCommandResult {
        return when (context.operation) {
            "file.read" -> readText(context)
            "file.write" -> writeText(context)
            else -> CompanionCommandResult.failure(
                requestId = context.requestId,
                errorCode = "COMPANION_OPERATION_NOT_SUPPORTED",
                message = "不支持的文件操作：${context.operation}",
                module = "companion.file",
                recoverable = false,
            )
        }
    }

    private fun readText(command: CompanionCommandContext): CompanionCommandResult {
        val file = resolveSandboxFile(command)
            ?: return invalidPath(command.requestId)
        if (!file.exists() || !file.isFile) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_FILE_NOT_FOUND",
                message = "沙盒文件不存在：${file.name}",
                module = "companion.file",
                recoverable = true,
            )
        }

        val maxBytes = (command.args.intArg("maxBytes") ?: 1024 * 1024).coerceIn(1, 4 * 1024 * 1024)
        if (file.length() > maxBytes) {
            return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_FILE_TOO_LARGE",
                message = "文件大小超过了请求的 maxBytes 限制。",
                module = "companion.file",
                recoverable = true,
                suggestion = "较大文件请使用底层流式文件传输操作。",
            )
        }

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "path" to file.relativeTo(context.filesDir).path,
                "sizeBytes" to file.length(),
                "text" to file.readText(),
            ),
        )
    }

    private fun writeText(command: CompanionCommandContext): CompanionCommandResult {
        val file = resolveSandboxFile(command)
            ?: return invalidPath(command.requestId)
        val text = command.args.stringArg("text")
            ?: return CompanionCommandResult.failure(
                requestId = command.requestId,
                errorCode = "COMPANION_PARAMS_INVALID",
                message = "file.write 需要 args.text 参数。",
                module = "companion.file",
                recoverable = false,
            )

        file.parentFile?.mkdirs()
        file.writeText(text)

        return CompanionCommandResult.success(
            requestId = command.requestId,
            result = mapOf(
                "path" to file.relativeTo(context.filesDir).path,
                "sizeBytes" to file.length(),
                "written" to true,
            ),
        )
    }

    private fun resolveSandboxFile(command: CompanionCommandContext): File? {
        val rawPath = command.args.stringArg("path") ?: return null
        val root = context.filesDir.canonicalFile
        val candidate = File(root, rawPath).canonicalFile

        // File capability is functional in the app sandbox first. External storage
        // and document-tree access must be routed through a later user-granted URI
        // provider so Core cannot cause arbitrary filesystem access by path alone.
        return if (candidate.path.startsWith(root.path)) candidate else null
    }

    private fun invalidPath(requestId: String): CompanionCommandResult {
        return CompanionCommandResult.failure(
            requestId = requestId,
            errorCode = "COMPANION_FILE_PATH_INVALID",
            message = "文件路径必须是伴侣 App 沙盒内的非空相对路径。",
            module = "companion.file",
            recoverable = false,
        )
    }
}
