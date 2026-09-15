using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Sessions;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ClaudeWidget;

/// <summary>
/// Dźwięk i powiadomienie Windows, gdy sesja zaczyna czekać na Ciebie albo kończy z nowym wynikiem.
/// Powiadomienie ma tag sesji: kolejne tej samej sesji zastępuje poprzednie, a znika, gdy sesja
/// przestaje czekać albo przejrzysz wynik. Kliknięcie w nie przenosi do sesji. Własne dźwięki
/// (~/.claude/widget/sounds/need.wav i done.wav) zastępują wbudowane.
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
    private SoundPlayer? _player;

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

    public bool SoundsEnabled { get; set; } = true;

    public bool NotificationsEnabled { get; set; } = true;

    /// <returns>true, gdy dźwięk faktycznie zagrał — tylko wtedy liczy się limit powtórzeń.</returns>
    public bool Raise(Alert alert)
    {
        var played = SoundsEnabled && alert.Sound && PlaySound(alert.Kind);
        if (NotificationsEnabled) Show(alert);
        return played;
    }

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

    private bool PlaySound(AlertKind kind)
    {
        var name = kind == AlertKind.Waiting ? "need.wav" : "done.wav";
        var custom = Path.Combine(_paths.WidgetDir, "sounds", name);
        var path = File.Exists(custom) ? custom : Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", name);
        try
        {
            // Nowe odtwarzanie przerywa poprzednie; odtwarzacz trzyma się do następnego dźwięku.
            _player = new SoundPlayer(path);
            _player.Play();
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        {
            _log($"dźwięk {name}: {error.Message}");
            return false;
        }
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
