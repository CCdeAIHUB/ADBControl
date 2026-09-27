package com.adbcontrol.remote.data.automation

import com.adbcontrol.remote.model.AutomationExecutionException
import com.adbcontrol.remote.model.AutomationTaskDefinition
import com.adbcontrol.remote.model.AutomationTriggerDefinition
import java.time.DayOfWeek
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime

/**
 * 触发器下一次执行时间计算：语义与桌面端 `AutomationScheduleCalculator.cs` 一致。
 * 纯 java.time 实现（minSdk 29，java.time 原生可用），便于纯 JVM 契约测试。
 */
object ScheduleCalculator {

    fun nextOccurrence(
        task: AutomationTaskDefinition,
        trigger: AutomationTriggerDefinition,
        afterExclusiveEpochMs: Long,
    ): Long? = when (trigger.type.lowercase()) {
        "once" -> trigger.runAtEpochMs?.takeIf { it > afterExclusiveEpochMs }
        "daily" -> nextDaily(trigger, afterExclusiveEpochMs)
        "weekly" -> nextWeekly(trigger, afterExclusiveEpochMs)
        "interval" -> nextInterval(task.createdAtEpochMs, trigger.intervalSeconds, afterExclusiveEpochMs)
        "cron" -> CronExpression.nextOccurrence(trigger.cron.orEmpty(), afterExclusiveEpochMs, resolveZone(trigger))
        else -> null
    }

    fun resolveZone(trigger: AutomationTriggerDefinition): ZoneId = try {
        if (trigger.timeZoneId.isBlank()) ZoneId.systemDefault() else ZoneId.of(trigger.timeZoneId)
    } catch (_: Exception) {
        // 与桌面一致：无效时区是显式失败，不允许静默回落到本机时区。
        throw AutomationExecutionException(
            "SCHEDULE_TIME_ZONE_INVALID", "找不到有效时区：${trigger.timeZoneId}。", "automation.schedule",
        )
    }

    private fun nextDaily(trigger: AutomationTriggerDefinition, afterExclusive: Long): Long {
        val time = parseTime(trigger.at)
        val zone = resolveZone(trigger)
        var date = LocalDateTime.ofInstant(Instant.ofEpochMilli(afterExclusive), zone).toLocalDate()
        repeat(371) {
            val candidate = ZonedDateTime.of(date, time, zone).toInstant().toEpochMilli()
            if (candidate > afterExclusive) return candidate
            date = date.plusDays(1)
        }
        throw AutomationExecutionException("SCHEDULE_RANGE_EXCEEDED", "无法计算每天触发器的下一次时间。", "automation.schedule")
    }

    private fun nextWeekly(trigger: AutomationTriggerDefinition, afterExclusive: Long): Long {
        val time = parseTime(trigger.at)
        val zone = resolveZone(trigger)
        val days = trigger.days.map { DayOfWeek.of(it.coerceIn(1, 7)) }.toSet()
        var date = LocalDateTime.ofInstant(Instant.ofEpochMilli(afterExclusive), zone).toLocalDate()
        repeat(15) {
            if (date.dayOfWeek in days) {
                val candidate = ZonedDateTime.of(date, time, zone).toInstant().toEpochMilli()
                if (candidate > afterExclusive) return candidate
            }
            date = date.plusDays(1)
        }
        throw AutomationExecutionException("SCHEDULE_RANGE_EXCEEDED", "无法计算每周触发器的下一次时间。", "automation.schedule")
    }

    private fun nextInterval(anchorEpochMs: Long, intervalSeconds: Int, afterExclusive: Long): Long {
        val intervalMs = intervalSeconds.coerceAtLeast(1) * 1000L
        if (afterExclusive < anchorEpochMs) return anchorEpochMs + intervalMs
        val elapsed = afterExclusive - anchorEpochMs
        val intervals = elapsed / intervalMs + 1
        return anchorEpochMs + intervals * intervalMs
    }

    private fun parseTime(value: String?): LocalTime = try {
        LocalTime.parse(value.orEmpty().trim())
    } catch (_: Exception) {
        throw AutomationExecutionException("SCHEDULE_TIME_INVALID", "无效的时间：$value。", "automation.schedule")
    }
}

/** 5 段 Cron（minute hour day month day-of-week），解析与求值规则逐条对齐桌面端 `AutomationCronExpression`。 */
object CronExpression {

