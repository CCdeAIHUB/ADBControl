using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public interface IPackageNameLookup
{
    Task<IReadOnlyDictionary<string, string>> ResolveMissingAsync(
        IEnumerable<string> packageNames,
        CancellationToken cancellationToken = default);
}

public interface IDevicePackageCatalogGateway
{
    Task<AdbCommandResult> ListPackagesAsync(DeviceModel device, CancellationToken cancellationToken);
    Task<AdbCommandResult> ReadPackageDumpAsync(DeviceModel device, CancellationToken cancellationToken);
    Task<AdbCommandResult> ReadCompanionMetadataAsync(
        DeviceModel device,
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken);
}

public sealed record DevicePackageCatalogItem(
    string PackageName,
    string DisplayName,
    bool HasResolvedDisplayName,
    byte[]? IconPng,
    bool? Enabled,
    bool? IsSystem);

public sealed record PackageCatalogError(
    string ErrorCode,
    string Message,
    string Module,
    bool Recoverable,
    string Suggestion);

public sealed record DevicePackageCatalogResult(
    IReadOnlyList<DevicePackageCatalogItem> Packages,
    IReadOnlyList<PackageCatalogError> Issues,
    PackageCatalogError? FatalError)
{
    public bool Success => FatalError is null;
    public int ResolvedDisplayNameCount => Packages.Count(package => package.HasResolvedDisplayName);
    public int IconCount => Packages.Count(package => package.IconPng is not null);
}

public sealed record PackageCatalogProgress(string Stage, string Message, int Completed, int Total);

public sealed class DevicePackageCatalogGateway : IDevicePackageCatalogGateway
{
    private readonly AdbService _adb;
    private readonly CompanionAppService _companion;

    public DevicePackageCatalogGateway(AdbService adb, CompanionAppService companion)
    {
        _adb = adb;
        _companion = companion;
    }

    public Task<AdbCommandResult> ListPackagesAsync(DeviceModel device, CancellationToken cancellationToken)
        => _adb.ShellAsync(device.DeviceId, "pm list packages", cancellationToken);

    public Task<AdbCommandResult> ReadPackageDumpAsync(DeviceModel device, CancellationToken cancellationToken)
        => _adb.ShellAsync(device.DeviceId, "dumpsys package", cancellationToken);

    public Task<AdbCommandResult> ReadCompanionMetadataAsync(
        DeviceModel device,
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken)
    {
        return _companion.ExecuteCommandAsync(
            device,
            "android.app.list",
            "app.list",
            new Dictionary<string, object?>
            {
                ["includeSystem"] = true,
                ["includeIcons"] = true,
                ["iconSizePx"] = 48,
                ["offset"] = 0,
                ["limit"] = packageNames.Count,
                ["packageNames"] = packageNames,
            },
            cancellationToken);
    }
}

public sealed class DevicePackageCatalogService
{
    public const int MetadataPageSize = 64;
    private readonly IDevicePackageCatalogGateway _gateway;
    private readonly IPackageNameLookup _onlineLookup;

    public DevicePackageCatalogService(IDevicePackageCatalogGateway gateway, IPackageNameLookup onlineLookup)
    {
        _gateway = gateway;
        _onlineLookup = onlineLookup;
    }

    public async Task<DevicePackageCatalogResult> LoadAsync(
        DeviceModel device,
        Action<PackageCatalogProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new PackageCatalogProgress("packages", "正在读取软件包...", 0, 0));
        var packageResult = await _gateway.ListPackagesAsync(device, cancellationToken);
        if (!packageResult.Success)
        {
            var unavailable = AdbConnectionEvaluator.IsDeviceUnavailable(packageResult);
            var fatal = new PackageCatalogError(
                unavailable ? "PACKAGE_LIST_DEVICE_UNAVAILABLE" : "PACKAGE_LIST_ADB_FAILED",
                unavailable ? "ADB 当前无法访问该设备。" : "设备没有返回已安装软件包列表。",
                "package.catalog",
                true,
                unavailable ? "请重新连接设备后重试。" : "请确认设备允许 ADB shell 读取软件包。");
            return new DevicePackageCatalogResult(
                Array.Empty<DevicePackageCatalogItem>(),
                Array.Empty<PackageCatalogError>(),
                fatal);
        }

        var packageNames = ParsePackageNames(packageResult.Stdout);
        var packageSet = packageNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, CompanionPackageMetadata>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<PackageCatalogError>();

        var dumpResult = await _gateway.ReadPackageDumpAsync(device, cancellationToken);
        if (dumpResult.Success)
        {
            foreach (var (packageName, label) in PackageLabelParser.ParseAdbPackageLabels(dumpResult.Stdout))
                labels[packageName] = label;
        }
        else
        {
            issues.Add(new PackageCatalogError(
                "PACKAGE_LABEL_ADB_UNAVAILABLE",
                "设备未提供 dumpsys 应用标签，将继续使用 Companion 本地元数据。",
                "package.catalog",
                true,
                "如仍有名称缺失，请确认伴侣 App 已更新并保持可访问。"));
        }

