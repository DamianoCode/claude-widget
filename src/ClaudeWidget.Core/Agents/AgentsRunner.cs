using System.Diagnostics;
using System.Text.Json;

namespace ClaudeWidget.Core.Agents;

/// <summary>
/// Uruchamia <c>claude agents --json --all</c> w procesie widżetu (bez node'a) i śledzi zmiany
/// względem poprzedniego przebiegu. Port pętli z agents.mjs, wywoływany asynchronicznie, żeby nigdy
/// nie zablokował wątku UI.
/// </summary>
public static class AgentsRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Jeden przebieg: uruchamia claude, parsuje wynik i łączy go z poprzednią listą. Błąd (brak
    /// claude na PATH, zły JSON, upłynięty czas) daje snapshot z <c>Ok = false</c> — widżet wraca
    /// wtedy do pracy na samych hookach, tak jak przy nieudanym przebiegu agents.mjs.
    /// </summary>
    public static async Task<AgentsSnapshot> RunAsync(
        IReadOnlyList<AgentSession> previous,
        long now,
        Func<string, bool> exists,
        string? path,
        string? pathExt,
        CancellationToken cancellationToken)
    {
        try
        {
            var listed = await ListAsync(exists, path, pathExt, cancellationToken).ConfigureAwait(false);
            var sessions = AgentsTracker.Track(listed, previous, now);
            return new AgentsSnapshot(true, now, sessions);
        }
        catch (Exception error) when (error is not OperationCanceledException || cancellationToken.IsCancellationRequested is false)
        {
            return new AgentsSnapshot(false, now, []);
        }
    }

    public static async Task<IReadOnlyList<AgentEntry>> ListAsync(
        Func<string, bool> exists, string? path, string? pathExt, CancellationToken cancellationToken)
    {
        var claude = ClaudeLocator.FindClaude(exists, path, pathExt);
        if (claude is null) throw new InvalidOperationException("nie znaleziono claude w PATH");

        using var process = new Process { StartInfo = BuildStartInfo(claude) };
        process.Start();

        using var timeoutSource = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        string stdout;
        try
        {
            stdout = await process.StandardOutput.ReadToEndAsync(linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("claude agents: przekroczono limit czasu");
        }

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"claude agents: kod {process.ExitCode} {stderr.Trim()}");
        }

        List<AgentEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize(stdout, AgentsJson.Default.ListAgentEntry);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("claude agents --json nie zwrócił listy sesji", error);
        }
        return entries ?? throw new InvalidOperationException("claude agents --json nie zwrócił listy sesji");
    }

    // claude.exe rusza bez powłoki. claude.cmd z npm uruchomi tylko cmd.exe — wtedy całe polecenie
    // idzie jednym napisem, ze ścieżką w cudzysłowie (tak jak zrobiłaby to powłoka).
    private static ProcessStartInfo BuildStartInfo(string claude)
    {
        var runViaShell = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        ProcessStartInfo info;
        if (runViaShell)
        {
            // ArgumentList would re-escape this like any other argument, wrapping the whole thing
            // in one more pair of quotes because the path has spaces — cmd.exe needs exactly two
            // quoted layers (whole command, then just the program), so the raw string goes as-is.
            info = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /s /c \"\"{claude}\" agents --json --all\"",
            };
        }
        else
        {
            info = new ProcessStartInfo(claude);
            info.ArgumentList.Add("agents");
            info.ArgumentList.Add("--json");
            info.ArgumentList.Add("--all");
        }
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        return info;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
