using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ADBControl.Desktop.Services;

public sealed class WindowsNotificationService : IDisposable
{
    private bool _registered;

    public void Show(string title, string message)
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
                return;

            var manager = AppNotificationManager.Default;
            if (!_registered)
            {
                manager.Register();
                _registered = true;
            }

            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            manager.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Windows notification failed: {ex}");
        }
    }

    public void Dispose()
    {
        if (!_registered)
            return;
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Windows notification unregister failed: {ex}");
        }
        _registered = false;
    }
}
