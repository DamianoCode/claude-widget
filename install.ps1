# Instaluje albo aktualizuje widżet Claude Code na tym komputerze:
#   1. kopiuje pliki z src\ do ~\.claude\widget (stan, pozycja i log widżetu zostają),
#   2. dopisuje hooki i statusline do ~\.claude\settings.json — z kopią zapasową obok,
#   3. dodaje skrót w folderze Autostart i uruchamia widżet.
# Ponowne uruchomienie (np. po `git pull`) aktualizuje instalację i niczego nie dubluje.
#   -NoAutostart  bez skrótu w folderze Autostart
param([switch]$NoAutostart)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'src'
$target = Join-Path $env:USERPROFILE '.claude\widget'
$launcher = Join-Path $target 'start-widget.vbs'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Code widget.lnk'

function Stop-Widget {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like '*\.claude\widget\widget.ps1*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Brak Node.js w PATH. Zainstaluj Node 20 lub nowszy (https://nodejs.org) i uruchom instalator ponownie.'
}
$nodeMajor = [int]((& node --version).TrimStart('v').Split('.')[0])
if ($nodeMajor -lt 20) { throw "Node $nodeMajor jest za stary — widżet potrzebuje wersji 20 lub nowszej." }

Write-Host 'Zatrzymuję działający widżet...'
Stop-Widget
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $target -Force
Write-Host "Pliki widżetu: $target"

& node (Join-Path $PSScriptRoot 'scripts\settings.mjs') --widget-dir $target
if ($LASTEXITCODE -ne 0) { throw 'Nie udało się zaktualizować settings.json — widżet nie jest podłączony do Claude Code.' }

if ($NoAutostart) {
    Remove-Item -LiteralPath $startupLink -ErrorAction SilentlyContinue
    Write-Host 'Autostart: wyłączony'
} else {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($startupLink)
    $link.TargetPath = "$env:WINDIR\System32\wscript.exe"
    $link.Arguments = '"' + $launcher + '"'
    $link.WorkingDirectory = $target
    $link.Description = 'Widżet Claude Code: sygnalizator sesji i limity'
    $link.Save()
    Write-Host 'Autostart: włączony'
}

Start-Process "$env:WINDIR\System32\wscript.exe" -ArgumentList ('"' + $launcher + '"')
Write-Host ''
Write-Host 'Gotowe. Widżet stoi przy prawej krawędzi ekranu, a jego ikona jest w zasobniku systemowym.'
Write-Host 'Otwarte sesje Claude Code pojawią się w nim po najbliższym poleceniu, nowe od razu.'
