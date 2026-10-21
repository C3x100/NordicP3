using System.Windows;
using System.Windows.Threading;

namespace NordicP3;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"UNHANDLED: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "NordicP3 error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
