package com.adbcontrol.remote

import com.adbcontrol.remote.data.adb.AppFrameParser
import com.adbcontrol.remote.data.adb.HardwareSnapshotData
import com.adbcontrol.remote.data.adb.LockStateParser
import com.adbcontrol.remote.data.adb.LsParser
import com.adbcontrol.remote.data.adb.PackageCatalogParser
import com.adbcontrol.remote.data.adb.SafeKeyboard
import com.adbcontrol.remote.data.adb.WmSizeParser
import com.adbcontrol.remote.data.ai.AiPolicies
import com.adbcontrol.remote.data.automation.ActionExecutor
import com.adbcontrol.remote.data.automation.AutomationValidator
import com.adbcontrol.remote.data.automation.ConditionEvaluator
import com.adbcontrol.remote.data.automation.CronExpression
import com.adbcontrol.remote.data.automation.ScheduleCalculator
import com.adbcontrol.remote.model.AiModelConfig
import com.adbcontrol.remote.model.AiPermissionMode
import com.adbcontrol.remote.model.AppState
import com.adbcontrol.remote.model.AppStateTransitions
import com.adbcontrol.remote.model.AutomationActionDefinition
import com.adbcontrol.remote.model.AutomationConditionDefinition
import com.adbcontrol.remote.model.AutomationConditionMode
import com.adbcontrol.remote.model.AutomationExecutionException
import com.adbcontrol.remote.model.AutomationTaskDefinition
import com.adbcontrol.remote.model.AutomationTriggerDefinition
import com.adbcontrol.remote.model.CoreProfile
import com.adbcontrol.remote.model.Session
import com.adbcontrol.remote.model.UserRole
import com.adbcontrol.remote.navigation.AccessPolicy
import com.adbcontrol.remote.security.OperationRisk
import com.adbcontrol.remote.security.RemotePathPolicy
import com.adbcontrol.remote.security.RiskPolicy
import com.adbcontrol.remote.transport.jniCertificateBytes
import java.time.ZoneId

object ContractTests {
    @JvmStatic
    fun main(args: Array<String>) {
        navigationAccessPolicyGatesAdminModules()
        firstLoginCannotSkipPasswordChange()
        destructiveDeviceCommandsRequireDangerConfirmation()
        terminalShellCommandRiskClassification()
        expiredSessionReturnsToLoginState()
        remoteFileDeletionProtectsStorageRoots()
        lockStateParserTwoStageRules()
        wmSizeParserPrefersOverride()
        lsParserHandlesColumnsAndSymlinks()
        packageCatalogParsingRules()
        hardwareSnapshotParsingRules()
        appFrameFpsWindowRules()
        scheduleCalculatorAndCronRules()
        conditionOperatorMatrix()
        conditionParsersMatchDesktop()
        actionIntentAndTokenRules()
        automationValidatorRules()
        safeKeyboardRules()
        aiPolicyRules()
        fingerprintPolicyRules()
        tofuConnectUsesNonNullEmptyCertificateArray()
    }

    // ---------- 导航与安全 ----------

    private fun navigationAccessPolicyGatesAdminModules() {
        // 场景：任务与账号管理仅管理员可见，设备与我的页对所有已登录角色开放。
        check(AccessPolicy.canSeeTasks(UserRole.ADMIN))
        check(!AccessPolicy.canSeeTasks(UserRole.USER))
        check(AccessPolicy.canSeeAccounts(UserRole.ADMIN))
        check(!AccessPolicy.canSeeAccounts(UserRole.USER))
        check(AccessPolicy.canSeeDevices(UserRole.USER))
        check(AccessPolicy.canSeeProfile(UserRole.USER))
    }

    private fun firstLoginCannotSkipPasswordChange() {
        // 场景：admin/admin 首次登录后只能改密，不能直接进入主界面。
        val profile = CoreProfile("quic://127.0.0.1:45921", "localhost", "cert")
        val login = AppState.NeedsLogin(profile)
        val session = Session("token", "admin", UserRole.ADMIN, emptySet(), true)
        check(AppStateTransitions.canTransition(login, AppState.MustChangePassword(profile, session)))
        check(!AppStateTransitions.canTransition(AppState.MustChangePassword(profile, session), AppState.Ready(profile, session)))
    }

