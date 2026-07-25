using Microsoft.Win32;

namespace ADBControl.Desktop;

internal static class WinUiStartupGraphicsCompatibility
{
    private const string Direct3DRegistryPath = @"Software\Microsoft\Direct3D";
    private const string SoftwareRenderingBehavior = "RequireSDKLayers=0;FeatureLevelLimit=0;ForceWARP=1;DisableFLUpgrade=0";
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADBControl",
        "software-ui-rendering.required");

    public static bool IsSoftwareRenderingRequired() => File.Exists(MarkerPath);

    public static void RememberSoftwareRenderingRequired(int exitCode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        File.WriteAllText(
            MarkerPath,
            $"detected={DateTimeOffset.Now:O}{Environment.NewLine}exit=0x{unchecked((uint)exitCode):X8}{Environment.NewLine}");
    }

    public static void ClearRequiredMarker()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (Exception ex)
        {
            StartupLog.Write($"Unable to clear the software-rendering marker: {ex.Message}");
        }
    }

    public static IDisposable? EnableSoftwareRendering(string executablePath)
    {
        try
        {
            var baseKey = Registry.CurrentUser.CreateSubKey(Direct3DRegistryPath, writable: true);
            var keyName = FindMatchingApplication(baseKey, executablePath) ?? FindAvailableApplicationName(baseKey);
            var key = baseKey.CreateSubKey(keyName, writable: true);
            var created = key.GetValue("Name") is null;
            var previousName = RegistryValueSnapshot.Capture(key, "Name");
            var previousBehavior = RegistryValueSnapshot.Capture(key, "D3DBehaviors");
            var previousSdkLayers = RegistryValueSnapshot.Capture(key, "RequireSDKLayers");

            key.SetValue("Name", executablePath, RegistryValueKind.String);
            key.SetValue("D3DBehaviors", SoftwareRenderingBehavior, RegistryValueKind.String);
            key.SetValue("RequireSDKLayers", 0, RegistryValueKind.DWord);
            key.Flush();
            baseKey.Flush();

            StartupLog.Write($"Enabled temporary Direct3D software rendering. key={keyName}");
            return new RegistryLease(
                baseKey,
                key,
                keyName,
                created,
                previousName,
                previousBehavior,
                previousSdkLayers);
        }
        catch (Exception ex)
        {
            StartupLog.Write($"Unable to configure Direct3D software rendering: {ex}");
            return null;
        }
    }

    private static string? FindMatchingApplication(RegistryKey baseKey, string executablePath)
    {
        foreach (var keyName in baseKey.GetSubKeyNames())
        {
            using var key = baseKey.OpenSubKey(keyName, writable: false);
            if (string.Equals(key?.GetValue("Name") as string, executablePath, StringComparison.OrdinalIgnoreCase))
                return keyName;
        }

        return null;
    }

    private static string FindAvailableApplicationName(RegistryKey baseKey)
    {
        var existing = baseKey.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 1024; index++)
        {
            var candidate = $"Application{index}";
            if (!existing.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException("No Direct3D application configuration slot is available.");
    }

    private sealed class RegistryLease(
        RegistryKey baseKey,
        RegistryKey applicationKey,
        string keyName,
        bool created,
        RegistryValueSnapshot previousName,
        RegistryValueSnapshot previousBehavior,
        RegistryValueSnapshot previousSdkLayers) : IDisposable
    {
        private RegistryKey? _baseKey = baseKey;
        private RegistryKey? _applicationKey = applicationKey;

        public void Dispose()
        {
            var key = Interlocked.Exchange(ref _applicationKey, null);
            var root = Interlocked.Exchange(ref _baseKey, null);
            if (key is null || root is null)
                return;

            try
            {
                if (created)
                {
                    key.Dispose();
                    root.DeleteSubKey(keyName, throwOnMissingSubKey: false);
                }
                else
                {
                    previousName.Restore(key, "Name");
                    previousBehavior.Restore(key, "D3DBehaviors");
                    previousSdkLayers.Restore(key, "RequireSDKLayers");
                    key.Flush();
                    key.Dispose();
                }

                root.Flush();
                StartupLog.Write($"Restored temporary Direct3D software-rendering configuration. key={keyName}");
            }
            catch (Exception ex)
            {
                StartupLog.Write($"Unable to restore Direct3D configuration: {ex}");
            }
            finally
            {
                key.Dispose();
                root.Dispose();
            }
        }
    }

    private sealed record RegistryValueSnapshot(bool Exists, object? Value, RegistryValueKind Kind)
    {
        public static RegistryValueSnapshot Capture(RegistryKey key, string name)
        {
            var exists = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
            return exists
                ? new RegistryValueSnapshot(true, key.GetValue(name), key.GetValueKind(name))
                : new RegistryValueSnapshot(false, null, RegistryValueKind.None);
        }

        public void Restore(RegistryKey key, string name)
        {
            if (Exists && Value is not null)
                key.SetValue(name, Value, Kind);
            else
                key.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}

internal static class StartupLog
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ADBControl",
                "logs");
            Directory.CreateDirectory(directory);
            lock (Sync)
            {
                File.AppendAllText(
                    Path.Combine(directory, "startup.log"),
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
