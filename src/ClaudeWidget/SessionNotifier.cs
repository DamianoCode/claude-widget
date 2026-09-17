using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows.Media;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Sessions;
using ClaudeWidget.Core.Settings;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ClaudeWidget;

/// <summary>
/// Dźwięk i powiadomienie Windows, gdy sesja zaczyna czekać na Ciebie albo kończy z nowym wynikiem.
/// Powiadomienie ma tag sesji: kolejne tej samej sesji zastępuje poprzednie, a znika, gdy sesja
/// przestaje czekać albo przejrzysz wynik. Kliknięcie w nie przenosi do sesji. Który dźwięk gra,
/// jak głośno i czy w ogóle (wyciszenie) — wg ustawień (<see cref="AlertSounds"/>).
/// </summary>
public sealed class SessionNotifier
{
    private const string Group = "claude-code";

    private readonly WidgetPaths _paths;
    private readonly Action<string> _log;
    private readonly Action<string> _activated;
    private readonly string? _appId;
    // Powiadomienia trzyma się, póki są aktualne — inaczej ich zdarzenie Activated zniknie z GC.
    private readonly Dictionary<string, ToastNotification> _shown = [];
    // Pliki większe od tego gra MediaPlayer strumieniowo, zamiast trzymać je w pamięci.
    private const long MaxInMemoryBytes = 16 * 1024 * 1024;
    private const uint SndAsync = 0x1, SndNoDefault = 0x2, SndMemory = 0x4;

    private MediaPlayer? _player;
    // Dane WAV, z których PlaySound właśnie gra — w pamięci natywnej, bo GC nie może ich przesunąć
    // ani zwolnić w trakcie odtwarzania (SoundPlayer z MemoryStream tablicy nie przypina).
    private IntPtr _wav;
    private WidgetConfig _config = new();

    /// <param name="installed">
    /// Zainstalowany widżet ma w menu Start skrót z identyfikatorem velopack.ClaudeWidget — bez niego
    /// (uruchomienie deweloperskie) Windows nie pokaże powiadomienia, więc się go nie wysyła.
    /// </param>
    public SessionNotifier(WidgetPaths paths, bool installed, Action<string> log, Action<string> activated)
    {
        _paths = paths;
        _log = log;
        _activated = activated;
        _appId = CurrentAppUserModelId() ?? (installed ? "velopack.ClaudeWidget" : null);
        // Powiadomienia poprzedniego procesu (restart po aktualizacji, awaria) są martwe: ich
        // kliknięcie niczego już nie zrobi, a same nie znikną.
        ClearAll();
    }

    /// <summary>Ustawienia dźwięków i powiadomień; wyłączenie powiadomień zdejmuje te na ekranie.</summary>
    public void Configure(WidgetConfig config)
    {
        _config = config;
        if (!config.NotificationsOn) ClearAll();
    }

    /// <returns>true, gdy dźwięk faktycznie zagrał — tylko wtedy liczy się limit powtórzeń.</returns>
    public bool Raise(Alert alert, long nowMs)
    {
        if (_config.IsMuted(nowMs)) return false;
        var played = _config.SoundsOn && alert.Sound && PlaySound(alert.Kind, ChoiceFor(alert.Kind, _config));
        if (_config.NotificationsOn) Show(alert);
        return played;
    }

    /// <summary>Odsłuchanie dźwięku z okna ustawień — także przy wyłączonych dźwiękach i wyciszeniu.</summary>
    public void Preview(AlertKind kind, string? choice, int volume)
    {
        var path = ResolvePath(kind, choice);
        if (path is not null) Play(path, volume, kind);
    }

    public string? ResolvePath(AlertKind kind, string? choice) =>
        AlertSounds.Resolve(choice, kind, _paths.WidgetDir, AppContext.BaseDirectory, File.Exists);

    private static string? ChoiceFor(AlertKind kind, WidgetConfig config) =>
        kind == AlertKind.Waiting ? config.SoundWaiting : config.SoundDone;

