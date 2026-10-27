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
        try { ReleaseAssetsService.EnsureAvailable(); }
        catch (Exception ex)
        {
            MessageBox.Show("Franca 无法准备运行组件，请将 EXE 放到可写的独立文件夹后重试。\n"
                + "Could not prepare application files. Move the EXE to a writable folder and retry.\n\n" + ex.Message,
                "Franca", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (await ProbeRunner.TryRunAsync(this, e.Args))
            return;
        base.OnStartup(e);
        UiStrings.Current = AppSettings.Load().TargetLanguage;
        new MainWindow().Show();
        ShutdownMode = ShutdownMode.OnLastWindowClose;
    }
}
