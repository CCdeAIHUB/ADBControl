namespace ADBControl.Desktop.Services;

public static class DeviceConnectivityPolicy
{
    public static bool CanUseCachedOnlineState(bool isConnected) => isConnected;

    public static bool ShouldUseSavedEndpointFallback(bool hasStableMdnsBinding, bool explicitConnection) =>
        explicitConnection || !hasStableMdnsBinding;
}