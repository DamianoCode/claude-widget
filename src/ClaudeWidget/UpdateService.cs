using Velopack;
using Velopack.Sources;

namespace ClaudeWidget;

/// <summary>
/// Sprawdza aktualizacje z GitHub Releases, gdy widżet jest zainstalowany przez Velopack (dev runy
/// nigdy nie sprawdzają). Pobiera po cichu; restart z zastosowaniem aktualizacji zależy od kliknięcia
/// w menu zasobnika, żeby nie przerywać pracy.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager _manager = new(new GithubSource("https://github.com/DamianoCode/claude-widget", null, false));

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Wersja gotowa do zainstalowania po restarcie, albo null, gdy nic nie czeka.</summary>
    public string? PendingVersion { get; private set; }

    private VelopackAsset? _pendingUpdate;

    public event Action? UpdateReady;

    public async Task StartAsync(Action<string> log, CancellationToken cancellationToken)
    {
        if (!IsInstalled) return;
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckOnceAsync(log).ConfigureAwait(false);
            try { await Task.Delay(CheckInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckOnceAsync(Action<string> log)
    {
        if (_pendingUpdate is not null) return; // już pobrana, czeka na restart
        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null) return;
            await _manager.DownloadUpdatesAsync(info, null, CancellationToken.None).ConfigureAwait(false);
            _pendingUpdate = info.TargetFullRelease;
            PendingVersion = info.TargetFullRelease.Version.ToString();
            UpdateReady?.Invoke();
        }
        catch (Exception error)
        {
            log($"sprawdzanie aktualizacji: {error.Message}");
        }
    }

    /// <summary>
    /// <paramref name="restartArgs"/> — argumenty startowe (np. <c>--state-dir</c>), żeby widżet po
    /// restarcie otworzył się na tym samym katalogu stanu, niezależnie od tego, jak go uruchomiono.
    /// </summary>
    public void ApplyAndRestart(string[] restartArgs)
    {
        if (_pendingUpdate is not null) _manager.ApplyUpdatesAndRestart(_pendingUpdate, restartArgs);
    }
}
