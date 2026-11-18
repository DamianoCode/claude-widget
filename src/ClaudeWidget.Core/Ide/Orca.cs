using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeWidget.Core.Ide;

/// <summary>
/// Miejsce sesji w Orca, zapisane przez hook ze zmiennych środowiskowych, które Orca daje
/// uruchamianym przez siebie procesom: <c>ORCA_TERMINAL_HANDLE</c> ma każdy proces w terminalu Orca,
/// a <c>ORCA_AGENT_SESSION_ID</c> — Claude Code w czacie Orca (czat nie ma terminala).
/// </summary>
public sealed partial record OrcaPlace(string? AgentSessionId, string? TerminalHandle)
{
    public const string AgentSessionVariable = "ORCA_AGENT_SESSION_ID";
    public const string TerminalVariable = "ORCA_TERMINAL_HANDLE";

    public static OrcaPlace? FromEnvironment(Func<string, string?> getEnvironmentVariable) =>
        From(getEnvironmentVariable(AgentSessionVariable), getEnvironmentVariable(TerminalVariable));

    /// <summary>Miejsce z dwóch identyfikatorów; null, gdy żaden nie wygląda jak identyfikator Orca.</summary>
    public static OrcaPlace? From(string? agentSessionId, string? terminalHandle)
    {
        // Wartości trafiają potem jako argumenty do orca.exe — przechodzą tylko znaki, z jakich Orca
        // buduje identyfikatory, i nic, co zaczyna się od „-”, więc nic z cudzego środowiska nie
        // zmieni sensu polecenia (np. w opcję).
        var session = IdentifierRegex().IsMatch(agentSessionId ?? "") ? agentSessionId : null;
        var terminal = IdentifierRegex().IsMatch(terminalHandle ?? "") ? terminalHandle : null;
        return session is null && terminal is null ? null : new OrcaPlace(session, terminal);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.:-]{0,199}\z")]
    private static partial Regex IdentifierRegex();
}

/// <summary>Jak skończyło się pokazywanie sesji w Orca.</summary>
public enum OrcaShown
{
    /// <summary>Orca pokazuje terminal sesji.</summary>
    Terminal,

    /// <summary>Orca pokazuje workspace sesji (czat: CLI Orca nie umie wybrać karty czatu).</summary>
    Workspace,

    /// <summary>Orca nie znalazła ani terminala, ani workspace — zostaje samo wyciągnięte okno.</summary>
    NotFound,
}

/// <summary>Wynik pokazywania i błędy CLI po drodze (także z kroków, po których zadziałał zapasowy).</summary>
public sealed record OrcaShowResult(OrcaShown Shown, IReadOnlyList<string> Errors);

/// <summary>Jedno wywołanie <c>orca … --json</c>: <c>result</c> z odpowiedzi albo kod i opis błędu.</summary>
public sealed record OrcaReply(JsonElement? Result, string? Error)
{
    public bool Ok => Error is null;
}

public interface IOrcaCli
{
    Task<OrcaReply> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>Jak Orca nazywa sesję: tytuł czatu (Orca nadaje go sama i potrafi zmienić) i nazwa workspace.</summary>
public sealed record OrcaLabel(string Title, string Workspace)
{
    /// <summary>
    /// Nazwy z <c>orca agent list --json</c>, według identyfikatora agenta. Null, gdy Orca nie
    /// odpowiedziała — wtedy zostają nazwy z poprzedniego odczytu.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, OrcaLabel>?> ListAsync(IOrcaCli cli, CancellationToken cancellationToken)
    {
        var reply = await cli.RunAsync(["agent", "list", "--json"], cancellationToken).ConfigureAwait(false);
        return reply.Result is { } result ? Parse(result) : null;
    }

    public static IReadOnlyDictionary<string, OrcaLabel> Parse(JsonElement result)
    {
        var labels = new Dictionary<string, OrcaLabel>(StringComparer.Ordinal);
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("agents", out var agents)
            || agents.ValueKind != JsonValueKind.Array)
        {
            return labels;
        }
        foreach (var agent in agents.EnumerateArray())
        {
            var id = Text(agent, "orcaSessionId");
            var title = Text(agent, "title");
            var workspace = Text(agent, "workspace");
            // Świeży czat nie ma jeszcze tytułu — do tego czasu nazywa go workspace.
            if (id.Length > 0 && (title.Length > 0 || workspace.Length > 0))
            {
                labels[id] = new OrcaLabel(title.Length > 0 ? title : workspace, workspace);
            }
        }
        return labels;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : "";
}

/// <summary>
/// Pokazuje w Orca miejsce sesji kliknięte w widżecie, przez publiczne CLI Orca. Terminal wybiera
/// dokładnie; czat — na poziomie workspace, bo tyle udostępnia Orca. Gdy identyfikatory z hooka nie
/// pasują już do niczego, workspace szuka się po katalogu sesji.
/// </summary>
public sealed class OrcaNavigator(IOrcaCli cli)
{
    // Czat zachowuje identyfikator Orca na całe życie (także po /clear), a jego workspace się nie
    // zmienia — drugi klik oszczędza jedno wywołanie CLI.
    private readonly ConcurrentDictionary<string, string> _workspaces = new(StringComparer.Ordinal);

