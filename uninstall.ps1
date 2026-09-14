# Usuwa widżet Claude Code z tego komputera: zatrzymuje go, zdejmuje autostart, usuwa jego
# hooki i statusline z ~\.claude\settings.json (z kopią zapasową obok) i kasuje ~\.claude\widget.
# Twoje pozostałe ustawienia i hooki zostają nietknięte.
#   -KeepFiles  zostawia ~\.claude\widget (stan sesji, pozycję i log)
param([switch]$KeepFiles)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:USERPROFILE '.claude\widget'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Code widget.lnk'

Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*\.claude\widget\widget.ps1*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }

Remove-Item -LiteralPath $startupLink -ErrorAction SilentlyContinue

& node (Join-Path $PSScriptRoot 'scripts\settings.mjs') --remove
if ($LASTEXITCODE -ne 0) { throw 'Nie udało się usunąć wpisów widżetu z settings.json.' }

if (-not $KeepFiles -and (Test-Path -LiteralPath $target)) {
    Start-Sleep -Milliseconds 500   # proces widżetu zwalnia pliki chwilę po zatrzymaniu
    Remove-Item -LiteralPath $target -Recurse -Force
}
Write-Host 'Widżet usunięty.'
