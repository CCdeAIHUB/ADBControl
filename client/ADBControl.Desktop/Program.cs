using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using System.Threading;
using WinRT;

namespace ADBControl.Desktop;

public static class Program
{
    private static App? _app;

    [STAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _app = new App();
        });
    }
}
