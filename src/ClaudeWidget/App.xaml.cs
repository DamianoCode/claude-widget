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
        // Aktualizacja pobrana w tle instaluje się sama przy następnym uruchomieniu (np. po restarcie
        // komputera); pozycja w menu zasobnika robi to od razu.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(true)
            .OnAfterInstallFastCallback(_ => InstallHooks.AfterInstallOrUpdate(isFirstInstall: true))
            .OnAfterUpdateFastCallback(_ => InstallHooks.AfterInstallOrUpdate(isFirstInstall: false))
            .OnBeforeUninstallFastCallback(_ => InstallHooks.BeforeUninstall())
            .Run();

        var options = StartupOptions.Parse(args);
        var paths = new WidgetPaths(options.StateDir);
        // Nazwa muteksu musi wyjść z pełnej, znormalizowanej ścieżki (WidgetPaths robi
        // Path.GetFullPath) — inaczej dwa uruchomienia tego samego katalogu z inną pisownią
        // (względna ścieżka, inna wielkość liter, końcowy ukośnik) dostałyby różne muteksy.
        using var instance = SingleInstance.Acquire(paths.StateDir);
        if (instance is null) return; // druga instancja na ten sam katalog stanu — po prostu kończymy

        var app = new App();
        app.InitializeComponent();
        // "okno: $_" w widget.ps1 łapało wszystko dookoła Dispatcher.Run(); to samo tutaj —
        // nieobsłużony wyjątek z dowolnego handlera UI ląduje w logu zamiast wywalać widżet.
        app.DispatcherUnhandledException += (_, e) =>
        {
            WidgetLog.Write(paths, $"okno: {e.Exception}");
            e.Handled = true;
        };
        var window = new MainWindow(paths, args);
        app.MainWindow = window;
        window.Show();
        app.Run();
    }
}
