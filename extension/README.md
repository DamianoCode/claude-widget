# Claude Code widget bridge

Rozszerzenie do [widżetu Claude Code](https://github.com/DamianoCode/claude-widget) dla Windows.
Gdy klikniesz w widżecie sesję Claude Code, która działa w terminalu VS Code, widżet wyciąga
właściwe okno VS Code na wierzch, a to rozszerzenie pokazuje w nim panel terminala i przełącza
na kartę z tą sesją. Dzięki niemu widżet wie też, czy naprawdę patrzysz na terminal sesji — i dopiero
wtedy uznaje jej nowy wynik za przejrzany.

Widżet instaluje rozszerzenie sam. Nie ma ustawień ani poleceń; komunikuje się z widżetem przez
pliki w `~\.claude\widget\vscode` i niczego nie wysyła poza komputer.