    public void Clear(string sessionId)
    {
        if (!_shown.Remove(sessionId) || _appId is null) return;
        try
        {
            ToastNotificationManager.History.Remove(Tag(sessionId), Group, _appId);
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        {
            _log($"powiadomienie (zdejmowanie): {error.Message}");
        }
    }

    // Cała grupa naraz, także powiadomienia, których ten proces nie pamięta.
    public void ClearAll()
    {
        _shown.Clear();
        if (_appId is null) return;
        try
        {
            ToastNotificationManager.History.RemoveGroup(Group, _appId);
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        {
            _log($"powiadomienia (zdejmowanie): {error.Message}");
        }
    }

    private bool PlaySound(AlertKind kind, string? choice)
    {
        var path = ResolvePath(kind, choice);
        return path is not null && Play(path, _config.VolumePercent, kind);
    }

    // WAV gra PlaySound — lekko i od razu. MediaPlayer ładuje odtwarzacz Windows Media (dziesiątki MB,
    // kilkanaście wątków, ~70 ms CPU na dźwięk), więc powstaje dopiero dla MP3/WMA/M4A albo WAV,
    // którego nie da się zagrać wprost (skompresowany, bardzo duży).
    private bool Play(string path, int volume, AlertKind kind)
    {
        // Nowe odtwarzanie przerywa poprzednie.
        StopPlayback();
        if (volume == 0) return true;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                _log($"dźwięk {path}: brak pliku");
                return false;
            }
            if (file.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) && file.Length <= MaxInMemoryBytes
                && PlayWav(File.ReadAllBytes(path), volume))
            {
                return true;
            }
            return PlayMedia(path, volume, kind);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log($"dźwięk {Path.GetFileName(path)}: {error.Message}");
            return false;
        }
    }

    private bool PlayWav(byte[] wav, int volume)
    {
        var gain = WavVolume.Gain(volume);
        byte[]? data = gain >= 1 ? (WavVolume.IsPlain(wav) ? wav : null) : WavVolume.Scale(wav, gain);
        if (data is null) return false;
        var memory = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, memory, data.Length);
        // PlaySound zna tylko rozmiary z nagłówka — RIFF nie może obiecywać więcej, niż jest w buforze.
        Marshal.WriteInt32(memory, 4, data.Length - 8);
        if (!PlaySound(memory, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault))
        {
            Marshal.FreeHGlobal(memory);
            return false;
        }
        _wav = memory;
        return true;
    }

    private bool PlayMedia(string path, int volume, AlertKind kind)
    {
        try
        {
            var player = new MediaPlayer();
            player.MediaEnded += (_, _) => Release(player);
            player.MediaFailed += (_, e) =>
            {
                // Np. Windows N bez Media Feature Pack. Prośba nie może przejść bez dźwięku — gra wbudowany.
                _log($"dźwięk {Path.GetFileName(path)}: {e.ErrorException?.Message}");
                // Tylko gdy to wciąż bieżący dźwięk — nowszego nie przerywa.
                if (_player != player) return;
                StopPlayback();
                var builtin = AlertSounds.BuiltinPath(kind, AppContext.BaseDirectory);
                try
                {
                    if (File.Exists(builtin)) PlayWav(File.ReadAllBytes(builtin), volume);
                }
                catch (Exception fallbackError) when (fallbackError is IOException or UnauthorizedAccessException)
                {
                    _log($"dźwięk wbudowany: {fallbackError.Message}");
                }
            };
            player.Open(new Uri(path));
            player.Volume = WavVolume.Gain(volume);
            player.Play();
            _player = player;
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or UriFormatException or COMException)
        {
            _log($"dźwięk {Path.GetFileName(path)}: {error.Message}");
            return false;
        }
    }

    private void StopPlayback()
    {
        if (_player is { } player) Release(player);
        if (_wav != IntPtr.Zero)
        {
            // PlaySound(NULL) zatrzymuje dźwięk synchronicznie — dopiero potem wolno zwolnić jego dane.
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            Marshal.FreeHGlobal(_wav);
            _wav = IntPtr.Zero;
        }
    }

    // Zamknięty odtwarzacz zwalnia plik i wątki Windows Media; nie czeka się z tym do następnego dźwięku.
    private void Release(MediaPlayer player)
    {
        player.Stop();
        player.Close();
        if (_player == player) _player = null;
    }

    private void Show(Alert alert)
    {
        if (_appId is null) return;
        var session = alert.Session;
        var (title, body, duration) = alert.Kind == AlertKind.Waiting
            ? ($"Czeka na Ciebie: {session.Name}", string.IsNullOrEmpty(session.Detail) ? "Potrzebna Twoja decyzja." : session.Detail, "long")
            : ($"Gotowe: {session.Name}", string.IsNullOrEmpty(session.Summary) ? "Odpowiedź gotowa." : session.Summary, "short");
        try
        {
            var xml = new XmlDocument();
            // Dźwięk gra widżet (albo nie, gdy go wyłączysz) — powiadomienie jest zawsze ciche.
            xml.LoadXml(
                $"<toast duration='{duration}'><visual><binding template='ToastGeneric'>" +
                $"<text>{Escape(title)}</text><text>{Escape(body)}</text>" +
                $"<text placement='attribution'>{Escape(session.Project)}</text>" +
                "</binding></visual><audio silent='true'/></toast>");
            var toast = new ToastNotification(xml) { Tag = Tag(session.Id), Group = Group };
            var id = session.Id;
            toast.Activated += (_, _) => _activated(id);
            ToastNotificationManager.CreateToastNotifier(_appId).Show(toast);
            _shown[id] = toast;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException)
        {
            _log($"powiadomienie: {error.Message}");
        }
    }

    // Tag powiadomienia może mieć najwyżej 64 znaki.
    private static string Tag(string sessionId) => sessionId.Length > 64 ? sessionId[..64] : sessionId;

    // Podgląd odpowiedzi bywa ucięty w pół emoji albo ma znaki sterujące — takich XML powiadomienia
    // nie przyjmie, a całe powiadomienie by przepadło.
    private static string Escape(string text)
    {
        var safe = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) safe.Append(c).Append(text[++i]);
            else if (System.Xml.XmlConvert.IsXmlChar(c)) safe.Append(c);
        }
        return SecurityElement.Escape(safe.ToString()) ?? "";
    }

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] out string appId);

    // Identyfikator, który Velopack ustawia procesowi przy starcie — ten sam co na skrócie w menu Start.
    private static string? CurrentAppUserModelId()
    {
        try
        {
            return GetCurrentProcessExplicitAppUserModelID(out var appId) == 0 && !string.IsNullOrEmpty(appId) ? appId : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