    private fun destructiveDeviceCommandsRequireDangerConfirmation() {
        // 场景：重启、清数据、卸载等操作必须进入危险确认流程。
        check(RiskPolicy.classify("adb.exec", listOf("reboot")) == OperationRisk.DANGER)
        check(RiskPolicy.classify("adb.exec", listOf("shell", "pm", "clear")) == OperationRisk.DANGER)
        check(RiskPolicy.classify("device.list", emptyList()) == OperationRisk.NORMAL)
    }

    private fun terminalShellCommandRiskClassification() {
        // 场景：终端整行命令按首 token 与危险词分级；普通查询放行，安装类确认，破坏类危险。
        check(RiskPolicy.classify("ls -la /sdcard") == RiskPolicy.Decision.ALLOW)
        check(RiskPolicy.classify("getprop ro.build.version.release") == RiskPolicy.Decision.ALLOW)
        check(RiskPolicy.classify("pm list packages -3") == RiskPolicy.Decision.CONFIRM)
        check(RiskPolicy.classify("reboot bootloader") == RiskPolicy.Decision.DANGER)
        check(RiskPolicy.classify("rm -rf /sdcard/x") == RiskPolicy.Decision.DANGER)
        check(RiskPolicy.classify("settings put global airplane_mode_on 1") == RiskPolicy.Decision.DANGER)
    }

    private fun expiredSessionReturnsToLoginState() {
        // 场景：令牌过期必须先回到 NeedsLogin，再接受新会话，不能只替换页面。
        val profile = CoreProfile("quic://127.0.0.1:45921", "localhost", "cert")
        val ready = AppState.Ready(profile, Session("expired", "member", UserRole.USER, setOf("d1"), false))
        check(AppStateTransitions.canTransition(ready, AppState.NeedsLogin(profile)))
    }

    private fun remoteFileDeletionProtectsStorageRoots() {
        // 场景：递归删除不能以根目录、共享存储根目录或包含上跳段的路径为目标。
        check(!RemotePathPolicy.canRecursivelyDelete("/"))
        check(!RemotePathPolicy.canRecursivelyDelete("/sdcard/"))
        check(!RemotePathPolicy.canRecursivelyDelete("/storage/emulated/0"))
        check(!RemotePathPolicy.canRecursivelyDelete("/sdcard/../data"))
        check(RemotePathPolicy.canRecursivelyDelete("/sdcard/Download/demo"))
    }

    // ---------- ADB 解析 ----------

    private fun lockStateParserTwoStageRules() {
        // 场景：灭屏 → 锁定；keyguard 信号 true → 锁定；全 false → 未锁；无信号 → Unknown（触发二段降级）。
        check(LockStateParser.parse("mWakefulness=Asleep") == LockStateParser.LockState.Locked)
        check(LockStateParser.parse("Display Power: state=OFF") == LockStateParser.LockState.Locked)
        check(LockStateParser.parse("mKeyguardShowing=true") == LockStateParser.LockState.Locked)
        check(LockStateParser.parse("(current) deviceLocked=true") == LockStateParser.LockState.Locked)
        check(LockStateParser.parse("mKeyguardShowing=false") == LockStateParser.LockState.Unlocked)
        check(LockStateParser.parse("mWakefulness=Awake\nmKeyguardShowing=false") == LockStateParser.LockState.Unlocked)
        check(LockStateParser.parse("") == LockStateParser.LockState.Unknown)
        // OneUI 两段式：主查询 + 降级查询合并解析。
        check(
            LockStateParser.parseCombined("", "keyguardShowing=false") == LockStateParser.LockState.Unlocked,
        )
    }

    private fun wmSizeParserPrefersOverride() {
        // 场景：wm size 同时存在 Override 与 Physical 时按桌面端语义取 Override。
        val parsed = WmSizeParser.parse("Physical size: 1080x2400\nOverride size: 900x2000")
        check(parsed != null && parsed.width == 900 && parsed.height == 2000)
        val physical = WmSizeParser.parse("Physical size: 1080x2400")
        check(physical != null && physical.width == 1080 && physical.height == 2400)
        check(WmSizeParser.parse("garbage") == null)
    }

