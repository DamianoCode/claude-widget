# Widżet Claude Code

Mały sygnalizator przy krawędzi ekranu Windows, który pokazuje, co dzieje się w Twoich sesjach
Claude Code — bez przełączania się między terminalami. Przydaje się najbardziej, gdy pracujesz
w kilku sesjach naraz (np. na różnych worktree) i łatwo zapomnieć, że któraś na Ciebie czeka.

- 🔴 **czerwone** — któraś sesja czeka na Ciebie: prośba o zgodę, pytanie albo błąd API (pulsuje;
  przy sesji widać, od ilu minut czeka)
- 🟡 **żółte** — któraś sesja pracuje, także gdy tura się skończyła, a agenci pracują w tle
- 🟢 **zielone** — któraś sesja ma nowy wynik, którego jeszcze nie widziałeś

Każde światło ma licznik sesji w danym stanie. Sesja bezczynna, której wynik już widziałeś,
niczego nie zapala. Obok: limit 5 h i tygodniowy (z prognozą, czy wystarczy do resetu) oraz
zajętość kontekstu każdej sesji.

## Instalacja

1. Pobierz **`ClaudeWidget-win-Setup.exe`** z [najnowszego wydania](https://github.com/DamianoCode/claude-widget/releases/latest).
2. Uruchom go. Instalator nie jest podpisany, więc Windows pokaże „System Windows ochronił ten
   komputer” — kliknij **Więcej informacji → Uruchom mimo to**.

Instalacja nie wymaga uprawnień administratora ani Node.js. Widżet trafia do
`%LocalAppData%\ClaudeWidget`, dopisuje swoje hooki i statusline do `~\.claude\settings.json`
(kopia zapasowa ląduje obok jako `settings.json.bak-widget-*`) i uruchamia się przy logowaniu.
Twoje własne hooki i ustawienia zostają.

Wymagania: Windows 10 lub 11 (x64) i Claude Code. Limity konta pokazują się w planach Pro i Max.

**Aktualizacje** przychodzą same: widżet przy starcie i co kilka godzin sprawdza nowe wydanie,
pobiera je w tle (zwykle jako małą paczkę różnicową) i instaluje przy następnym uruchomieniu,
np. po restarcie komputera. Kto nie chce czekać, wybiera w menu ikony w zasobniku „Sprawdź
aktualizacje”, a po pobraniu „Zaktualizuj do … i uruchom ponownie”. Zainstalowaną wersję
widać na górze tego menu.

**Odinstalowanie:** Ustawienia Windows → Aplikacje → *Claude Code widget*. Hooki i statusline
widżetu znikają z `settings.json`; stan w `~\.claude\widget` zostaje.

**Przejście z wersji na PowerShellu:** po prostu zainstaluj nową. Instalator zatrzyma stary widżet,
usunie jego skrót z Autostartu, stare skrypty i wpisy w `settings.json`. Stan sesji, pozycja okna
i przejrzane wyniki przechodzą bez zmian.

### Gdy masz już własną statusline

Instalator jej nie nadpisuje i nie zmienia jej wyglądu. Jeśli masz Git Bash (Git for Windows)
i Twoja komenda jest prosta (bez `&&`, `|`, `;`), instalator wstawia przed nią przekaźnik widżetu:
`{ …/ClaudeWidgetRelay.exe tee || cat; } 2>/dev/null | <Twoja komenda>`. Przekaźnik zapisuje dane
o limitach i kontekście i oddaje Twojej komendzie wejście bez zmian — a gdyby go zabrakło, `cat`
oddaje je sam, więc Twoja statusline działa i bez widżetu. Odinstalowanie przywraca komendę
w oryginale; widżet sprawdza to też przy każdym starcie (np. gdy odinstalujesz Git Bash).

W pozostałych przypadkach (złożona komenda, brak Git Bash — wtedy Claude Code uruchamia statusline
przez PowerShell) widżet działa bez limitów i kontekstu, a panel pokazuje podpowiedź. Wystarczy
jedna linia na początku Twojego skryptu, która przekaże mu wejście (wyjście przekaźnika się
wyrzuca, więc wygląd zostaje):

```bash
# bash (np. ~/.claude/statusline.sh)
input=$(cat)
printf '%s' "$input" | "$(cygpath "$LOCALAPPDATA")/ClaudeWidget/current/ClaudeWidgetHook.exe" statusline > /dev/null
# ...a dalej Twoja dotychczasowa linia statusu, czytająca z "$input"
```

```powershell
# PowerShell
$json = [Console]::In.ReadToEnd()
$json | & "$env:LOCALAPPDATA\ClaudeWidget\current\ClaudeWidgetHook.exe" statusline | Out-Null
# ...a dalej Twoja dotychczasowa linia statusu, czytająca z $json
```

## Obsługa

| Gdzie | Co robi |
|---|---|
| najechanie na sygnalizator | po chwili otwiera panel: lista sesji, limity, podgląd nowych wyników |
| kliknięcie sygnalizatora | przypina panel; drugie kliknięcie go zamyka |
| kliknięcie sesji w panelu | przenosi do okna jej terminala i oznacza wynik jako przejrzany |
| **Ctrl+Alt+K** | przenosi do sesji, która najdłużej czeka na Ciebie (a gdy żadna — do najnowszego wyniku) |
| przeciągnięcie | przesuwa widżet; blisko krawędzi ekranu przykleja się do niej |
| „–” w rogu karty / „˅” pod światłami w mini | zmniejsza do widoku mini / rozwija do pełnego (widać je po najechaniu) |
| prawy przycisk | widok mini / pełny, dźwięki, powiadomienia, przyklejenie do krawędzi, ukrycie, zamknięcie |
| ikona w zasobniku | kolor najpilniejszego stanu; kliknięcie chowa i pokazuje widżet, menu ma też wersję, autostart i aktualizacje |

Wynik uznaje się za przejrzany, gdy wpiszesz w tej sesji nowe polecenie, klikniesz ją w panelu
albo przez około 3 s patrzysz na jej okno terminala. Widżet chowa się sam, gdy na jego monitorze
działa coś na pełnym ekranie (prezentacja, film).

Ctrl+Alt+C byłoby naturalniejsze, ale na polskiej klawiaturze to AltGr+C, czyli „ć”.

### Dźwięki i powiadomienia

Gdy sesja zaczyna czekać na Ciebie albo kończy z nowym wynikiem, widżet gra dźwięk (dwa różne)
i pokazuje powiadomienie Windows z nazwą sesji. Kliknięcie w powiadomienie przenosi do sesji,
a samo powiadomienie znika, gdy sesja przestaje czekać albo przejrzysz wynik. Gdy właśnie patrzysz
na terminal tej sesji, widżet milczy. Seria próśb o zgodę w jednej sesji gra najwyżej raz na 15 s.

Oba włącza się i wyłącza w menu pod prawym przyciskiem albo w menu ikony w zasobniku
(„Dźwięki”, „Powiadomienia Windows”). Własne dźwięki: pliki `need.wav` (czeka) i `done.wav`
(nowy wynik) w `~\.claude\widget\sounds` zastępują wbudowane.

### Sesje w terminalu VS Code

Kliknięcie sesji, która działa w terminalu VS Code, wyciąga właściwe okno VS Code (także gdy masz
ich kilka, np. dla różnych worktree), pokazuje panel terminala i przełącza na kartę z tą sesją.
Robi to małe rozszerzenie *Claude Code widget bridge*, które widżet instaluje sam przy starcie
w VS Code (a także w Cursorze i Windsurfie, jeśli ich polecenie jest w `PATH`). Dzięki niemu nowy
wynik uznaje się za przejrzany dopiero wtedy, gdy terminal tej sesji jest aktywny w oknie, na które
patrzysz. Bez rozszerzenia (np. gdy VS Code zainstalujesz po widżecie — dojdzie przy następnym
starcie widżetu) kliknięcie tylko wyciąga właściwe okno.

## Jak to działa

- `ClaudeWidgetHook.exe hook` dostaje zdarzenia sesji od Claude Code (start, polecenie, prośba
  o zgodę, koniec tury, błąd, koniec sesji) i zapisuje stan sesji w
  `~\.claude\widget\state\<sesja>.state.json`. To mały program skompilowany z góry (Native AOT),
  więc startuje w kilkadziesiąt milisekund i nie potrzebuje Node ani runtime .NET.
- `ClaudeWidgetHook.exe statusline` to jednocześnie linia statusu w terminalu i źródło danych
  o kontekście sesji oraz limitach konta (`<sesja>.usage.json`, `limits.json`).
- `ClaudeWidget.exe` (WPF, .NET 10) czyta te pliki i rysuje widżet. Sam zapisuje tylko
  `<sesja>.seen.json` — kiedy przejrzałeś wynik — i sprząta pliki sesji zamkniętych bez
  SessionEnd (zamknięte okno terminala, awaria) oraz porzucone pliki starsze niż doba. Sesję
  uznaje za zamkniętą, gdy jej proces nie żyje albo jego PID dostał już inny proces.
- Widżet pyta też `claude agents --json --all` o listę sesji — co 4 s, gdy coś pracuje albo czeka
  w tle lub panel jest otwarty, a poza tym co 20 s. Dzięki temu pokazuje **sesje w tle** (widok
  agentów, `claude --bg`, `/bg`, `/fork`) z dopiskiem „w tle”; kliknięcie otwiera taką sesję
  w nowej karcie terminala (`claude attach`). Zna też sesje w terminalu, które nie wysłały jeszcze
  żadnego zdarzenia. Bez `claude` w `PATH` widżet działa dalej, tylko bez sesji w tle.

Sesje uruchamiane ze skryptów (`claude -p`, SDK) nie trafiają do widżetu. Sesja w tle, którą
widżet zastał już skończoną (np. po restarcie komputera), nie zapala zielonego — nowy wynik to
tylko taki, którego zakończenie widżet widział.

## Ograniczenia

- Po zatwierdzeniu długiego polecenia czerwone gaśnie dopiero, gdy polecenie się skończy —
  Claude Code nie zgłasza hookom samej chwili udzielenia zgody.
- Kliknięcie sesji aktywuje okno terminala, ale nie przełącza karty w Windows Terminal.
- Limity odświeżają się tylko wtedy, gdy któraś sesja jest aktywna; „stan z …” mówi, jak są świeże.

## Rozwój

Potrzebny [.NET 10 SDK](https://dotnet.microsoft.com/download), a do kompilacji hooka (Native AOT)
także Visual Studio Build Tools z komponentem C++.

```powershell
dotnet build ClaudeWidget.slnx
dotnet test ClaudeWidget.slnx
dotnet run --project src/ClaudeWidget -- --state-dir $env:TEMP\widget-dev\state   # osobna instancja na próbnym stanie
```

| Projekt | Co to jest |
|---|---|
| `src/ClaudeWidget.Core` | logika: stan sesji, hook, statusline, `settings.json`, lista agentów, limity, teksty |
| `src/ClaudeWidget.Hook` | `ClaudeWidgetHook.exe` — hook i statusline (Native AOT) |
| `src/ClaudeWidget` | `ClaudeWidget.exe` — widżet WPF, instalacja i aktualizacje (Velopack) |
| `tests/ClaudeWidget.Tests` | testy xUnit, także `ClaudeWidgetHook.exe` uruchamiany jako proces potomny |

**Wydania** prowadzi [release-please](https://github.com/googleapis/release-please). Po każdym
scaleniu do `main` aktualizuje PR „chore: wydanie X.Y.Z”: numer wersji wynika z typów commitów
(`fix:` → poprawka, `feat:` → nowa funkcja, `feat!:` → nowa wersja główna), a zmiany trafiają do
`CHANGELOG.md`. Scalenie tego PR publikuje wydanie: workflow `release` zbuduje widżet z własnym
runtime .NET i hook, spakuje je [Velopackiem](https://velopack.io) i dołączy `Setup.exe` oraz
paczki, z których zainstalowane widżety same się zaktualizują. Commity `docs:`, `ci:`, `chore:`
itp. nie tworzą nowej wersji.
