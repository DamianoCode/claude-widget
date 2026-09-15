# Uruchom w PowerShell 7: pwsh src/ClaudeWidget/Assets/make-sounds.ps1
# Wbudowane dźwięki widżetu — syntetyczne, więc bez cudzych praw. Własne pliki
# ~/.claude/widget/sounds/need.wav i done.wav je zastępują.
#   need.wav — sesja czeka na Ciebie: dwa rosnące tony, jak pytanie
#   done.wav — nowy wynik: dwa opadające, łagodniejsze tony
param([string]$OutDir = (Join-Path $PSScriptRoot 'Sounds'))
$ErrorActionPreference = 'Stop'
$rate = 44100

function Write-Chime([string]$path, [double[]]$notes, [double]$noteSeconds, [double]$volume) {
    $samples = New-Object System.Collections.Generic.List[int16]
    $count = [int]($rate * $noteSeconds)
    $fade = $rate * 0.012
    foreach ($freq in $notes) {
        for ($i = 0; $i -lt $count; $i++) {
            $t = $i / $rate
            # Krótkie narastanie i wykładnicze wygasanie jak dzwonek; końcówka wyciszona, żeby nie trzaskało.
            $envelope = [math]::Min(1, $t / 0.004) * [math]::Exp(-$t * 8) * [math]::Min(1, ($count - $i) / $fade)
            $wave = [math]::Sin(2 * [math]::PI * $freq * $t) + 0.22 * [math]::Sin(4 * [math]::PI * $freq * $t)
            $samples.Add([int16][math]::Round($wave / 1.22 * $envelope * $volume * 32767))
        }
    }
    foreach ($i in 1..([int]($rate * 0.04))) { $samples.Add(0) }

    $data = $samples.Count * 2
    $stream = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($stream)
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $data)); $w.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
    $w.Write([Text.Encoding]::ASCII.GetBytes('fmt ')); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1)
    $w.Write([int]$rate); $w.Write([int]($rate * 2)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]$data)
    foreach ($sample in $samples) { $w.Write($sample) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $stream.ToArray())
    "{0}: {1:0.00} s" -f (Split-Path $path -Leaf), ($samples.Count / $rate)
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Write-Chime (Join-Path $OutDir 'need.wav') @(659.25, 987.77) 0.2 0.45   # E5 → H5
Write-Chime (Join-Path $OutDir 'done.wav') @(783.99, 587.33) 0.22 0.35  # G5 → D5