    private fun lsParserHandlesColumnsAndSymlinks() {
        // 场景：ls -la -p 的 9 列解析、目录优先排序、" -> " 符号链接截断、./.. 过滤。
        val output = """
            total 12
            drwxrwx--x 4 root sdcard 4096 Jan  1 10:00 .
            drwxrwx--x 4 root sdcard 4096 Jan  1 10:00 ..
            -rw-rw---- 1 root sdcard 1024 Jan  2 11:30 notes.txt
            lrwxrwxrwx 1 root root 18 Jan  1 09:00 link -> /data/other
            drwxrwx--- 2 root sdcard 4096 Jan  3 08:00 Pics
        """.trimIndent()
        val entries = LsParser.parse(output)
        check(entries.size == 3)
        check(entries.first().isDirectory && entries.first().name == "Pics")
        val link = entries.first { it.name == "link" }
        check(link.isSymlink && !link.name.contains("data/other"))
        check(entries.first { it.name == "notes.txt" }.sizeBytes == 1024L)
    }

    private fun packageCatalogParsingRules() {
        // 场景：package: 行解析去重；dumpsys 的 label/version/codePath 提取。
        val list = PackageCatalogParser.parsePackageList("package:com.a\npackage:com.b\npackage:com.a\n", system = true)
        check(list.size == 2 && list.all { it.isSystem })
        val info = PackageCatalogParser.parseDumpsys(
            """
            Package [com.a] (1234)
              versionName=2.5.1
              codePath=/data/app/~~x/com.a-base.apk
              application-label: 示例应用
            """.trimIndent(),
        )
        check(info.label == "示例应用")
        check(info.versionName == "2.5.1")
        check(info.apkPath.startsWith("/data/app"))
    }

    private fun hardwareSnapshotParsingRules() {
        // 场景：key=value 快照解析；温度 >200 视为毫度；GPU 频率 <10MHz 视为 kHz（桌面端同款规则）；
        // CPU 估算占用 = 当前/最高 频率均值。
        val snapshot = HardwareSnapshotData.parse(
            """
            brand=Xiaomi
            model=22612
            cpu_model=Snapdragon 8 Gen 1
            cpu_cores=8
            cpu_freqs=cpu0:1804800,cpu1:806400,
            cpu_max_freqs=cpu0:2841600,cpu1:2841600
            battery_level=66
            battery_status=2
            battery_temp=315
            mem_total_kb=8000000
            mem_available_kb=2000000
            swap_total_kb=4000000
            swap_free_kb=1000000
            thermal_service=CPU:61.5,BATTERY:30.0
            gpu_access=available
            gpu_usage=37.5
            gpu_cur_freq=305000
            gpu_max_freq=818000
            refresh_rate=120.0
            """.trimIndent(),
        )
        check(snapshot.cpuCores == 8)
        check(snapshot.cpuFreqs["cpu0"] == 1804800L)
        check(snapshot.batteryLevel == 66 && snapshot.batteryCharging)
        check(snapshot.memUsedPercent != null && snapshot.memUsedPercent!! > 70)
        check(snapshot.temperaturesCelsius.first { it.first == "CPU" }.second == 61.5)
        check(HardwareSnapshotData.normalizeGpuFrequency(305_000L) == 305_000_000L)
        check(HardwareSnapshotData.normalizeGpuFrequency(3_050_000_000L) == 3_050_000_000L)
        check(snapshot.cpuUsageEstimatePercent != null)
        check(snapshot.refreshRate == 120.0)
        // 空值不抛异常。
        check(HardwareSnapshotData.parse("").memTotalKb == 0L)
    }

    private fun appFrameFpsWindowRules() {
        // 场景：1 秒窗口帧率；时间戳不前进时 FPS=0；样本 <2 时 FPS=0（桌面端 CalculateAppFps 同款）。
        val base = 1_000_000_000_000L
        val times = listOf(base, base + 100_000_000, base + 250_000_000, base + 400_000_000)
        val ref = LongArray(1)
        val fps = AppFrameParser.fps(AppFrameParser.parseFrameTimes("app_frame_times=${times.joinToString(",")},"), ref)
        check(fps != null && fps > 0)
        check(AppFrameParser.fps(listOf(base), ref) == 0.0)
        check(AppFrameParser.fps(emptyList(), ref) == null)
    }

