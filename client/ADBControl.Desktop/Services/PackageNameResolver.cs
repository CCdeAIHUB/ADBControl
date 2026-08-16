using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace ADBControl.Desktop.Services;

public sealed class PackageNameResolver : IPackageNameLookup
{
    private static readonly HttpClient Client = CreateClient();
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyDictionary<string, string>> ResolveMissingAsync(
        IEnumerable<string> packageNames,
        CancellationToken cancellationToken = default)
    {
        var unresolved = packageNames
            .Where(IsValidPackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();
        var resolved = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(unresolved, new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = cancellationToken,
        }, async (packageName, token) =>
        {
            var label = await ResolveAsync(packageName, token);
            if (!string.IsNullOrWhiteSpace(label))
                resolved[packageName] = label;
        });
        return resolved;
    }

    public async Task<string?> ResolveAsync(string packageName, CancellationToken cancellationToken = default)
    {
        if (!IsValidPackageName(packageName))
            return null;
        if (_cache.TryGetValue(packageName, out var cached))
            return cached;

        try
        {
            var uri = new Uri($"https://play.google.com/store/apps/details?id={Uri.EscapeDataString(packageName)}&hl=zh-CN");
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _cache.TryAdd(packageName, null);
                return null;
            }
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var label = ParseStoreTitle(html);
            _cache[packageName] = label;
            return label;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _cache.TryAdd(packageName, null);
            return null;
        }
    }

    public static string? ParseStoreTitle(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var meta = Regex.Match(html, "<meta[^>]+itemprop=[\\\"']name[\\\"'][^>]+content=[\\\"'](?<name>[^\\\"']+)", RegexOptions.IgnoreCase);
        if (!meta.Success)
            meta = Regex.Match(html, "<meta[^>]+content=[\\\"'](?<name>[^\\\"']+)[\\\"'][^>]+itemprop=[\\\"']name[\\\"']", RegexOptions.IgnoreCase);
        var title = meta.Success
            ? meta.Groups["name"].Value
            : Regex.Match(html, "<title>(?<name>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["name"].Value;
        title = WebUtility.HtmlDecode(title).Trim();
        title = Regex.Replace(title, @"\s+-\s+Google Play.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static bool IsValidPackageName(string packageName)
        => !string.IsNullOrWhiteSpace(packageName) &&
           packageName.Length <= 255 &&
           Regex.IsMatch(packageName, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ADBControl/0.1 package-label-resolver");
        return client;
    }
}