    fun nextOccurrence(expression: String, afterExclusiveEpochMs: Long, zone: ZoneId): Long {
        val fields = parse(expression)
        var candidate = LocalDateTime.ofInstant(Instant.ofEpochMilli(afterExclusiveEpochMs), zone)
            .let { LocalDateTime.of(it.year, it.month, it.dayOfMonth, it.hour, it.minute, 0) }
            .plusMinutes(1)
        val maximumMinutes = 60L * 24 * 366 * 3
        repeat(maximumMinutes.toInt()) {
            if (fields.minute.contains(candidate.minute) && fields.hour.contains(candidate.hour) &&
                fields.month.contains(candidate.monthValue)
            ) {
                val dayMatches = fields.day.contains(candidate.dayOfMonth)
                val weekdayMatches = fields.dayOfWeek.contains(candidate.dayOfWeek.value % 7) // JVM 7=周日 → cron 0
                val calendarMatches = when {
                    fields.day.wildcard && fields.dayOfWeek.wildcard -> true
                    fields.day.wildcard -> weekdayMatches
                    fields.dayOfWeek.wildcard -> dayMatches
                    else -> dayMatches || weekdayMatches
                }
                if (calendarMatches) {
                    val instant = ZonedDateTime.of(candidate, zone).toInstant().toEpochMilli()
                    if (instant > afterExclusiveEpochMs) return instant
                }
            }
            candidate = candidate.plusMinutes(1)
        }
        throw AutomationExecutionException("SCHEDULE_RANGE_EXCEEDED", "三年范围内没有找到下一次 Cron 执行时间。", "automation.schedule")
    }

    fun parse(value: String): Fields {
        val parts = value.trim().split(Regex("\\s+"))
        if (parts.size != 5) {
            throw AutomationExecutionException(
                "SCHEDULE_CRON_INVALID", "Cron 必须包含 minute hour day month day-of-week 五段。", "automation.schedule",
            )
        }
        return Fields(
            minute = Field.parse(parts[0], 0, 59, normalizeSunday = false),
            hour = Field.parse(parts[1], 0, 23, normalizeSunday = false),
            day = Field.parse(parts[2], 1, 31, normalizeSunday = false),
            month = Field.parse(parts[3], 1, 12, normalizeSunday = false),
            dayOfWeek = Field.parse(parts[4], 0, 7, normalizeSunday = true),
        )
    }

    data class Fields(
        val minute: Field,
        val hour: Field,
        val day: Field,
        val month: Field,
        val dayOfWeek: Field,
    )

    data class Field(private val values: Set<Int>, val wildcard: Boolean) {
        fun contains(value: Int) = value in values

        companion object {
            fun parse(text: String, minimum: Int, maximum: Int, normalizeSunday: Boolean): Field {
                val values = mutableSetOf<Int>()
                val wildcard = text == "*"
                for (item in text.split(',').map(String::trim).filter(String::isNotEmpty)) {
                    val rangeAndStep = item.split('/')
                    if (rangeAndStep.size > 2) {
                        throw cronFieldError(item)
                    }
                    val step = if (rangeAndStep.size == 2) {
                        rangeAndStep[1].toIntOrNull()?.takeIf { it >= 1 } ?: throw cronFieldError(item)
                    } else 1
                    val range = rangeAndStep[0]
                    val start: Int
                    val end: Int
                    if (range == "*") {
                        start = minimum; end = maximum
                    } else if (range.contains('-')) {
                        val bounds = range.split('-')
                        if (bounds.size != 2) throw cronFieldError(item)
                        start = bounds[0].toIntOrNull() ?: throw cronFieldError(item)
                        end = bounds[1].toIntOrNull() ?: throw cronFieldError(item)
                    } else {
                        start = range.toIntOrNull() ?: throw cronFieldError(item)
                        end = start
                    }
                    if (start < minimum || end > maximum || start > end) {
                        throw AutomationExecutionException(
                            "SCHEDULE_CRON_INVALID", "Cron 值超出 $minimum-$maximum：$item。", "automation.schedule",
                        )
                    }
                    var v = start
                    while (v <= end) {
                        values.add(if (normalizeSunday && v == 7) 0 else v)
                        v += step
                    }
                }
                if (values.isEmpty()) {
                    throw AutomationExecutionException("SCHEDULE_CRON_INVALID", "Cron 字段不能为空。", "automation.schedule")
                }
                return Field(values, wildcard)
            }

            private fun cronFieldError(item: String) = AutomationExecutionException(
                "SCHEDULE_CRON_INVALID", "Cron 字段步长无效：$item。", "automation.schedule",
            )
        }
    }
}

/** 本地日期辅助：把“今天/明天”等相对时间转成毫秒，供 UI 构造 once 触发器。 */
fun localDateBaseEpochMs(): Long = LocalDate.now().atStartOfDay(ZoneId.systemDefault()).toInstant().toEpochMilli()