    // ---------- 自动化 ----------

    private fun scheduleCalculatorAndCronRules() {
        // 场景：interval 从锚点对齐；daily 计算下一次；cron 五段解析与非法输入显式失败。
        val zone = ZoneId.of("Asia/Shanghai")
        val anchor = java.time.ZonedDateTime.of(2026, 9, 26, 0, 0, 0, 0, zone).toInstant().toEpochMilli()
        val task = AutomationTaskDefinition(id = "t", name = "t", createdAtEpochMs = anchor, updatedAtEpochMs = anchor)
        val interval = AutomationTriggerDefinition(id = "i", type = "interval", intervalSeconds = 3600)
        val after = anchor + 90 * 60 * 1000
        val next = ScheduleCalculator.nextOccurrence(task, interval, after)
        check(next == anchor + 2 * 3600 * 1000L)

        val daily = AutomationTriggerDefinition(id = "d", type = "daily", at = "17:00", timeZoneId = "Asia/Shanghai")
        val morning = java.time.ZonedDateTime.of(2026, 9, 26, 8, 0, 0, 0, zone).toInstant().toEpochMilli()
        val dailyNext = ScheduleCalculator.nextOccurrence(task, daily, morning)
        check(
            dailyNext == java.time.ZonedDateTime.of(2026, 9, 26, 17, 0, 0, 0, zone).toInstant().toEpochMilli(),
        )
        val once = AutomationTriggerDefinition(id = "o", type = "once", runAtEpochMs = morning - 1)
        check(ScheduleCalculator.nextOccurrence(task, once, morning) == null)
        try {
            CronExpression.parse("*/61 * * *")
            error("非法 cron 必须失败")
        } catch (expected: AutomationExecutionException) {
            check(expected.errorCode == "SCHEDULE_CRON_INVALID")
        }
        val cron = CronExpression.parse("0 9 * * 1-5")
        val friday = java.time.ZonedDateTime.of(2026, 9, 25, 20, 0, 0, 0, zone).toInstant().toEpochMilli()
        val cronNext = CronExpression.nextOccurrence("0 9 * * 1-5", friday, zone)
        check(cronNext > friday)
        check(cron.dayOfWeek.contains(1))
    }

    private fun conditionOperatorMatrix() {
        // 场景：全部操作符语义 + 大小写 + 取反（桌面端 Compare 同款）。
        val cases = listOf(
            Triple("hello", "equals", "hello") to true,
            Triple("Hello", "equals", "hello") to true,
            Triple("Hello", "equals:cs", "hello") to false,
            Triple("abc123", "contains", "c12") to true,
            Triple("abc", "notcontains", "z") to true,
            Triple("adb.log", "startswith", "adb") to true,
            Triple("a.txt", "endswith", ".txt") to true,
            Triple("86", "greaterthan", "70") to true,
            Triple("86", "lessthan", "70") to false,
            Triple("wifi", "in", "wifi,cellular") to true,
            Triple("", "exists", "") to false,
            Triple("true", "truthy", "") to true,
            Triple("0", "truthy", "") to false,
            Triple("abc123", "regex", "c1[0-9]") to true,
        )
        cases.forEach { (input, expected) ->
            val (actual, op, value) = input
            val caseSensitive = op.endsWith(":cs")
            val operator = op.removeSuffix(":cs")
            val matched = ConditionEvaluator.compare(actual, operator, value, caseSensitive)
            check(matched == expected) { "operator $operator failed: $actual vs $value" }
        }
    }

