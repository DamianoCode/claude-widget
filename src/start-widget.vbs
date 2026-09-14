' Uruchamia widżet Claude Code bez migającego okna konsoli.
' Skrót do tego pliku w folderze Autostart włącza widżet przy logowaniu.
Set shell = CreateObject("WScript.Shell")
dir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
shell.Run "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & dir & "\widget.ps1""", 0, False
