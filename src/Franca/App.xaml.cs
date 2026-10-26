using System.Windows;
using Franca.Diagnostics;
using Franca.Services;

namespace Franca;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // No StartupUri: an awaited diagnostic must not create the normal window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (await ProbeRunner.TryRunAsync(this, e.Args))
            return;
        base.OnStartup(e);
        UiStrings.Current = AppSettings.Load().TargetLanguage;
        new MainWindow().Show();
        ShutdownMode = ShutdownMode.OnLastWindowClose;
    }
}