    private fun conditionParsersMatchDesktop() {
        // 场景：条件解析器与桌面端语义一致。
        check(ConditionEvaluator.parseScreenOn("mWakefulness=Awake"))
        check(ConditionEvaluator.parseScreenOn("Display Power: state=ON"))
        check(!ConditionEvaluator.parseScreenOn("mWakefulness=Dozing"))
        check(ConditionEvaluator.parseLocked("mKeyguardShowing=true") == "true")
        check(ConditionEvaluator.parseLocked("nothing") == "unknown")
        check(ConditionEvaluator.parseOrientation("SurfaceOrientation: 1") == "landscape")
        check(ConditionEvaluator.parseNamedNumber("  level: 88\n status: 2", "level") == "88")
        check(ConditionEvaluator.parseCharging("status: 2"))
        check(ConditionEvaluator.parseCharging("AC powered: true"))
        check(ConditionEvaluator.parseNetworkType("TRANSPORT_WIFI") == "wifi")
        check(ConditionEvaluator.parseCallState("mCallState=1") == "ringing")
        check(ConditionEvaluator.isTruthy("true") && ConditionEvaluator.isTruthy("3.2"))
        check(!ConditionEvaluator.isTruthy("0"))
        check(ConditionEvaluator.formatTemperature("315") == "31.5")
        check(ConditionEvaluator.parseCompanionBoolean("{\"result\":{\"enabled\":false}}", "enabled") == "false")
        check(ConditionEvaluator.parseCompanionBoolean("data={\"enabled\":true}", "enabled") == "true")
    }

    private fun actionIntentAndTokenRules() {
        // 场景：intent.start 命令构建（--ez/--el/--es 类型化 extras）与 shell 单引号转义（桌面端同款）。
        val command = ActionExecutor.buildIntentCommand(
            mapOf("action" to "android.intent.action.VIEW", "data" to "https://example.com/a?b=1"),
            listOf(
                ActionExecutor.IntentExtra("ez", "flag", "true"),
                ActionExecutor.IntentExtra("el", "count", "3"),
                ActionExecutor.IntentExtra("es", "name", "it's"),
            ),
        )
        // 桌面端 BuildIntentCommand 对 -a/-d 同样做单引号转义。
        check(command.startsWith("am start -a 'android.intent.action.VIEW' -d 'https://example.com/a?b=1'"))
        check(command.contains("--ez 'flag' true"))
        check(command.contains("--el 'count' 3"))
        check(command.contains("'it'\\''s'"))
        check(ActionExecutor.shellToken("a'b") == "'a'\\''b'")
    }

    private fun automationValidatorRules() {
        // 场景：缺触发器/缺动作/非法 cron/未知动作/嵌套深度必须显式失败；合法任务通过。
        fun task(actions: List<AutomationActionDefinition>, triggers: List<AutomationTriggerDefinition>) =
            AutomationTaskDefinition(id = "t", name = "n", actions = actions, triggers = triggers, createdAtEpochMs = 0, updatedAtEpochMs = 0)

        fun expectInvalid(block: () -> Unit) {
            try {
                block()
                error("expected AUTOMATION_TASK_INVALID")
            } catch (error: AutomationExecutionException) {
                check(error.errorCode == "AUTOMATION_TASK_INVALID")
            }
        }

        AutomationValidator.validate(
            task(
                listOf(AutomationActionDefinition(id = "a", type = "log", parameters = mapOf("message" to "hi"))),
                listOf(AutomationTriggerDefinition(id = "t1", type = "manual")),
            ),
        )
        expectInvalid { AutomationValidator.validate(task(emptyList(), listOf(AutomationTriggerDefinition(id = "t", type = "manual")))) }
        expectInvalid { AutomationValidator.validate(task(listOf(AutomationActionDefinition(id = "a", type = "log")), emptyList())) }
        expectInvalid {
            AutomationValidator.validate(
                task(
                    listOf(AutomationActionDefinition(id = "a", type = "adb.shell", parameters = mapOf("command" to "ls"))),
                    listOf(AutomationTriggerDefinition(id = "t", type = "cron", cron = "bad cron")),
                ),
            )
        }
        expectInvalid {
            AutomationValidator.validate(
                task(
                    listOf(AutomationActionDefinition(id = "a", type = "nope.action")),
                    listOf(AutomationTriggerDefinition(id = "t", type = "manual")),
                ),
            )
        }
        // flow.if 缺条件必须失败。
        expectInvalid {
            AutomationValidator.validate(
                task(
                    listOf(AutomationActionDefinition(id = "a", type = "flow.if")),
                    listOf(AutomationTriggerDefinition(id = "t", type = "manual")),
                ),
            )
        }
    }