        for (var offset = 0; offset < packageNames.Count; offset += MetadataPageSize)
        {
            var pageNames = packageNames.Skip(offset).Take(MetadataPageSize).ToArray();
            progress?.Invoke(new PackageCatalogProgress(
                "metadata",
                $"正在读取本机 App 名称与图标（{Math.Min(offset + pageNames.Length, packageNames.Count)}/{packageNames.Count}）...",
                offset,
                packageNames.Count));
            var pageResult = await ReadCompanionMetadataPageAsync(
                device,
                pageNames,
                progress,
                offset,
                packageNames.Count,
                cancellationToken);
            if (!pageResult.Success)
            {
                issues.Add(new PackageCatalogError(
                    "PACKAGE_METADATA_COMPANION_UNAVAILABLE",
                    "伴侣 App 无法返回本机应用元数据。",
                    "package.catalog",
                    true,
                    "请安装或更新伴侣 App；基础包列表仍可使用。"));
                break;
            }
            if (!PackageLabelParser.TryParseCompanionPackagePage(pageResult.Stdout, out var page, out var parseError))
            {
                issues.Add(new PackageCatalogError(
                    "PACKAGE_METADATA_INVALID_RESPONSE",
                    parseError,
                    "package.catalog",
                    true,
                    "请更新伴侣 App 后刷新软件包。"));
                break;
            }

            foreach (var app in page.Apps.Where(app => packageSet.Contains(app.PackageName)))
            {
                metadata[app.PackageName] = app;
                if (!string.IsNullOrWhiteSpace(app.Label))
                    labels[app.PackageName] = app.Label;
            }
            if (page.InvalidIconCount > 0)
            {
                issues.Add(new PackageCatalogError(
                    "PACKAGE_ICON_INVALID",
                    $"已隔离 {page.InvalidIconCount} 个无效应用图标。",
                    "package.catalog",
                    true,
                    "刷新列表；若持续出现，请更新对应 App 或伴侣 App。"));
            }
        }

        var unresolved = packageNames.Where(packageName => !labels.ContainsKey(packageName)).ToArray();
        if (unresolved.Length > 0)
        {
            progress?.Invoke(new PackageCatalogProgress(
                "online-fallback",
                $"正在联网补充 {Math.Min(unresolved.Length, 60)} 个未识别名称...",
                0,
                Math.Min(unresolved.Length, 60)));
            using var onlineTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            onlineTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                foreach (var (packageName, label) in await _onlineLookup.ResolveMissingAsync(unresolved, onlineTimeout.Token))
                    labels.TryAdd(packageName, label);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                issues.Add(new PackageCatalogError(
                    "PACKAGE_NAME_LOOKUP_TIMEOUT",
                    "联网名称补充已超时，本地结果仍可使用。",
                    "package.catalog",
                    true,
                    "检查网络后可刷新重试。"));
            }
        }

        var packages = packageNames.Select(packageName =>
        {
            var hasLabel = labels.TryGetValue(packageName, out var label) && !string.IsNullOrWhiteSpace(label);
            metadata.TryGetValue(packageName, out var app);
            return new DevicePackageCatalogItem(
                packageName,
                hasLabel ? label! : "未获取到 App 名称",
                hasLabel,
                app?.IconPng,
                app?.Enabled,
                app?.IsSystem);
        }).ToArray();
        return new DevicePackageCatalogResult(packages, issues, null);
    }

    private async Task<AdbCommandResult> ReadCompanionMetadataPageAsync(
        DeviceModel device,
        IReadOnlyList<string> packageNames,
        Action<PackageCatalogProgress>? progress,
        int completed,
        int total,
        CancellationToken cancellationToken)
    {
        var result = await _gateway.ReadCompanionMetadataAsync(device, packageNames, cancellationToken);
        if (result.Success || !AdbConnectionEvaluator.IsDeviceUnavailable(result))
            return result;

        // Wireless adbd may close one transport immediately after another long shell command.
        // Retry only that failed page once; protocol, permission, and parsing errors are not retried.
        progress?.Invoke(new PackageCatalogProgress(
            "metadata-retry",
            "无线 ADB 暂时中断，正在重试当前应用元数据页...",
            completed,
            total));
        await Task.Delay(250, cancellationToken);
        return await _gateway.ReadCompanionMetadataAsync(device, packageNames, cancellationToken);
    }

    public static IReadOnlyList<string> ParsePackageNames(string output)
    {
        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["package:".Length..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