    public async Task<OrcaShowResult> ShowAsync(OrcaPlace place, string cwd, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (place.TerminalHandle is { } terminal
            && await SwitchToTerminalAsync(terminal, errors, cancellationToken).ConfigureAwait(false))
        {
            return new OrcaShowResult(OrcaShown.Terminal, errors);
        }

        if (place.AgentSessionId is { } agent
            && await WorkspaceOfAsync(agent, errors, cancellationToken).ConfigureAwait(false) is { } workspace)
        {
            if (await ActivateAsync($"id:{workspace}", errors, cancellationToken).ConfigureAwait(false))
            {
                return new OrcaShowResult(OrcaShown.Workspace, errors);
            }
            _workspaces.TryRemove(agent, out _);
        }

        if (cwd.Length > 0 && await ActivateAsync($"path:{cwd}", errors, cancellationToken).ConfigureAwait(false))
        {
            return new OrcaShowResult(OrcaShown.Workspace, errors);
        }
        return new OrcaShowResult(OrcaShown.NotFound, errors);
    }

    private async Task<string?> WorkspaceOfAsync(string agent, List<string> errors, CancellationToken cancellationToken)
    {
        if (_workspaces.TryGetValue(agent, out var cached)) return cached;
        var reply = await RunAsync(["agent", "show", agent, "--json"], errors, cancellationToken).ConfigureAwait(false);
        if (reply.Result is not { ValueKind: JsonValueKind.Object } result
            || !result.TryGetProperty("places", out var places)
            || places.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var place in places.EnumerateArray())
        {
            if (place.ValueKind == JsonValueKind.Object
                && place.TryGetProperty("worktreeId", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } workspace)
            {
                _workspaces[agent] = workspace;
                return workspace;
            }
        }
        return null;
    }

    // Orca odpowiada „ok” także wtedy, gdy okno nie przeszło do terminala (np. wygrało nowsze
    // przełączenie) — wtedy `focus.navigated` jest false i warto spróbować workspace.
    private async Task<bool> SwitchToTerminalAsync(string terminal, List<string> errors, CancellationToken cancellationToken)
    {
        var reply = await RunAsync(["terminal", "switch", "--terminal", terminal, "--json"], errors, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok) return false;
        var stayed = reply.Result is { ValueKind: JsonValueKind.Object } result
            && result.TryGetProperty("focus", out var focus) && focus.ValueKind == JsonValueKind.Object
            && focus.TryGetProperty("navigated", out var navigated) && navigated.ValueKind == JsonValueKind.False;
        if (stayed) errors.Add("orca terminal switch: okno nie przeszło do terminala");
        return !stayed;
    }

    private Task<bool> ActivateAsync(string selector, List<string> errors, CancellationToken cancellationToken) =>
        TryAsync(["worktree", "activate", "--worktree", selector, "--json"], errors, cancellationToken);

    private async Task<bool> TryAsync(IReadOnlyList<string> arguments, List<string> errors, CancellationToken cancellationToken) =>
        (await RunAsync(arguments, errors, cancellationToken).ConfigureAwait(false)).Ok;

    private async Task<OrcaReply> RunAsync(IReadOnlyList<string> arguments, List<string> errors, CancellationToken cancellationToken)
    {
        // Nowszy klik przerwał to pokazywanie — kolejne kroki nie mogą już przestawiać Orca.
        var reply = cancellationToken.IsCancellationRequested
            ? new OrcaReply(null, "przerwane")
            : await cli.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok) errors.Add($"orca {arguments[0]} {arguments[1]}: {reply.Error}");
        return reply;
    }
}

/// <summary>
/// <c>orca.exe</c> z instalacji Orca, uruchamiany bez okna. Każdy błąd — brak Orca, wyłączona
/// aplikacja, upłynięty czas, odpowiedź bez <c>"ok": true</c> — wraca jako <see cref="OrcaReply.Error"/>,
/// nigdy jako wyjątek: klik w widżecie nie może się wysypać przez Orca.
/// </summary>
public sealed class OrcaCli(string executable) : IOrcaCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public string Executable { get; } = executable;

    /// <summary>
    /// orca.exe z domyślnej instalacji (instalator Orca na użytkownika), a bez niej — pierwszy z PATH.
    /// orca.cmd z PATH się pomija: przez cmd.exe argumenty musiałyby przejść przez jego cytowanie.
    /// </summary>
    public static OrcaCli? Find(Func<string, bool> exists, string? localAppData, string? path)
    {
        if (!string.IsNullOrEmpty(localAppData))
        {
            var installed = Path.Combine(localAppData, "Programs", "orca", "resources", "bin", "orca.exe");
            if (exists(installed)) return new OrcaCli(installed);
        }
        foreach (var rawEntry in (path ?? "").Split(';'))
        {
            var dir = rawEntry.Trim().Trim('"');
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir, "orca.exe");
            if (exists(candidate)) return new OrcaCli(candidate);
        }
        return null;
    }

    public async Task<OrcaReply> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new OrcaReply(null, $"nie udało się uruchomić orca: {error.Message}");
        }

        using var timeoutSource = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        string stdout;
        try
        {
            // stderr czyta się równolegle: pełny bufor potoku zatrzymałby orca w pół zapisu.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            stdout = stdoutTask.Result;
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new OrcaReply(null, cancellationToken.IsCancellationRequested ? "przerwane" : "przekroczono limit czasu");
        }
        return Parse(stdout);
    }

    /// <summary>Odpowiedź <c>--json</c> Orca: <c>{ ok, result }</c> albo <c>{ ok: false, error: { code } }</c>.</summary>
    public static OrcaReply Parse(string stdout)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new OrcaReply(null, "odpowiedź nie jest obiektem JSON");
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return new OrcaReply(root.TryGetProperty("result", out var result) ? result.Clone() : null, null);
            }
            var code = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
                ? codeValue.GetString()
                : null;
            return new OrcaReply(null, code ?? "nieznany błąd");
        }
        catch (JsonException)
        {
            return new OrcaReply(null, "odpowiedź nie jest JSON-em");
        }
    }
}
