using System.Text.Json;
using System.Text.RegularExpressions;

namespace ADBControl.Desktop.Services;

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
        foreach (var json in ExtractJsonCandidates(output).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("result", out var result))
                    root = result;
                if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array)
                    continue;

                var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var app in apps.EnumerateArray())
                {
                    if (!app.TryGetProperty("packageName", out var packageNameElement) ||
                        !app.TryGetProperty("label", out var labelElement))
                        continue;
                    var packageName = packageNameElement.GetString();
                    var label = labelElement.GetString();
                    if (!string.IsNullOrWhiteSpace(packageName) && !string.IsNullOrWhiteSpace(label))
                        labels[packageName] = label;
                }

                if (labels.Count > 0)
                    return labels;
            }
            catch (JsonException)
            {
                // Broadcast diagnostics can appear before the JSON result snapshot.
            }
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                    continue;
                }
            }

            if (candidate.StartsWith('{'))
                yield return candidate;
        }
    }
}
