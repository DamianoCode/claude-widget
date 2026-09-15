using System.Windows;
using ClaudeWidget.Core;
using Velopack;

namespace ClaudeWidget;

/// <summary>
/// Punkt wejścia. App.xaml nie generuje własnego Main (patrz csproj) — Velopack musi ruszyć,
/// zanim cokolwiek z WPF czy okna widżetu w ogóle istnieje.
/// </summary>
public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Instalacja/aktualizacja/odinstalowanie: Velopack woła nas z odpowiednią flagą, robimy
        // to, co trzeba, i kończymy — bez okna, bo hooki nie mogą pokazywać UI.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => InstallHooks.AfterInstallOrUpdate(isFirstInstall: true))
            .OnAfterUpdateFastCallback(_ => InstallHooks.AfterInstallOrUpdate(isFirstInstall: false))
            .OnBeforeUninstallFastCallback(_ => InstallHooks.BeforeUninstall())
            .Run();

        var options = StartupOptions.Parse(args);
        using var instance = SingleInstance.Acquire(options.StateDir);
        if (instance is null) return; // druga instancja na ten sam katalog stanu — po prostu kończymy

        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow(new WidgetPaths(options.StateDir));
        app.MainWindow = window;
        window.Show();
        app.Run();
    }
}
