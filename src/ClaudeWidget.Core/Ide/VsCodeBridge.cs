using System.Text.Json.Serialization;
using ClaudeWidget.Core.Sessions;

namespace ClaudeWidget.Core.Ide;

/// <summary><c>vscode/windows/&lt;id&gt;.json</c> — stan okna VS Code zapisany przez rozszerzenie-most.</summary>
public sealed record VsCodeWindow
{
    public int ExtensionHostPid { get; init; }

    public IReadOnlyList<string> WorkspaceFolders { get; init; } = [];

    public bool Focused { get; init; }

    /// <summary>PID procesu aktywnego terminala (powłoki albo samego claude); null bez terminala.</summary>
    public int? ActiveTerminalPid { get; init; }

    public IReadOnlyList<int> TerminalPids { get; init; } = [];

    public long UpdatedAt { get; init; }
}

/// <summary><c>vscode/focus-request.json</c> — prośba widżetu o pokazanie terminala sesji.</summary>
public sealed record FocusRequest
{
    public int Version { get; init; } = 1;

    public required string Id { get; init; }

    /// <summary>claude.exe i jego przodkowie aż do okna — terminal VS Code ma PID jednego z nich.</summary>
    public required IReadOnlyList<int> Pids { get; init; }

    public long At { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VsCodeWindow))]
[JsonSerializable(typeof(FocusRequest))]
public sealed partial class BridgeJson : JsonSerializerContext;

/// <summary>
/// Strona widżetu w moście do rozszerzenia VS Code „Claude Code widget bridge” (katalog
/// <c>extension</c>). Rozszerzenie w każdym oknie zapisuje, jakie ma terminale i który jest aktywny;
/// widżet po kliknięciu sesji zapisuje prośbę, a okno z terminalem tej sesji go pokazuje.
/// </summary>
public sealed class VsCodeBridge
{
    private readonly Func<int, bool> _isAlive;

    public VsCodeBridge(string directory, Func<int, bool> isAlive)
    {
        Directory = directory;
        _isAlive = isAlive;
    }

    public static VsCodeBridge For(WidgetPaths paths, Func<int, bool> isAlive) =>
        new(Path.Combine(paths.WidgetDir, "vscode"), isAlive);

    public string Directory { get; }

    public string RequestFile => Path.Combine(Directory, "focus-request.json");

    private string WindowsDir => Path.Combine(Directory, "windows");

    /// <summary>
    /// Okna z działającym rozszerzeniem. Plik okna, którego host rozszerzeń już nie żyje (VS Code
    /// zamknięte bez sprzątania), jest pomijany i usuwany.
    /// </summary>
    public IReadOnlyList<VsCodeWindow> ReadWindows()
    {
        var windows = new List<VsCodeWindow>();
        if (!System.IO.Directory.Exists(WindowsDir)) return windows;
        List<string> files;
        try { files = System.IO.Directory.EnumerateFiles(WindowsDir, "*.json").ToList(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return windows; }

        foreach (var file in files)
        {
            var window = JsonStore.Read(file, BridgeJson.Default.VsCodeWindow);
            if (window is null) continue;
            if (!_isAlive(window.ExtensionHostPid))
            {
                JsonStore.Remove(file);
                continue;
            }
            windows.Add(window);
        }
        return windows;
    }

    /// <summary>Okno, w którym jest terminal z procesem z łańcucha sesji.</summary>
    public static VsCodeWindow? OwnerOf(IReadOnlyList<VsCodeWindow> windows, IReadOnlyList<int> chain) =>
        windows.FirstOrDefault(window => window.TerminalPids.Any(chain.Contains));

    /// <summary>Patrzysz na sesję: jej terminal jest aktywny w oknie, które ma fokus.</summary>
    public static bool IsLookingAt(VsCodeWindow window, IReadOnlyList<int> chain) =>
        window.Focused && window.ActiveTerminalPid is int pid && chain.Contains(pid);

    /// <summary>Nazwa folderu okna do dopasowania tytułu: ten, w którym leży katalog sesji, a bez niego pierwszy.</summary>
    public static string? FolderLeaf(VsCodeWindow window, string cwd)
    {
        var folder = window.WorkspaceFolders
            .Where(candidate => WindowTitleHints.IsInside(cwd, candidate))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault() ?? window.WorkspaceFolders.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var leaf = Path.GetFileName(folder.Replace('/', '\\').TrimEnd('\\'));
        return leaf.Length > 0 ? leaf : null;
    }

    public void RequestFocus(IReadOnlyList<int> chain, long nowMs) =>
        JsonStore.Write(RequestFile, new FocusRequest { Id = Guid.NewGuid().ToString("N"), Pids = chain, At = nowMs }, BridgeJson.Default.FocusRequest);
}
