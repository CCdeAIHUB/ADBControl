using System.IO.Compression;
using System.Security;
using System.Text;
using ADBControl.Desktop.Models;
using Microsoft.Data.Sqlite;

namespace ADBControl.Desktop.Services;

public sealed class HardwareReportExporter
{
    public async Task ExportAsync(HardwareReportFormat format, IReadOnlyList<HardwareMonitorSample> samples, string path)
    {
        if (samples.Count == 0)
            throw new InvalidOperationException("没有可导出的硬件监控记录。");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        switch (format)
        {
            case HardwareReportFormat.Excel:
                await Task.Run(() => ExportExcel(samples, path));
                break;
            case HardwareReportFormat.Html:
                await File.WriteAllTextAsync(path, BuildHtml(samples), new UTF8Encoding(false));
                break;
            case HardwareReportFormat.Sqlite:
                await Task.Run(() => ExportSqlite(samples, path));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的硬件报告格式。");
        }
    }

    public static string BuildHtml(IReadOnlyList<HardwareMonitorSample> samples)
    {
        var rows = new StringBuilder();
        foreach (var sample in samples)
        {
            rows.Append("<tr>")
                .Append(Cell(sample.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")))
                .Append(Cell(FormatCpu(sample)))
                .Append(Cell(sample.CpuFrequencyUsagePercent is double cpuUsage ? $"{cpuUsage:0.##}%" : "当前连接无法获取"))
                .Append(Cell(FormatMemory(sample)))
                .Append(Cell(FormatGpu(sample)))
                .Append(Cell(FormatTemperatures(sample)))
                .Append(Cell(sample.RefreshRateHz is double rate ? $"{rate:0.##} Hz" : "当前连接无法获取"))
                .Append(Cell(sample.AppFps is double fps ? $"{fps:0.##} FPS" : "当前连接无法获取"))
                .AppendLine("</tr>");
        }

        return $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ADBControl 硬件监控报告</title>
              <style>
                body { background: #111827; color: #e5edf8; font: 14px "Segoe UI", sans-serif; margin: 0; padding: 28px; }
                h1 { margin: 0 0 8px; color: #36d782; font-size: 22px; }
                p { color: #99a9bd; margin: 0 0 22px; }
                table { border-collapse: collapse; width: 100%; background: #172236; }
                th, td { border: 1px solid #32425b; padding: 10px; text-align: left; vertical-align: top; }
                th { color: #36d782; background: #1c2a40; }
                tr:nth-child(even) { background: #142034; }
              </style>
            </head>
            <body>
              <h1>硬件监控报告</h1>
              <p>共记录 {{samples.Count}} 个采样点，导出时间 {{DateTimeOffset.Now.LocalDateTime:yyyy-MM-dd HH:mm:ss}}。</p>
              <table>
                <thead><tr><th>时间</th><th>CPU 各核心频率</th><th>CPU 总体占用估算</th><th>内存</th><th>GPU</th><th>硬件温度</th><th>刷新率</th><th>当前应用 FPS</th></tr></thead>
                <tbody>{{rows}}</tbody>
              </table>
            </body>
            </html>
            """;
    }

    private static void ExportExcel(IReadOnlyList<HardwareMonitorSample> samples, string path)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteZipText(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        WriteZipText(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteZipText(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="硬件监控" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteZipText(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        WriteZipText(archive, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="2"><font><sz val="11"/><color rgb="FFE5EDF8"/><name val="Segoe UI"/></font><font><b/><sz val="11"/><color rgb="FF36D782"/><name val="Segoe UI"/></font></fonts>
              <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1C2A40"/><bgColor indexed="64"/></patternFill></fill></fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="1" borderId="0" xfId="0"/></cellXfs>
            </styleSheet>
            """);
        WriteZipText(archive, "xl/worksheets/sheet1.xml", BuildWorksheet(samples));
    }

    private static string BuildWorksheet(IReadOnlyList<HardwareMonitorSample> samples)
    {
        var rows = new StringBuilder();
        var headers = new[]
        {
            "时间", "CPU 各核心频率", "CPU 总体占用估算", "物理内存总量", "物理内存已用",
            "物理内存使用率", "虚拟内存总量", "虚拟内存可用", "扩展后内存使用率",
            "GPU 占用率", "GPU 当前频率", "GPU 显存占用", "硬件温度", "刷新率", "当前应用 FPS",
        };
        rows.Append("<row r=\"1\">");
        for (var index = 0; index < headers.Length; index++)
            rows.Append(InlineCell(index, 1, headers[index], 1));
        rows.AppendLine("</row>");

        for (var index = 0; index < samples.Count; index++)
        {
            var row = index + 2;
            var sample = samples[index];
            var values = new[]
            {
                sample.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                FormatCpu(sample),
                sample.CpuFrequencyUsagePercent?.ToString("0.##") ?? "",
                sample.TotalMemoryKb?.ToString() ?? "",
                sample.UsedMemoryKb?.ToString() ?? "",
                sample.MemoryUsagePercent?.ToString("0.##") ?? "",
                sample.SwapTotalKb?.ToString() ?? "",
                sample.SwapFreeKb?.ToString() ?? "",
                sample.ExtendedMemoryUsagePercent?.ToString("0.##") ?? "",
                sample.Gpu?.EffectiveUsagePercent?.ToString("0.##") ?? "",
                sample.Gpu?.CurrentFrequencyHz?.ToString() ?? "",
                sample.Gpu?.MemoryBytes?.ToString() ?? "",
                FormatTemperatures(sample),
                sample.RefreshRateHz?.ToString("0.##") ?? "",
                sample.AppFps?.ToString("0.##") ?? "",
            };
            rows.Append($"<row r=\"{row}\">");
            for (var column = 0; column < values.Length; column++)
                rows.Append(InlineCell(column, row, values[column], 0));
            rows.AppendLine("</row>");
        }

        return $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetViews><sheetView workbookViewId="0"/></sheetViews>
              <sheetFormatPr defaultRowHeight="18"/>
              <cols><col min="1" max="1" width="22" customWidth="1"/><col min="2" max="2" width="42" customWidth="1"/><col min="3" max="15" width="20" customWidth="1"/></cols>
              <sheetData>{{rows}}</sheetData>
            </worksheet>
            """;
    }

    private static void ExportSqlite(IReadOnlyList<HardwareMonitorSample> samples, string path)
    {
        if (File.Exists(path))
            File.Delete(path);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE hardware_samples (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    captured_at TEXT NOT NULL,
                    total_memory_kb INTEGER,
                    available_memory_kb INTEGER,
                    used_memory_kb INTEGER,
                    memory_usage_percent REAL,
                    swap_total_kb INTEGER,
                    swap_free_kb INTEGER,
                    extended_memory_usage_percent REAL,
                    cpu_frequency_usage_percent REAL,
                    gpu_usage_percent REAL,
                    gpu_current_frequency_hz INTEGER,
                    gpu_maximum_frequency_hz INTEGER,
                    gpu_memory_bytes INTEGER,
                    refresh_rate_hz REAL,
                    app_fps REAL
                );
                CREATE TABLE cpu_frequencies (
                    sample_id INTEGER NOT NULL,
                    core_index INTEGER NOT NULL,
                    kilohertz INTEGER NOT NULL,
                    maximum_kilohertz INTEGER,
                    frequency_usage_percent REAL
                );
                CREATE TABLE temperatures (
                    sample_id INTEGER NOT NULL,
                    sensor_name TEXT NOT NULL,
                    celsius REAL NOT NULL
                );
                """;
            schema.ExecuteNonQuery();
        }

        foreach (var sample in samples)
        {
            long sampleId;
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO hardware_samples (
                        captured_at, total_memory_kb, available_memory_kb, used_memory_kb, memory_usage_percent,
                        swap_total_kb, swap_free_kb, extended_memory_usage_percent, cpu_frequency_usage_percent,
                        gpu_usage_percent, gpu_current_frequency_hz, gpu_maximum_frequency_hz, gpu_memory_bytes, refresh_rate_hz, app_fps)
                    VALUES (
                        $capturedAt, $totalMemory, $availableMemory, $usedMemory, $usagePercent,
                        $swapTotal, $swapFree, $extendedUsage, $cpuUsage,
                        $gpuUsage, $gpuCurrent, $gpuMaximum, $gpuMemory, $refreshRate, $appFps);
                    """;
                insert.Parameters.AddWithValue("$capturedAt", sample.CapturedAt.ToString("O"));
                insert.Parameters.AddWithValue("$totalMemory", (object?)sample.TotalMemoryKb ?? DBNull.Value);
                insert.Parameters.AddWithValue("$availableMemory", (object?)sample.AvailableMemoryKb ?? DBNull.Value);
                insert.Parameters.AddWithValue("$usedMemory", (object?)sample.UsedMemoryKb ?? DBNull.Value);
                insert.Parameters.AddWithValue("$usagePercent", (object?)sample.MemoryUsagePercent ?? DBNull.Value);
                insert.Parameters.AddWithValue("$swapTotal", (object?)sample.SwapTotalKb ?? DBNull.Value);
                insert.Parameters.AddWithValue("$swapFree", (object?)sample.SwapFreeKb ?? DBNull.Value);
                insert.Parameters.AddWithValue("$extendedUsage", (object?)sample.ExtendedMemoryUsagePercent ?? DBNull.Value);
                insert.Parameters.AddWithValue("$cpuUsage", (object?)sample.CpuFrequencyUsagePercent ?? DBNull.Value);
                insert.Parameters.AddWithValue("$gpuUsage", (object?)sample.Gpu?.EffectiveUsagePercent ?? DBNull.Value);
                insert.Parameters.AddWithValue("$gpuCurrent", (object?)sample.Gpu?.CurrentFrequencyHz ?? DBNull.Value);
                insert.Parameters.AddWithValue("$gpuMaximum", (object?)sample.Gpu?.MaximumFrequencyHz ?? DBNull.Value);
                insert.Parameters.AddWithValue("$gpuMemory", (object?)sample.Gpu?.MemoryBytes ?? DBNull.Value);
                insert.Parameters.AddWithValue("$refreshRate", (object?)sample.RefreshRateHz ?? DBNull.Value);
                insert.Parameters.AddWithValue("$appFps", (object?)sample.AppFps ?? DBNull.Value);
                insert.ExecuteNonQuery();
                using var lastId = connection.CreateCommand();
                lastId.Transaction = transaction;
                lastId.CommandText = "SELECT last_insert_rowid();";
                sampleId = Convert.ToInt64(lastId.ExecuteScalar());
            }

            foreach (var frequency in sample.CpuFrequencies)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO cpu_frequencies (sample_id, core_index, kilohertz, maximum_kilohertz, frequency_usage_percent)
                    VALUES ($sampleId, $core, $kilohertz, $maximum, $usage);
                    """;
                insert.Parameters.AddWithValue("$sampleId", sampleId);
                insert.Parameters.AddWithValue("$core", frequency.CoreIndex);
                insert.Parameters.AddWithValue("$kilohertz", frequency.Kilohertz);
                insert.Parameters.AddWithValue("$maximum", (object?)frequency.MaximumKilohertz ?? DBNull.Value);
                insert.Parameters.AddWithValue("$usage", (object?)frequency.FrequencyUsagePercent ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }

            foreach (var temperature in sample.Temperatures)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO temperatures (sample_id, sensor_name, celsius) VALUES ($sampleId, $name, $celsius);";
                insert.Parameters.AddWithValue("$sampleId", sampleId);
                insert.Parameters.AddWithValue("$name", temperature.Name);
                insert.Parameters.AddWithValue("$celsius", temperature.Celsius);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static string InlineCell(int columnIndex, int row, string value, int style)
    {
        var column = ((char)('A' + columnIndex)).ToString();
        return $"<c r=\"{column}{row}\" t=\"inlineStr\" s=\"{style}\"><is><t>{Xml(value)}</t></is></c>";
    }

    private static void WriteZipText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Cell(string value) => $"<td>{Xml(value)}</td>";
    private static string Xml(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string FormatCpu(HardwareMonitorSample sample)
        => sample.CpuFrequencies.Count == 0
            ? "当前连接无法获取"
            : string.Join("，", sample.CpuFrequencies.Select(frequency => $"CPU {frequency.CoreIndex}: {frequency.DisplayValue}"));

    private static string FormatMemory(HardwareMonitorSample sample)
    {
        if (sample.TotalMemoryKb is not long total || sample.UsedMemoryKb is not long used)
            return "当前连接无法获取";
        var usage = sample.MemoryUsagePercent is double percent ? $" ({percent:0.#}%)" : string.Empty;
        var extension = sample.SwapTotalKb is > 0
            ? $"；虚拟内存 {sample.SwapTotalKb:N0} kB（可用 {sample.SwapFreeKb:N0} kB）"
            : string.Empty;
        return $"物理内存已用 {used:N0} kB / 总计 {total:N0} kB{usage}{extension}";
    }

    private static string FormatGpu(HardwareMonitorSample sample)
    {
        if (sample.Gpu is null)
            return "当前连接无法获取";
        var usage = sample.Gpu.EffectiveUsagePercent is double percent ? $"占用 {percent:0.##}%" : "占用率无法获取";
        var frequency = sample.Gpu.CurrentFrequencyHz is long current ? $"，频率 {current / 1_000_000d:0.##} MHz" : string.Empty;
        var memory = sample.Gpu.MemoryBytes is long bytes ? $"，显存占用 {bytes / 1024d / 1024d:0.##} MB" : string.Empty;
        return usage + frequency + memory;
    }

    private static string FormatTemperatures(HardwareMonitorSample sample)
        => sample.Temperatures.Count == 0
            ? "当前连接无法获取"
            : string.Join("，", sample.Temperatures.Select(temperature => $"{temperature.Name}: {temperature.DisplayValue}"));
}
