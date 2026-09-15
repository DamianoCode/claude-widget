// Punkt wejścia hooka widżetu Claude Code: `ClaudeWidgetHook.exe hook` i `... statusline`.
// Zastępuje `node hook.mjs` / `node statusline.mjs` z wersji sprzed przenosin na Native AOT —
// żeby widżet działał też u znajomych, którzy zainstalowali Claude Code bez Node.

using System.Text.Json;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Hook;
using ClaudeWidget.Hook;
using StatusLineEngine = ClaudeWidget.Core.StatusLine.StatusLineEngine;

var command = args.Length > 0 ? args[0] : null;
switch (command)
{
    case "hook":
        RunHook();
        break;
    case "statusline":
        RunStatusLine();
        break;
    case "tee":
        RunTee();
        break;
    default:
        // brak albo nieznana subkomenda: cicho, kod 0 — tak samo jak przy błędnym wejściu
        break;
}
return;

static void RunHook()
{
    try
    {
        // obserwacja nigdy nie może zepsuć sesji — hook niczego nie wypisuje i zawsze kończy się
        // kodem 0. PermissionRequest traktuje wypisany tekst jako decyzję o zgodzie.
        if (!AttendedSession.IsAttended()) return;

        var input = ReadInput();
        if (input is null) return;

        var paths = WidgetPaths.FromEnvironment();
        var pid = ParentProcess.GetParentProcessId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HookEngine.Handle(paths, input.Value, pid, now);
    }
    catch
    {
        // ignorowane celowo
    }
}

static void RunStatusLine()
{
    var input = ReadInput() ?? EmptyObject();

    try
    {
        // stan widżetu jest dodatkiem do linii statusu, nie jej warunkiem
        if (AttendedSession.IsAttended())
        {
            var paths = WidgetPaths.FromEnvironment();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            StatusLineEngine.Record(paths, input, now);
        }
    }
    catch
    {
        // ignorowane celowo
    }

    // Claude Code czyta linię statusu jako UTF-8, a Console.Out koduje stroną kodową konsoli —
    // „·” zamieniłoby się w znak sterujący. Bajty idą więc prosto do strumienia, bez BOM.
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(System.Text.Encoding.UTF8.GetBytes(StatusLineEngine.Render(input)));
}

// Przekaźnik przed cudzą statusline (`…ClaudeWidgetHook.exe tee | <komenda>`): zapisuje dane dla
// widżetu i oddaje wejście dalej bajt w bajt, więc cudza linia statusu wygląda jak dotąd.
static void RunTee()
{
    var bytes = ReadAllInput();
    try
    {
        if (AttendedSession.IsAttended() && Parse(bytes) is { } input)
        {
            StatusLineEngine.Record(WidgetPaths.FromEnvironment(), input, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }
    catch
    {
        // zapis dla widżetu nigdy nie może zepsuć cudzej statusline
    }
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(bytes);
}

static byte[] ReadAllInput()
{
    using var stream = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
}

// Bufor wejścia czyta się w całości przed dekodowaniem: znak wielobajtowy na granicy kawałków
// rozpadłby się przy dekodowaniu każdego kawałka osobno.
static JsonElement? ReadInput() => Parse(ReadAllInput());

static JsonElement? Parse(byte[] bytes)
{
    if (bytes.Length == 0) return null;
    try
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

static JsonElement EmptyObject()
{
    using var document = JsonDocument.Parse("{}");
    return document.RootElement.Clone();
}
