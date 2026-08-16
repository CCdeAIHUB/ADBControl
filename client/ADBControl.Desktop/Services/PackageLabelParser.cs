using System.Text.Json;
using System.Text.RegularExpressions;

namespace ADBControl.Desktop.Services;

public sealed record CompanionPackageMetadata(
    string PackageName,
    string? Label,
    bool Enabled,
    bool IsSystem,
    string? SourceDir,
    byte[]? IconPng);

public sealed record CompanionPackagePage(
    IReadOnlyList<CompanionPackageMetadata> Apps,
    int Total,
    int Offset,
    int Limit,
    int InvalidIconCount)
{
    public int NextOffset => Offset + Apps.Count;
    public bool HasMore => Apps.Count > 0 && NextOffset < Total;
}

public static class PackageLabelParser
{
    public static Dictionary<string, string> ParseAdbPackageLabels(string output)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? packageName = null;
        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var packageMatch = Regex.Match(line, @"Package \[(?<package>[^\]]+)\]", RegexOptions.IgnoreCase);
            if (packageMatch.Success)
            {
                packageName = packageMatch.Groups["package"].Value;
                continue;
            }

            var embeddedPackageMatch = Regex.Match(line, @"pkg=Package\{[^\s]+\s+(?<package>[^\s\}]+)", RegexOptions.IgnoreCase);
            if (embeddedPackageMatch.Success)
            {
                packageName = embeddedPackageMatch.Groups["package"].Value;
                continue;
            }

            if (packageName is null || !line.StartsWith("application-label", StringComparison.OrdinalIgnoreCase))
                continue;

            var separator = line.IndexOf(':');
            if (separator < 0)
                continue;
            var label = line[(separator + 1)..].Trim();
            var localeSeparator = label.LastIndexOf('=');
            if (localeSeparator >= 0)
                label = label[(localeSeparator + 1)..];
            label = label.Trim().Trim('\'', '"');
            if (!string.IsNullOrWhiteSpace(label))
                labels[packageName] = label;
        }

        return labels;
    }

    public static Dictionary<string, string> ParseCompanionPackageLabels(string output)
    {
        if (!TryParseCompanionPackagePage(output, out var page, out _))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return page.Apps
            .Where(app => !string.IsNullOrWhiteSpace(app.Label))
            .GroupBy(app => app.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Label!, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryParseCompanionPackagePage(
        string output,
        out CompanionPackagePage page,
        out string error)
    {
        error = "未找到 Companion 应用元数据 JSON。";
        foreach (var json in ExtractJsonCandidates(output).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    error = "Companion 返回应用元数据失败。";
                    continue;
                }
                if (root.TryGetProperty("result", out var result))
                    root = result;
                if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
                {
                    error = "Companion 响应缺少 apps 数组。";
                    continue;
                }

                var metadata = new List<CompanionPackageMetadata>();
                var invalidIconCount = 0;
                foreach (var app in apps.EnumerateArray())
                {
                    if (!app.TryGetProperty("packageName", out var packageNameElement))
                        continue;
                    var packageName = packageNameElement.GetString();
                    if (string.IsNullOrWhiteSpace(packageName))
                        continue;

                    metadata.Add(new CompanionPackageMetadata(
                        packageName,
                        ReadString(app, "label"),
                        ReadBoolean(app, "enabled", true),
                        ReadBoolean(app, "system", false),
                        ReadString(app, "sourceDir"),
                        ParsePng(app, ref invalidIconCount)));
                }

                var total = ReadInt32(root, "total", metadata.Count);
                var offset = ReadInt32(root, "offset", 0);
                var limit = ReadInt32(root, "limit", Math.Max(metadata.Count, 1));
                page = new CompanionPackagePage(
                    metadata,
                    Math.Max(total, metadata.Count),
                    Math.Max(offset, 0),
                    Math.Max(limit, 1),
                    invalidIconCount);
                error = string.Empty;
                return true;
            }
            catch (JsonException ex)
            {
                error = $"Companion 应用元数据 JSON 无效：{ex.Message}";
            }
        }

        page = new CompanionPackagePage(Array.Empty<CompanionPackageMetadata>(), 0, 0, 1, 0);
        return false;
    }

    private static IEnumerable<string> ExtractJsonCandidates(string output)
    {
        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var dataIndex = line.IndexOf("data=", StringComparison.OrdinalIgnoreCase);
            var candidate = dataIndex >= 0 ? line[(dataIndex + "data=".Length)..].Trim() : line;
            if (!candidate.StartsWith('{') && !candidate.StartsWith('"'))
                continue;

            if (candidate.StartsWith('"') && candidate.EndsWith('"'))
            {
                try
                {
                    candidate = JsonSerializer.Deserialize<string>(candidate) ?? string.Empty;
                }
                catch (JsonException)
                {
                    // Some OEM am broadcast implementations wrap resultData without escaping it.
                    candidate = candidate[1..^1];
                }
            }

            if (candidate.StartsWith('{'))
                yield return candidate;
        }
    }

    private static byte[]? ParsePng(JsonElement app, ref int invalidIconCount)
    {
        var encoded = ReadString(app, "iconPngBase64");
        if (string.IsNullOrWhiteSpace(encoded))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length is 0 or > 262_144 ||
                bytes.Length < 8 ||
                !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            {
                invalidIconCount++;
                return null;
            }
            return bytes;
        }
        catch (FormatException)
        {
            invalidIconCount++;
            return null;
        }
    }

    private static string? ReadString(JsonElement source, string propertyName)
        => source.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ReadBoolean(JsonElement source, string propertyName, bool fallback)
        => source.TryGetProperty(propertyName, out var property) &&
           property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;

    private static int ReadInt32(JsonElement source, string propertyName, int fallback)
        => source.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetInt32(out var value)
            ? value
            : fallback;
}
