# Widżet Claude Code

Mały sygnalizator przy krawędzi ekranu Windows, który pokazuje, co dzieje się w Twoich sesjach
Claude Code — bez przełączania się między terminalami.

- 🔴 **czerwone** — któraś sesja czeka na Ciebie: prośba o zgodę, pytanie albo błąd API (pulsuje;
  przy sesji widać, od ilu minut czeka)
- 🟡 **żółte** — któraś sesja pracuje, także gdy tura się skończyła, a agenci pracują w tle
- 🟢 **zielone** — któraś sesja ma nowy wynik, którego jeszcze nie widziałeś

Każde światło ma licznik sesji w danym stanie. Sesja bezczynna, której wynik już widziałeś,
niczego nie zapala. Obok: limit 5 h i tygodniowy (z prognozą, czy wystarczy do resetu) oraz
zajętość kontekstu każdej sesji.

## Wymagania

- Windows 10 lub 11 (Windows PowerShell 5.1 jest wbudowany)
- [Node.js](https://nodejs.org) 20 lub nowszy w `PATH`
- Claude Code; limity konta pokazują się w planach Pro i Max

## Instalacja

```powershell
gh repo clone DamianoCode/claude-widget
cd claude-widget
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Instalator kopiuje pliki do `~\.claude\widget`, dopisuje hooki i statusline do
`~\.claude\settings.json` (kopia zapasowa ląduje obok jako `settings.json.bak-widget-*`),
dodaje skrót w folderze Autostart i uruchamia widżet. Twoje własne hooki i ustawienia zostają.
Bez autostartu: `.\install.ps1 -NoAutostart`.

**Aktualizacja:** `git pull`, potem ponownie `.\install.ps1`.

**Odinstalowanie:** `powershell -ExecutionPolicy Bypass -File .\uninstall.ps1`
(`-KeepFiles` zostawia stan i pozycję widżetu).

### Gdy masz już własną statusline

Instalator jej nie nadpisuje. Widżet działa wtedy bez limitów i kontekstu, dopóki Twoja
statusline nie przekaże swojego wejścia do `statusline.mjs` widżetu, np. w PowerShellu:

```powershell
$json = [Console]::In.ReadToEnd()
$json | node "$env:USERPROFILE\.claude\widget\statusline.mjs" | Out-Null   # zasila widżet
# ...a dalej Twoja dotychczasowa linia statusu
```

## Obsługa

| Gdzie | Co robi |
|---|---|
| najechanie na sygnalizator | po chwili otwiera panel: lista sesji, limity, podgląd nowych wyników |
| kliknięcie sygnalizatora | przypina panel; drugie kliknięcie go zamyka |
| kliknięcie sesji w panelu | przenosi do okna jej terminala i oznacza wynik jako przejrzany |
| **Ctrl+Alt+K** | przenosi do sesji, która najdłużej czeka na Ciebie (a gdy żadna — do najnowszego wyniku) |
| przeciągnięcie | przesuwa widżet; blisko krawędzi ekranu przykleja się do niej |
| prawy przycisk | widok mini / pełny, przyklejenie do krawędzi, ukrycie, zamknięcie |
| ikona w zasobniku | kolor najpilniejszego stanu; kliknięcie chowa i pokazuje widżet, menu ma też autostart |

Wynik uznaje się za przejrzany, gdy wpiszesz w tej sesji nowe polecenie, klikniesz ją w panelu
albo przez około 3 s patrzysz na jej okno terminala. Widżet chowa się sam, gdy na jego monitorze
działa coś na pełnym ekranie (prezentacja, film).

Ctrl+Alt+C byłoby naturalniejsze, ale na polskiej klawiaturze to AltGr+C, czyli „ć”.

## Jak to działa

- `hook.mjs` dostaje zdarzenia sesji od Claude Code (start, polecenie, prośba o zgodę, koniec
  tury, błąd, koniec sesji) i zapisuje stan sesji w `~\.claude\widget\state\<sesja>.state.json`.
- `statusline.mjs` to jednocześnie linia statusu w terminalu i źródło danych o kontekście sesji
  oraz limitach konta (`<sesja>.usage.json`, `limits.json`).
- `widget.ps1` (WPF) czyta te pliki i rysuje widżet. Sam zapisuje tylko `<sesja>.seen.json` —
  kiedy przejrzałeś wynik — i sprząta pliki sesji zamkniętych bez SessionEnd (zamknięte okno
  terminala, awaria) oraz porzucone pliki starsze niż doba. Sesję uznaje za zamkniętą, gdy jej
  proces nie żyje albo jego PID dostał już inny proces. `start-widget.vbs` uruchamia go bez okna
  konsoli.

- `agents.mjs` pyta `claude agents --json --all` o listę sesji i zapisuje ją do `agents.json` —
  co 4 s, gdy coś pracuje albo czeka w tle lub panel jest otwarty, a poza tym co 20 s. Dzięki temu widżet pokazuje też **sesje w tle** (widok agentów, `claude --bg`,
  `/bg`, `/fork`) — z dopiskiem „w tle”; kliknięcie otwiera taką sesję w nowej karcie terminala
  (`claude attach`). Zna też sesje w terminalu, które nie wysłały jeszcze żadnego zdarzenia.
  Bez `claude` w `PATH` widżet działa dalej, tylko bez sesji w tle.

Sesje uruchamiane ze skryptów (`claude -p`, SDK) nie trafiają do widżetu. Sesja w tle, którą
widżet zastał już skończoną (np. po restarcie komputera), nie zapala zielonego — nowy wynik to
tylko taki, którego zakończenie widżet widział.

## Ograniczenia

- Po zatwierdzeniu długiego polecenia czerwone gaśnie dopiero, gdy polecenie się skończy —
  Claude Code nie zgłasza hookom samej chwili udzielenia zgody.
- Kliknięcie sesji aktywuje okno terminala, ale nie przełącza karty w Windows Terminal.
- Limity odświeżają się tylko wtedy, gdy któraś sesja jest aktywna; „stan z …” mówi, jak są świeże.

## Rozwój

```powershell
npm test
```

Testy uruchamiają `hook.mjs`, `statusline.mjs` i `scripts/settings.mjs` jako osobne procesy,
tak jak robi to Claude Code i instalator. CI dodatkowo sprawdza, że skrypty PowerShell się
parsują i mają BOM — bez niego Windows PowerShell 5.1 psuje polskie znaki.