    // ---------- 输入与 AI ----------

    private fun safeKeyboardRules() {
        // 场景：特殊键映射与桌面一致；ASCII 逐字符转义；非 ASCII 拒绝。
        check(SafeKeyboard.keyeventCommand("backspace") == 67)
        check(SafeKeyboard.keyeventCommand("enter") == 66)
        check(SafeKeyboard.keyeventCommand("space") == 62)
        check(SafeKeyboard.printableCommand('a') == "a")
        check(SafeKeyboard.printableCommand('\'') == "'\\''")
        check(SafeKeyboard.printableCommand('中') == null)
    }

    private fun aiPolicyRules() {
        // 场景：只读前缀自动放行；破坏性命令拒绝；完全访问全放行；多模态校验；低风险伴侣操作。
        val readonly = AiPermissionMode.AUTO_READONLY
        check(AiPolicies.classifyShell("getprop ro.build.version.release", readonly) == AiPolicies.ShellDecision.ALLOW)
        check(AiPolicies.classifyShell("dumpsys battery", readonly) == AiPolicies.ShellDecision.ALLOW)
        check(AiPolicies.classifyShell("rm -rf /sdcard/x", readonly) == AiPolicies.ShellDecision.DENY)
        check(AiPolicies.classifyShell("pm clear com.a", readonly) == AiPolicies.ShellDecision.DENY)
        check(AiPolicies.classifyShell("input tap 1 2", readonly) == AiPolicies.ShellDecision.DENY)
        check(AiPolicies.classifyShell("am start -n x/.y", readonly) == AiPolicies.ShellDecision.CONFIRM)
        check(AiPolicies.classifyShell("rm -rf /", AiPermissionMode.FULL_ACCESS) == AiPolicies.ShellDecision.ALLOW)
        check(AiPolicies.classifyShell("ls", AiPermissionMode.APPROVE_EVERY) == AiPolicies.ShellDecision.CONFIRM)
        check(AiPolicies.isLowRiskCompanion("accessibility.status"))
        check(AiPolicies.isLowRiskCompanion("volume.get"))
        check(!AiPolicies.isLowRiskCompanion("sms.send"))
        // 场景：带图消息要求模型已通过多模态验证（AI_MODEL_MULTIMODAL_REQUIRED 的守门条件）。
        check(AiPolicies.multimodalRequired(hasImage = true, multimodalVerified = false))
        check(!AiPolicies.multimodalRequired(hasImage = true, multimodalVerified = true))
        check(!AiPolicies.multimodalRequired(hasImage = false, multimodalVerified = false))
    }

    private fun fingerprintPolicyRules() {
        // 场景：用户从 Core 日志复制带冒号/大小写混合的指纹必须可归一化；非法输入必须拒绝。
        val mixed = "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89"
        val normalized = com.adbcontrol.remote.security.FingerprintPolicy.normalize(mixed)
        check(normalized != null && normalized.length == 64 && normalized == normalized.lowercase())
        check(com.adbcontrol.remote.security.FingerprintPolicy.normalize(normalized) == normalized)
        check(com.adbcontrol.remote.security.FingerprintPolicy.normalize("not a fingerprint") == null)
        check(com.adbcontrol.remote.security.FingerprintPolicy.normalize("abcd") == null)
        check(com.adbcontrol.remote.security.FingerprintPolicy.normalize("") == null)
    }

    private fun tofuConnectUsesNonNullEmptyCertificateArray() {
        // 场景：首次 TOFU 连接没有预置证书时，JNI 仍必须收到非 null 的零长度数组；
        // 传 null 会让 Rust JByteArray 转换提前失败，网络握手根本不会开始。
        check(jniCertificateBytes(null).isEmpty())
        val pinned = byteArrayOf(1, 2, 3)
        check(jniCertificateBytes(pinned).contentEquals(pinned))
    }
}
