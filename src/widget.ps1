# Widżet Claude Code: sygnalizator stanu sesji i limity konta, przyklejony do krawędzi ekranu.
# Czyta to, co hook.mjs i statusline.mjs zapisują w katalogu stanu. Sam zapisuje tam tylko
# znaczniki <sesja>.seen.json — kiedy przejrzałeś wynik danej sesji.
#   -StateDir  katalog stanu (domyślnie ~/.claude/widget/state); inny katalog = osobna instancja
#
# Światła są niezależne, każde z licznikiem sesji:
#   czerwone — któraś sesja czeka na Ciebie (pulsuje)
#   żółte    — któraś pracuje, także w tle
#   zielone  — któraś ma nowy, jeszcze nieprzejrzany wynik
# Sesja bezczynna, której wynik już widziałeś, nie zapala niczego.
#
# Uruchamiany przez start-widget.vbs (bez okna konsoli). Wymaga Windows PowerShell w trybie STA.
param([string]$StateDir = (Join-Path $env:USERPROFILE '.claude\widget\state'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing

Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WidgetNative {
    public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int GWL_STYLE = -16;
    const int WS_CAPTION = 0x00C00000;

    public static string ClassOf(IntPtr hwnd) { var text = new StringBuilder(256); GetClassName(hwnd, text, 256); return text.ToString(); }
    public static string TitleOf(IntPtr hwnd) { var text = new StringBuilder(512); GetWindowText(hwnd, text, 512); return text.ToString(); }
    public static uint ProcessOf(IntPtr hwnd) { uint pid; GetWindowThreadProcessId(hwnd, out pid); return pid; }
    public static IntPtr MonitorOf(IntPtr hwnd) { return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST); }

    // Pełny ekran: okno bez paska tytułu, które zakrywa cały monitor (prezentacja, film, gra).
    // Zmaksymalizowane okno ma pasek tytułu, więc się nie liczy — nawet przy ukrytym pasku zadań.
    public static bool IsFullscreen(IntPtr hwnd) {
        RECT rect;
        if (!GetWindowRect(hwnd, out rect)) return false;
        if ((GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;
        var info = new MONITORINFO();
        info.Size = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(MonitorOf(hwnd), ref info)) return false;
        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
            && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    // Widoczne okno procesu, najlepiej to, którego tytuł zawiera podany tekst (nazwę sesji);
    // proces terminala może mieć kilka okien.
    public static IntPtr FindWindowOf(uint pid, string titlePart) {
        IntPtr best = IntPtr.Zero, first = IntPtr.Zero;
        EnumWindows(delegate (IntPtr hwnd, IntPtr param) {
            if (!IsWindowVisible(hwnd) || ProcessOf(hwnd) != pid) return true;
            string title = TitleOf(hwnd);
            if (title.Length == 0) return true;
            if (first == IntPtr.Zero) first = hwnd;
            if (!string.IsNullOrEmpty(titlePart) && title.Contains(titlePart)) { best = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return best != IntPtr.Zero ? best : first;
    }
}

// Globalny skrót klawiszowy: ukryte okno odbiera WM_HOTKEY i zgłasza zdarzenie Pressed.
// Proces, który odebrał skrót, może przełączyć inne okno na pierwszy plan.
public class WidgetHotkey : System.Windows.Forms.NativeWindow, IDisposable {
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    const int WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;

    public event EventHandler Pressed;
    public bool Registered { get; private set; }

    public WidgetHotkey(uint modifiers, uint key) {
        CreateHandle(new System.Windows.Forms.CreateParams());
        Registered = RegisterHotKey(Handle, 1, modifiers | MOD_NOREPEAT, key);
    }

    protected override void WndProc(ref System.Windows.Forms.Message message) {
        if (message.Msg == WM_HOTKEY && Pressed != null) Pressed(this, EventArgs.Empty);
        base.WndProc(ref message);
    }

    public void Dispose() {
        if (Registered) UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}
'@

$WidgetDir = Split-Path $StateDir -Parent
$ConfigPath = Join-Path $WidgetDir 'widget-config.json'
$LogPath = Join-Path $WidgetDir 'widget.log'
$StartupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Code widget.lnk'

# Jedna instancja na katalog stanu: drugi start po prostu się kończy. Tę samą nazwę sprawdza
# notify.ps1, żeby nie dublować powiadomienia „Gotowe”, gdy widżet działa.
$mutex = New-Object Threading.Mutex($false, ('Local\ClaudeCodeWidget-' + ($StateDir.ToLowerInvariant() -replace '[^a-z0-9]', '_')))
if (-not $mutex.WaitOne(0)) { exit 0 }

# Światła sygnalizatora i rodzaje sesji. Rodzaj „bezczynna” nie ma własnego światła.
$Lights = [ordered]@{
    czeka   = @{ On = '#FF5A4E'; Off = '#2B1614' }
    pracuje = @{ On = '#FFB224'; Off = '#2B2211' }
    gotowe  = @{ On = '#3DD68C'; Off = '#11261B' }
}
$Kinds = @{
    czeka     = @{ Light = 'czeka'; Label = 'Czeka na Ciebie'; Color = '#FF5A4E'; Rank = 4 }
    nowe      = @{ Light = 'gotowe'; Label = 'Nowy wynik'; Color = '#3DD68C'; Rank = 3 }
    pracuje   = @{ Light = 'pracuje'; Label = 'Pracuje'; Color = '#FFB224'; Rank = 2 }
    bezczynna = @{ Light = $null; Label = 'Nic nie czeka'; Color = '#6E6E6E'; Rank = 1 }
}
# Sesja bez PID (sprzed wersji hooka z PID) i bez zdarzeń od 12 h najpewniej już nie istnieje.
$StaleMs = 12 * 3600 * 1000
$LimitWindowMs = @{ fiveHour = 5 * 3600 * 1000; sevenDay = 7 * 86400 * 1000 }
$Days = @('nd', 'pn', 'wt', 'śr', 'czw', 'pt', 'sob')
$CardInner = 122      # szerokość treści karty: 148 - ramka 2 - padding 20 - margines 4
$PanelInner = 306     # szerokość treści panelu: 340 - ramka 2 - padding 28 - margines 4
$ShadowMargin = 24    # margines okna, w którym mieści się cień karty
$SnapDistance = 24    # z tej odległości karta przykleja się do krawędzi obszaru roboczego
$SeenAfterMs = 3000   # tyle trzeba patrzeć na terminal sesji, żeby jej wynik uznać za przejrzany
$Sizes = @('mini', 'full')
# Ctrl+Alt+K. Ctrl+Alt+litera to na polskiej klawiaturze AltGr+litera, więc odpadają litery
# z ogonkami (a, c, e, l, n, o, s, x, z) — skrót zabierałby „ć” czy „ś”.
$HotkeyModifiers = 3   # MOD_ALT | MOD_CONTROL
$HotkeyKey = 0x4B      # K
$HotkeyLabel = 'Ctrl+Alt+K'

function Write-WidgetLog([string]$message) {
    try { Add-Content -Path $LogPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $message" -Encoding UTF8 } catch { }
}

# --- kolory i pędzle ---------------------------------------------------------

$BrushCache = @{}
function Get-Brush([string]$hex) {
    if (-not $BrushCache.ContainsKey($hex)) {
        $brush = New-Object Windows.Media.SolidColorBrush (Get-Color $hex)
        $brush.Freeze()
        $BrushCache[$hex] = $brush
    }
    $BrushCache[$hex]
}

function Get-Color([string]$hex) { [Windows.Media.Color][Windows.Media.ColorConverter]::ConvertFromString($hex) }

function Get-MixedColor([Windows.Media.Color]$color, [Windows.Media.Color]$with, [double]$amount) {
    [Windows.Media.Color]::FromRgb(
        [byte]($color.R + ($with.R - $color.R) * $amount),
        [byte]($color.G + ($with.G - $color.G) * $amount),
        [byte]($color.B + ($with.B - $color.B) * $amount))
}

function New-Glow([Windows.Media.Color]$color, [double]$blur, [double]$opacity) {
    $effect = New-Object Windows.Media.Effects.DropShadowEffect
    $effect.Color = $color
    $effect.BlurRadius = $blur
    $effect.ShadowDepth = 0
    $effect.Opacity = $opacity
    $effect.Freeze()
    $effect
}

# Zapalone światło: jaśniejszy odblask u góry, pełny kolor w środku, ciemniejszy brzeg i poświata.
$LitFill = @{}; $Glow = @{}; $MiniGlow = @{}; $DotGlow = @{}
foreach ($key in $Lights.Keys) {
    $on = Get-Color $Lights[$key].On
    $gradient = New-Object Windows.Media.RadialGradientBrush
    $gradient.GradientOrigin = [Windows.Point]::new(0.35, 0.3)
    $gradient.Center = [Windows.Point]::new(0.42, 0.4)
    $gradient.RadiusX = 0.7
    $gradient.RadiusY = 0.7
    $gradient.GradientStops.Add([Windows.Media.GradientStop]::new((Get-MixedColor $on ([Windows.Media.Colors]::White) 0.6), 0.0))
    $gradient.GradientStops.Add([Windows.Media.GradientStop]::new($on, 0.5))
    $gradient.GradientStops.Add([Windows.Media.GradientStop]::new((Get-MixedColor $on ([Windows.Media.Colors]::Black) 0.2), 1.0))
    $gradient.Freeze()
    $LitFill[$key] = $gradient
    $Glow[$key] = New-Glow $on 26 0.9
    $MiniGlow[$key] = New-Glow $on 14 0.9
}
foreach ($kind in $Kinds.Keys) { $DotGlow[$kind] = if ($kind -eq 'bezczynna') { $null } else { New-Glow (Get-Color $Kinds[$kind].Color) 10 0.8 } }

# --- okno --------------------------------------------------------------------

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Claude Code" WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize" SizeToContent="WidthAndHeight"
        FontFamily="Segoe UI Variable Text, Segoe UI" UseLayoutRounding="True">
  <Window.Resources>
    <Style x:Key="Count" TargetType="TextBlock">
      <Setter Property="HorizontalAlignment" Value="Center"/>
      <Setter Property="VerticalAlignment" Value="Center"/>
      <Setter Property="FontWeight" Value="Bold"/>
      <Setter Property="Foreground" Value="#E61A1A1A"/>
      <Setter Property="Visibility" Value="Collapsed"/>
    </Style>
    <Style x:Key="Row" TargetType="Border">
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="#14FFFFFF"/></Trigger>
      </Style.Triggers>
    </Style>
    <Style x:Key="WaitingRow" TargetType="Border">
      <Setter Property="Background" Value="#14FF5A4E"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="#26FF5A4E"/></Trigger>
      </Style.Triggers>
    </Style>
  </Window.Resources>
  <Grid Margin="24">
    <Border x:Name="Card" Width="148" Background="#202020" BorderBrush="#14FFFFFF" BorderThickness="1"
            CornerRadius="14" Padding="10,10,10,12" Visibility="Collapsed">
      <Border.Effect>
        <DropShadowEffect BlurRadius="30" ShadowDepth="6" Direction="270" Opacity="0.55" Color="Black"/>
      </Border.Effect>
      <StackPanel>
        <Grid Margin="2,0,2,12">
          <Canvas Width="12" Height="12" HorizontalAlignment="Left" VerticalAlignment="Center">
            <Ellipse Canvas.Left="2.9" Canvas.Top="1.4" Width="2.2" Height="2.2" Fill="#5E5E5E"/>
            <Ellipse Canvas.Left="6.9" Canvas.Top="1.4" Width="2.2" Height="2.2" Fill="#5E5E5E"/>
            <Ellipse Canvas.Left="2.9" Canvas.Top="4.9" Width="2.2" Height="2.2" Fill="#5E5E5E"/>
            <Ellipse Canvas.Left="6.9" Canvas.Top="4.9" Width="2.2" Height="2.2" Fill="#5E5E5E"/>
            <Ellipse Canvas.Left="2.9" Canvas.Top="8.4" Width="2.2" Height="2.2" Fill="#5E5E5E"/>
            <Ellipse Canvas.Left="6.9" Canvas.Top="8.4" Width="2.2" Height="2.2" Fill="#5E5E5E"/>
          </Canvas>
          <TextBlock Text="Claude Code" FontSize="11" Foreground="#8A8A8A" HorizontalAlignment="Center"/>
        </Grid>
        <Border Background="#141414" BorderBrush="#0AFFFFFF" BorderThickness="1" CornerRadius="12" Padding="0,10,0,2">
          <StackPanel HorizontalAlignment="Center">
            <Border Width="56" Height="56" CornerRadius="28" Background="#0A0A0A" Margin="0,0,0,8">
              <Grid><Ellipse x:Name="LightCzeka" Width="42" Height="42"/><TextBlock x:Name="CountCzeka" Style="{StaticResource Count}" FontSize="17"/></Grid>
            </Border>
            <Border Width="56" Height="56" CornerRadius="28" Background="#0A0A0A" Margin="0,0,0,8">
              <Grid><Ellipse x:Name="LightPracuje" Width="42" Height="42"/><TextBlock x:Name="CountPracuje" Style="{StaticResource Count}" FontSize="17"/></Grid>
            </Border>
            <Border Width="56" Height="56" CornerRadius="28" Background="#0A0A0A" Margin="0,0,0,8">
              <Grid><Ellipse x:Name="LightGotowe" Width="42" Height="42"/><TextBlock x:Name="CountGotowe" Style="{StaticResource Count}" FontSize="17"/></Grid>
            </Border>
          </StackPanel>
        </Border>
        <StackPanel Margin="2,12,2,0">
          <TextBlock x:Name="StatusLabel" FontSize="14" FontWeight="SemiBold" Foreground="#8A8A8A"/>
          <TextBlock x:Name="StatusDetail" FontSize="12" Foreground="#A6A6A6" Margin="0,2,0,0" TextTrimming="CharacterEllipsis"/>
          <TextBlock x:Name="SessionLine" FontSize="11" Foreground="#767676" Margin="0,4,0,0" TextTrimming="CharacterEllipsis"/>
        </StackPanel>
        <Border Height="1" Background="#0FFFFFFF" Margin="0,12,0,12"/>
        <StackPanel x:Name="Meters" Margin="2,0,2,0"/>
        <TextBlock x:Name="Freshness" FontSize="10.5" Foreground="#626262" HorizontalAlignment="Center" Margin="0,2,0,0"/>
      </StackPanel>
    </Border>
    <Border x:Name="Mini" Width="34" Background="#202020" BorderBrush="#14FFFFFF" BorderThickness="1"
            CornerRadius="17" Padding="0,5,0,3" Visibility="Collapsed">
      <Border.Effect>
        <DropShadowEffect BlurRadius="20" ShadowDepth="4" Direction="270" Opacity="0.5" Color="Black"/>
      </Border.Effect>
      <StackPanel HorizontalAlignment="Center">
        <Border Width="24" Height="24" CornerRadius="12" Background="#0A0A0A" Margin="0,0,0,2">
          <Grid><Ellipse x:Name="MiniCzeka" Width="17" Height="17"/><TextBlock x:Name="MiniCountCzeka" Style="{StaticResource Count}" FontSize="10"/></Grid>
        </Border>
        <Border Width="24" Height="24" CornerRadius="12" Background="#0A0A0A" Margin="0,0,0,2">
          <Grid><Ellipse x:Name="MiniPracuje" Width="17" Height="17"/><TextBlock x:Name="MiniCountPracuje" Style="{StaticResource Count}" FontSize="10"/></Grid>
        </Border>
        <Border Width="24" Height="24" CornerRadius="12" Background="#0A0A0A" Margin="0,0,0,2">
          <Grid><Ellipse x:Name="MiniGotowe" Width="17" Height="17"/><TextBlock x:Name="MiniCountGotowe" Style="{StaticResource Count}" FontSize="10"/></Grid>
        </Border>
      </StackPanel>
    </Border>
    <Popup x:Name="Panel" Placement="Left" AllowsTransparency="True" StaysOpen="True" PopupAnimation="Fade">
      <Grid Margin="16,16,4,16">
        <Border x:Name="PanelBody" Width="340" Background="#2B2B2B" BorderBrush="#14FFFFFF" BorderThickness="1"
                CornerRadius="10" Padding="14">
          <Border.Effect>
            <DropShadowEffect BlurRadius="30" ShadowDepth="6" Direction="270" Opacity="0.55" Color="Black"/>
          </Border.Effect>
          <StackPanel>
            <Grid Margin="0,0,0,10">
              <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                <TextBlock Text="Sesje" FontSize="13" FontWeight="SemiBold" Foreground="#F3F3F3"/>
                <Border CornerRadius="9" Background="#14FFFFFF" Padding="7,1,7,1" Margin="8,0,0,0">
                  <TextBlock x:Name="PanelCount" FontSize="11" Foreground="#C8C8C8"/>
                </Border>
              </StackPanel>
              <TextBlock x:Name="PanelFreshness" FontSize="11" Foreground="#767676" HorizontalAlignment="Right" VerticalAlignment="Center"/>
            </Grid>
            <StackPanel x:Name="SessionList"/>
            <Border Height="1" Background="#0FFFFFFF" Margin="0,12,0,12"/>
            <StackPanel x:Name="PanelLimits" Margin="2,0,2,0"/>
            <TextBlock x:Name="PanelHint" FontSize="11" Foreground="#6E6E6E" Margin="2,12,2,0" TextWrapping="Wrap"/>
          </StackPanel>
        </Border>
      </Grid>
    </Popup>
  </Grid>
</Window>
'@

$window = [Windows.Markup.XamlReader]::Load((New-Object Xml.XmlNodeReader $xaml))
foreach ($name in 'Card', 'Mini', 'LightCzeka', 'LightPracuje', 'LightGotowe', 'CountCzeka', 'CountPracuje', 'CountGotowe',
    'MiniCzeka', 'MiniPracuje', 'MiniGotowe', 'MiniCountCzeka', 'MiniCountPracuje', 'MiniCountGotowe',
    'StatusLabel', 'StatusDetail', 'SessionLine', 'Meters', 'Freshness', 'Panel', 'PanelBody', 'PanelCount',
    'PanelFreshness', 'SessionList', 'PanelLimits', 'PanelHint') {
    Set-Variable -Name $name -Value $window.FindName($name)
}
$RowStyle = $window.FindResource('Row')
$WaitingRowStyle = $window.FindResource('WaitingRow')

# Oba rozmiary świecą tak samo; różnią się tylko wielkością poświaty.
$LightSets = @(
    @{ Ellipses = @{ czeka = $LightCzeka; pracuje = $LightPracuje; gotowe = $LightGotowe }
        Counts = @{ czeka = $CountCzeka; pracuje = $CountPracuje; gotowe = $CountGotowe }; Glow = $Glow },
    @{ Ellipses = @{ czeka = $MiniCzeka; pracuje = $MiniPracuje; gotowe = $MiniGotowe }
        Counts = @{ czeka = $MiniCountCzeka; pracuje = $MiniCountPracuje; gotowe = $MiniCountGotowe }; Glow = $MiniGlow }
)

$script:Size = 'mini'
$script:Hwnd = [IntPtr]::Zero
$script:Sessions = @()
$script:Pinned = $false
$script:Pulsing = $false
$script:UserHidden = $false
$script:FullscreenHidden = $false
$script:PanelSignature = ''
$script:StateStamp = [DateTime]::MinValue
$script:Dwell = @{}
$script:TrayKey = ''
$script:TrayHandle = [IntPtr]::Zero
$script:Hotkey = $null
$HostCache = @{}

# --- elementy budowane w kodzie ----------------------------------------------

function New-Text([string]$text, [double]$size, [string]$color) {
    $block = New-Object Windows.Controls.TextBlock
    $block.Text = $text
    $block.FontSize = $size
    $block.Foreground = Get-Brush $color
    $block
}

function New-Meter([string]$label, [double]$width, [double]$labelSize, [string]$labelColor, [double]$bottom) {
    $root = New-Object Windows.Controls.StackPanel
    $root.Margin = "0,0,0,$bottom"
    $head = New-Object Windows.Controls.Grid
    $value = New-Text '' 12 '#F3F3F3'
    $value.FontWeight = 'SemiBold'
    $value.HorizontalAlignment = 'Right'
    [Windows.Documents.Typography]::SetNumeralAlignment($value, 'Tabular')
    [void]$head.Children.Add((New-Text $label $labelSize $labelColor))
    [void]$head.Children.Add($value)
    $track = New-Object Windows.Controls.Border
    $track.Height = 4
    $track.CornerRadius = 2
    $track.Background = Get-Brush '#14FFFFFF'
    $track.Margin = '0,4,0,0'
    $fill = New-Object Windows.Controls.Border
    $fill.Height = 4
    $fill.CornerRadius = 2
    $fill.HorizontalAlignment = 'Left'
    $fill.Width = 0
    $track.Child = $fill
    $note = New-Text '' 10.5 '#767676'
    $note.Margin = '0,4,0,0'
    $note.TextTrimming = 'CharacterEllipsis'
    [void]$root.Children.Add($head)
    [void]$root.Children.Add($track)
    [void]$root.Children.Add($note)
    @{ Root = $root; Value = $value; Fill = $fill; Note = $note; Width = $width }
}

function Set-Meter($meter, $pct, [string]$note, [string]$color) {
    if ($null -eq $pct) {
        $meter.Value.Text = '—'
        $meter.Fill.Width = 0
    } else {
        $clamped = [math]::Max(0, [math]::Min(100, [double]$pct))
        $meter.Value.Text = ('{0:0}%' -f $clamped)
        $meter.Fill.Width = $meter.Width * $clamped / 100
        $meter.Fill.Background = Get-Brush $color
    }
    $meter.Note.Text = $note
}

$M5h = New-Meter 'Limit 5 h' $CardInner 11 '#A6A6A6' 10
$MWeek = New-Meter 'Tydzień' $CardInner 11 '#A6A6A6' 10
$MContext = New-Meter 'Kontekst sesji' $CardInner 11 '#A6A6A6' 8
foreach ($meter in $M5h, $MWeek, $MContext) { [void]$Meters.Children.Add($meter.Root) }

$P5h = New-Meter 'Limit 5 h' $PanelInner 12 '#C8C8C8' 10
$PWeek = New-Meter 'Limit tygodniowy' $PanelInner 12 '#C8C8C8' 0
foreach ($meter in $P5h, $PWeek) { [void]$PanelLimits.Children.Add($meter.Root) }

# --- dane --------------------------------------------------------------------

# Hook i statusline podmieniają pliki w trakcie, więc czyta się je z pełnym udostępnieniem.
function Read-SharedJson([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
            return ($reader.ReadToEnd() | ConvertFrom-Json)
        } finally {
            $stream.Dispose()
        }
    } catch {
        return $null
    }
}

# Emituje sesje do potoku; wywołujący zbiera je przez @(...), co działa dla 0, 1 i wielu.
# Sesja, której proces Claude Code już nie żyje, zamknęła się bez SessionEnd — pomija się ją.
function Get-Sessions([double]$nowMs) {
    if (-not (Test-Path -LiteralPath $StateDir)) { return }
    $raw = New-Object Collections.Generic.List[object]
    foreach ($file in Get-ChildItem -LiteralPath $StateDir -Filter '*.state.json' -File) {
        $state = Read-SharedJson $file.FullName
        if ($state -and $Lights.Contains("$($state.state)")) { $raw.Add(@{ Id = ($file.Name -replace '\.state\.json$', ''); State = $state }) }
    }

    $pids = @($raw | ForEach-Object { $_.State.pid } | Where-Object { $_ })
    $alive = @{}
    if ($pids.Count) {
        Get-Process -Id $pids -ErrorAction SilentlyContinue | ForEach-Object { $alive[[int]$_.Id] = $_.ProcessName }
    }

    $sessions = New-Object Collections.Generic.List[object]
    foreach ($entry in $raw) {
        $state = $entry.State
        $id = $entry.Id
        if ($state.pid) {
            # Nazwa chroni przed ponownym użyciem PID przez zupełnie inny program.
            $processName = $alive[[int]$state.pid]
            if (-not $processName -or $processName -notmatch '^(claude|node)') { continue }
        } elseif ($nowMs - [double]$state.updatedAt -gt $StaleMs) {
            continue
        }
        $usage = Read-SharedJson (Join-Path $StateDir "$id.usage.json")
        $seen = Read-SharedJson (Join-Path $StateDir "$id.seen.json")
        $seenAt = if ($seen) { [double]$seen.seenAt } else { 0 }
        $since = [double]$state.since
        $kind = switch ("$($state.state)") {
            'gotowe' { if ($state.fresh -eq $true -and $seenAt -lt $since) { 'nowe' } else { 'bezczynna' } }
            default { "$($state.state)" }
        }
        $project = if ($usage -and $usage.project) { "$($usage.project)" } elseif ($state.cwd) { Split-Path "$($state.cwd)" -Leaf } else { 'sesja' }
        $sessions.Add([pscustomobject]@{
                Id            = $id
                Kind          = $kind
                Since         = $since
                Detail        = "$($state.detail)"
                Summary       = "$($state.summary)"
                Background    = [int]$state.background
                Pid           = if ($state.pid) { [int]$state.pid } else { 0 }
                Project       = $project
                Name          = if ($usage -and $usage.name) { "$($usage.name)" } else { $project }
                ContextPct    = if ($usage) { $usage.contextPct } else { $null }
                ContextTokens = if ($usage) { $usage.contextTokens } else { $null }
                ContextSize   = if ($usage) { $usage.contextSize } else { $null }
            })
    }
    # Najpilniejsza sesja na początku; przy remisie ta, która zmieniła stan najpóźniej.
    $sessions | Sort-Object @{ Expression = { $Kinds[$_.Kind].Rank }; Descending = $true }, @{ Expression = { $_.Since }; Descending = $true }
}

function Set-Seen([string]$id) {
    try {
        [IO.File]::WriteAllText((Join-Path $StateDir "$id.seen.json"), ('{"seenAt":' + [DateTimeOffset]::Now.ToUnixTimeMilliseconds() + '}'))
    } catch {
        Write-WidgetLog "znacznik przejrzenia: $_"
    }
}

# Proces z oknem, w którym działa sesja: idzie się w górę od claude.exe (np. przez pwsh.exe)
# aż do terminala. Wynik zapamiętuje się na całe życie procesu sesji.
function Resolve-HostPid([int]$claudePid) {
    if ($HostCache.ContainsKey($claudePid)) { return $HostCache[$claudePid] }
    $found = 0
    $id = $claudePid
    for ($depth = 0; $depth -lt 6 -and $id; $depth++) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue
        if (-not $process -or $process.Name -eq 'explorer.exe') { break }
        $windowed = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($windowed -and $windowed.MainWindowHandle -ne [IntPtr]::Zero) { $found = $id; break }
        $id = [int]$process.ParentProcessId
    }
    $HostCache[$claudePid] = $found
    $found
}

function Show-SessionTerminal($session) {
    Set-Seen $session.Id
    if (-not $session.Pid) { return }
    $hostPid = Resolve-HostPid $session.Pid
    if (-not $hostPid) { return }
    $target = [WidgetNative]::FindWindowOf([uint32]$hostPid, $session.Name)
    if ($target -eq [IntPtr]::Zero) { return }
    if ([WidgetNative]::IsIconic($target)) { [void][WidgetNative]::ShowWindow($target, 9) }   # SW_RESTORE
    [void][WidgetNative]::SetForegroundWindow($target)
}

# Skrót klawiszowy: sesja, która najdłużej czeka na Ciebie; gdy żadna nie czeka — najnowszy wynik.
function Invoke-Jump {
    $waiting = @($script:Sessions | Where-Object { $_.Kind -eq 'czeka' } | Sort-Object Since)
    $target = if ($waiting.Count) { $waiting[0] } else {
        @($script:Sessions | Where-Object { $_.Kind -eq 'nowe' } | Sort-Object Since -Descending) | Select-Object -First 1
    }
    if ($target) {
        Show-SessionTerminal $target
        Update-View
    }
}

# --- formatowanie ------------------------------------------------------------

function Format-Span([double]$ms) {
    $seconds = [math]::Floor([math]::Max(0, $ms) / 1000)
    if ($seconds -lt 60) { return "$seconds s" }
    if ($seconds -lt 3600) { return "$([math]::Floor($seconds / 60)) min" }
    if ($seconds -lt 86400) {
        $hours = [math]::Floor($seconds / 3600)
        $minutes = [math]::Floor(($seconds % 3600) / 60)
        if ($minutes) { return "$hours h $minutes min" } else { return "$hours h" }
    }
    "$([math]::Floor($seconds / 86400)) d"
}

function Format-Sessions([int]$count) {
    $word = if ($count -eq 1) { 'sesja' }
    elseif (($count % 10) -in 2..4 -and ($count % 100) -notin 12..14) { 'sesje' }
    else { 'sesji' }
    "$count $word"
}

function Format-Tokens([double]$tokens) {
    if ($tokens -ge 1000000) { return ('{0:0.#}' -f ($tokens / 1000000)) + ' mln' }
    ('{0:0}' -f ($tokens / 1000)) + ' tys.'
}

function Get-LocalTime([double]$ms) { [DateTimeOffset]::FromUnixTimeMilliseconds([long]$ms).LocalDateTime }

function Format-Moment([double]$ms, [bool]$withDay) {
    $at = Get-LocalTime $ms
    if ($withDay) { "$($Days[[int]$at.DayOfWeek]) $($at.ToString('HH:mm'))" } else { $at.ToString('HH:mm') }
}

function Get-FreshnessText($limits) {
    if (-not $limits -or -not $limits.updatedAt) { return 'brak danych o limitach' }
    $at = Get-LocalTime ([double]$limits.updatedAt)
    if ($at.Date -eq (Get-Date).Date) { "stan z $($at.ToString('HH:mm'))" } else { "stan z $($at.ToString('dd.MM HH:mm'))" }
}

# Limit konta: procent, kolor i opis. Kolor zależy od tempa, nie tylko od procentu —
# 60% po 4 godzinach okna to spokój, 60% po 30 minutach oznacza, że zabraknie przed resetem.
function Get-LimitView($limit, [string]$window, $measuredAt, [double]$nowMs, [bool]$panel) {
    $view = @{ Pct = $null; Color = '#D97757'; Note = ''; Warn = $null }
    if (-not $limit -or $null -eq $limit.pct) { return $view }
    $withDay = $window -eq 'sevenDay'
    $resetMs = if ($null -ne $limit.resetsAt) { [double]$limit.resetsAt * 1000 } else { $null }
    if ($resetMs -and $nowMs -ge $resetMs) {
        $view.Note = 'po resecie, czekam na nowe dane'
        return $view
    }

    $pct = [double]$limit.pct
    $view.Pct = $pct
    $exhaustMs = $null
    if ($resetMs -and $measuredAt -and $pct -gt 0) {
        $startMs = $resetMs - $LimitWindowMs[$window]
        $elapsed = ([double]$measuredAt - $startMs) / $LimitWindowMs[$window]
        # Na samym początku okna tempo jest jeszcze szumem.
        if ($elapsed -ge 0.1) {
            $hitMs = $startMs + ([double]$measuredAt - $startMs) * 100 / $pct
            if ($hitMs -lt $resetMs) { $exhaustMs = $hitMs }
        }
    }
    if ($pct -ge 90) { $view.Color = '#FF5A4E'; $view.Warn = '#FF5A4E' }
    elseif ($pct -ge 75 -or $exhaustMs) { $view.Color = '#FFB224'; $view.Warn = '#FFB224' }

    $reset = if ($resetMs) {
        if ($panel) {
            if ($withDay) { "reset $((Get-LocalTime $resetMs).ToString('dd.MM')), $(Format-Moment $resetMs $false)" }
            else { "reset $(Format-Moment $resetMs $false) · za $(Format-Span ($resetMs - $nowMs))" }
        } elseif ($withDay) { "reset $(Format-Moment $resetMs $true)" } else { "reset za $(Format-Span ($resetMs - $nowMs))" }
    } else { '' }
    $view.Note = if ($exhaustMs) {
        $moment = Format-Moment $exhaustMs $withDay
        # Na karcie mieści się jedna krótka linia; pełne zdanie z godziną resetu jest w panelu.
        if (-not $panel) { "skończy się ok. $moment" }
        elseif ($reset) { "w tym tempie skończy się ok. $moment · $reset" }
        else { "w tym tempie skończy się ok. $moment" }
    } else { $reset }
    $view
}

function Get-Detail($session, [double]$nowMs) {
    $elapsed = $nowMs - $session.Since
    switch ($session.Kind) {
        'czeka'   { if ($session.Detail) { $session.Detail } else { 'Potrzebna Twoja decyzja' } }
        'pracuje' { if ($session.Background) { "$($session.Detail) · $(Format-Span $elapsed)" } else { "od $(Format-Span $elapsed)" } }
        'nowe'    { if ($elapsed -lt 60000) { 'przed chwilą' } else { "$(Format-Span $elapsed) temu" } }
        default   { if ($elapsed -lt 60000) { 'przed chwilą' } else { "od $(Format-Span $elapsed)" } }
    }
}

function Get-SessionMeta($session, [double]$nowMs) {
    $detail = Get-Detail $session $nowMs
    $what = switch ($session.Kind) {
        'czeka'   { $detail.Substring(0, 1).ToLower() + $detail.Substring(1) }
        'pracuje' { "pracuje $detail" }
        'nowe'    { "nowy wynik · $detail" }
        default   { "bezczynna $detail" }
    }
    "$($session.Project) · $what"
}

function Get-ContextNote($session) {
    if ($session -and $null -ne $session.ContextTokens -and $session.ContextSize) {
        return "$(Format-Tokens $session.ContextTokens) / $(Format-Tokens $session.ContextSize)"
    }
    ''
}

# --- odświeżanie widoku ------------------------------------------------------

function New-SessionRow($session, [double]$nowMs) {
    $row = New-Object Windows.Controls.Border
    $row.Style = if ($session.Kind -eq 'czeka') { $WaitingRowStyle } else { $RowStyle }
    $row.CornerRadius = 8
    $row.Padding = '10,9,10,9'
    $row.Margin = '0,0,0,2'
    $row.Tag = $session
    $row.ToolTip = if ($session.Kind -eq 'nowe' -and $session.Summary) { "$($session.Summary)`n`nKliknij, aby przejść do terminala tej sesji." }
    elseif ($session.Kind -eq 'czeka') { "$(Get-Detail $session $nowMs)`n`nKliknij, aby przejść do terminala tej sesji." }
    else { 'Kliknij, aby przejść do terminala tej sesji.' }
    $row.Add_MouseLeftButtonUp({
            param($sender, $eventArgs)
            $eventArgs.Handled = $true
            try { Show-SessionTerminal $sender.Tag } catch { Write-WidgetLog "przejście do terminala: $_" }
            Close-Panel
            Update-View
        })

    $grid = New-Object Windows.Controls.Grid
    foreach ($width in [Windows.GridLength]::Auto, [Windows.GridLength]::new(1, 'Star'), [Windows.GridLength]::new(52)) {
        $column = New-Object Windows.Controls.ColumnDefinition
        $column.Width = $width
        [void]$grid.ColumnDefinitions.Add($column)
    }

    $dot = New-Object Windows.Shapes.Ellipse
    $dot.Width = 10
    $dot.Height = 10
    $dot.VerticalAlignment = 'Top'
    $dot.Margin = '0,4,10,0'
    $dot.Fill = Get-Brush $Kinds[$session.Kind].Color
    $dot.Effect = $DotGlow[$session.Kind]

    $texts = New-Object Windows.Controls.StackPanel
    $title = New-Text $session.Name 13 $(if ($session.Kind -eq 'bezczynna') { '#BDBDBD' } else { '#F3F3F3' })
    $title.FontWeight = 'SemiBold'
    $title.TextTrimming = 'CharacterEllipsis'
    $meta = New-Text (Get-SessionMeta $session $nowMs) 12 '#A6A6A6'
    $meta.TextTrimming = 'CharacterEllipsis'
    $meta.Margin = '0,1,0,0'
    [void]$texts.Children.Add($title)
    [void]$texts.Children.Add($meta)
    # Podgląd nowego wyniku: pierwsze zdanie odpowiedzi, żeby ocenić, czy przełączać się od razu.
    if ($session.Kind -eq 'nowe' -and $session.Summary) {
        $preview = New-Text $session.Summary 11.5 '#8A8A8A'
        $preview.TextWrapping = 'Wrap'
        $preview.TextTrimming = 'CharacterEllipsis'
        $preview.MaxHeight = 32
        $preview.Margin = '0,3,0,0'
        [void]$texts.Children.Add($preview)
    }
    [Windows.Controls.Grid]::SetColumn($texts, 1)

    $usage = New-Object Windows.Controls.StackPanel
    $usage.HorizontalAlignment = 'Right'
    $usage.VerticalAlignment = 'Top'
    $usage.Margin = '0,2,0,0'
    [Windows.Controls.Grid]::SetColumn($usage, 2)
    if ($null -ne $session.ContextPct) {
        $pct = [math]::Max(0, [math]::Min(100, [double]$session.ContextPct))
        # Kontekst blisko pełnego zapowiada automatyczne streszczanie rozmowy.
        $value = New-Text ('{0:0}%' -f $pct) 12 $(if ($pct -ge 80) { '#FFB224' } else { '#C8C8C8' })
        $value.HorizontalAlignment = 'Right'
        [Windows.Documents.Typography]::SetNumeralAlignment($value, 'Tabular')
        $track = New-Object Windows.Controls.Border
        $track.Width = 48
        $track.Height = 3
        $track.CornerRadius = 2
        $track.Margin = '0,4,0,0'
        $track.Background = Get-Brush '#14FFFFFF'
        $fill = New-Object Windows.Controls.Border
        $fill.Height = 3
        $fill.Width = 48 * $pct / 100
        $fill.HorizontalAlignment = 'Left'
        $fill.Background = Get-Brush $(if ($pct -ge 80) { '#FFB224' } else { '#BDBDBD' })
        $track.Child = $fill
        [void]$usage.Children.Add($value)
        [void]$usage.Children.Add($track)
    }

    [void]$grid.Children.Add($dot)
    [void]$grid.Children.Add($texts)
    [void]$grid.Children.Add($usage)
    $row.Child = $grid
    $row
}

function Update-Panel($sessions, $fiveView, $weekView, $limits, [double]$nowMs) {
    $PanelCount.Text = "$($sessions.Count)"
    $PanelFreshness.Text = Get-FreshnessText $limits
    # Wiersze buduje się od nowa tylko, gdy zmieni się to, co pokazują — inaczej podświetlenie
    # pod kursorem migałoby co sekundę.
    $signature = ($sessions | ForEach-Object { "$($_.Id)|$($_.Kind)|$(Get-SessionMeta $_ $nowMs)|$($_.Name)|$($_.ContextPct)|$($_.Summary)" }) -join "`n"
    if ($signature -ne $script:PanelSignature) {
        $script:PanelSignature = $signature
        $SessionList.Children.Clear()
        if ($sessions.Count -eq 0) { [void]$SessionList.Children.Add((New-Text 'Brak otwartych sesji' 12 '#A6A6A6')) }
        foreach ($session in $sessions) { [void]$SessionList.Children.Add((New-SessionRow $session $nowMs)) }
    }
    Set-Meter $P5h $fiveView.Pct $fiveView.Note $fiveView.Color
    Set-Meter $PWeek $weekView.Pct $weekView.Note $weekView.Color
}

function Set-Pulse([bool]$on) {
    if ($on -eq $script:Pulsing) { return }
    $script:Pulsing = $on
    foreach ($light in $LightCzeka, $MiniCzeka) {
        if ($on) {
            $pulse = New-Object Windows.Media.Animation.DoubleAnimation -ArgumentList 1.0, 0.45, ([Windows.Duration]::new([TimeSpan]::FromMilliseconds(900)))
            $pulse.AutoReverse = $true
            $pulse.RepeatBehavior = [Windows.Media.Animation.RepeatBehavior]::Forever
            $light.BeginAnimation([Windows.UIElement]::OpacityProperty, $pulse)
        } else {
            $light.BeginAnimation([Windows.UIElement]::OpacityProperty, $null)
            $light.Opacity = 1
        }
    }
}

function Update-View {
    $nowMs = [double][DateTimeOffset]::Now.ToUnixTimeMilliseconds()
    $sessions = @(Get-Sessions $nowMs)
    $script:Sessions = $sessions
    $limits = Read-SharedJson (Join-Path $StateDir 'limits.json')
    $measuredAt = if ($limits) { $limits.updatedAt } else { $null }
    $fiveView = Get-LimitView $(if ($limits) { $limits.fiveHour }) 'fiveHour' $measuredAt $nowMs $false
    $weekView = Get-LimitView $(if ($limits) { $limits.sevenDay }) 'sevenDay' $measuredAt $nowMs $false
    $focus = if ($sessions.Count) { $sessions[0] } else { $null }

    $counts = @{ czeka = 0; pracuje = 0; gotowe = 0 }
    foreach ($session in $sessions) {
        $light = $Kinds[$session.Kind].Light
        if ($light) { $counts[$light]++ }
    }
    foreach ($key in $Lights.Keys) {
        foreach ($set in $LightSets) {
            $light = $set.Ellipses[$key]
            $count = $set.Counts[$key]
            if ($counts[$key] -gt 0) {
                $light.Fill = $LitFill[$key]
                $light.Effect = $set.Glow[$key]
                $count.Text = "$($counts[$key])"
                $count.Visibility = 'Visible'
            } else {
                $light.Fill = Get-Brush $Lights[$key].Off
                $light.Effect = $null
                $count.Visibility = 'Collapsed'
            }
        }
    }
    Set-Pulse ($counts.czeka -gt 0)

    if ($focus) {
        $StatusLabel.Text = $Kinds[$focus.Kind].Label
        $StatusLabel.Foreground = Get-Brush $(if ($focus.Kind -eq 'bezczynna') { '#8A8A8A' } else { $Kinds[$focus.Kind].Color })
        $StatusDetail.Text = if ($focus.Kind -eq 'bezczynna') { 'wyniki przejrzane' } else { Get-Detail $focus $nowMs }
        $SessionLine.Text = "$($focus.Project) · $(Format-Sessions $sessions.Count)"
    } else {
        $StatusLabel.Text = 'Brak sesji'
        $StatusLabel.Foreground = Get-Brush '#8A8A8A'
        $StatusDetail.Text = 'uruchom Claude Code'
        $SessionLine.Text = ''
    }

    Set-Meter $M5h $fiveView.Pct $fiveView.Note $fiveView.Color
    Set-Meter $MWeek $weekView.Pct $weekView.Note $weekView.Color
    $contextPct = if ($focus) { $focus.ContextPct } else { $null }
    Set-Meter $MContext $contextPct (Get-ContextNote $focus) $(if ($contextPct -ge 80) { '#FFB224' } else { '#BDBDBD' })
    $Freshness.Text = Get-FreshnessText $limits

    # Obramowanie ostrzega przed końcem limitu także w widoku mini, bez najeżdżania.
    $warn = if ($fiveView.Warn -eq '#FF5A4E' -or $weekView.Warn -eq '#FF5A4E') { '#FF5A4E' } elseif ($fiveView.Warn -or $weekView.Warn) { '#FFB224' } else { $null }
    $Card.BorderBrush = Get-Brush $(if ($warn) { '#B3' + $warn.Substring(1) } else { '#14FFFFFF' })
    $Mini.BorderBrush = $Card.BorderBrush
    $Mini.BorderThickness = if ($warn) { 1.5 } else { 1 }

    Update-Tray $counts

    if ($Panel.IsOpen) {
        $panelFive = Get-LimitView $(if ($limits) { $limits.fiveHour }) 'fiveHour' $measuredAt $nowMs $true
        $panelWeek = Get-LimitView $(if ($limits) { $limits.sevenDay }) 'sevenDay' $measuredAt $nowMs $true
        Update-Panel $sessions $panelFive $panelWeek $limits $nowMs
    }
}

# Wynik uznaje się za przejrzany, gdy terminal tej sesji jest na pierwszym planie przez kilka sekund.
# Jeden terminal może mieć kilka sesji w kartach — wtedy decyduje tytuł okna (nazwa sesji).
function Update-SeenByFocus([double]$nowMs) {
    $fresh = @($script:Sessions | Where-Object { $_.Kind -eq 'nowe' -and $_.Pid })
    if (-not $fresh.Count) { $script:Dwell = @{}; return }
    $foreground = [WidgetNative]::GetForegroundWindow()
    $foregroundPid = [int][WidgetNative]::ProcessOf($foreground)
    $title = [WidgetNative]::TitleOf($foreground)
    foreach ($session in $fresh) {
        $hostPid = Resolve-HostPid $session.Pid
        $sharing = @($script:Sessions | Where-Object { $_.Pid -and (Resolve-HostPid $_.Pid) -eq $hostPid }).Count
        $looking = $hostPid -and $hostPid -eq $foregroundPid -and ($sharing -eq 1 -or ($session.Name -and $title.Contains($session.Name)))
        if (-not $looking) { $script:Dwell.Remove($session.Id); continue }
        if (-not $script:Dwell.ContainsKey($session.Id)) { $script:Dwell[$session.Id] = $nowMs }
        elseif ($nowMs - $script:Dwell[$session.Id] -ge $SeenAfterMs) {
            Set-Seen $session.Id
            $script:Dwell.Remove($session.Id)
        }
    }
}

# --- zasobnik systemowy ------------------------------------------------------

function New-TrayIconHandle([string]$hex) {
    $bitmap = New-Object Drawing.Bitmap 32, 32
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $fill = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($hex))
    $outline = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(200, 24, 24, 24)), 2
    $graphics.FillEllipse($fill, 4, 4, 24, 24)
    $graphics.DrawEllipse($outline, 4, 4, 24, 24)
    $fill.Dispose(); $outline.Dispose(); $graphics.Dispose()
    $handle = $bitmap.GetHicon()
    $bitmap.Dispose()
    $handle
}

# W zasobniku jedno światło wystarcza: kolor najpilniejszego stanu, szczegóły w podpowiedzi.
function Update-Tray($counts) {
    $color = if ($counts.czeka) { '#FF5A4E' } elseif ($counts.gotowe) { '#3DD68C' } elseif ($counts.pracuje) { '#FFB224' } else { '#6E6E6E' }
    if ($color -ne $script:TrayKey) {
        $handle = New-TrayIconHandle $color
        $previous = $script:TrayHandle
        $Tray.Icon = [Drawing.Icon]::FromHandle($handle)
        if ($previous -ne [IntPtr]::Zero) { [void][WidgetNative]::DestroyIcon($previous) }
        $script:TrayHandle = $handle
        $script:TrayKey = $color
    }
    $parts = @()
    if ($counts.czeka) { $parts += "czeka $($counts.czeka)" }
    if ($counts.pracuje) { $parts += "pracuje $($counts.pracuje)" }
    if ($counts.gotowe) { $parts += "nowe wyniki $($counts.gotowe)" }
    $text = if ($parts.Count) { 'Claude Code: ' + ($parts -join ' · ') } else { 'Claude Code: nic nie czeka' }
    if ($text.Length -gt 63) { $text = $text.Substring(0, 63) }
    if ($Tray.Text -ne $text) { $Tray.Text = $text }
}

# --- rozmiar, położenie, widoczność ------------------------------------------

function Get-ActiveCard { if ($script:Size -eq 'mini') { $Mini } else { $Card } }

# Przełącza rozmiar; przy przełączaniu z menu prawa krawędź zostaje w miejscu,
# więc widżet przyklejony do brzegu ekranu nie odjeżdża od niego.
function Set-Size([string]$size, [bool]$keepRightEdge) {
    $right = $window.Left + $window.ActualWidth
    $script:Size = $size
    $Card.Visibility = if ($size -eq 'mini') { 'Collapsed' } else { 'Visible' }
    $Mini.Visibility = if ($size -eq 'mini') { 'Visible' } else { 'Collapsed' }
    $Panel.PlacementTarget = Get-ActiveCard
    $label = if ($size -eq 'mini') { 'Widok pełny' } else { 'Widok mini' }
    $SizeItem.Header = $label
    $TraySize.Text = $label
    $window.UpdateLayout()
    if ($keepRightEdge) { $window.Left = $right - $window.ActualWidth }
}

function Switch-Size {
    Close-Panel
    Set-Size $(if ($script:Size -eq 'mini') { 'full' } else { 'mini' }) $true
    Save-Settings
}

function Set-DefaultPosition {
    # Karta 8 px od prawej krawędzi obszaru roboczego; okno jest szersze o margines na cień.
    $area = [Windows.SystemParameters]::WorkArea
    $window.Left = $area.Right - $window.ActualWidth + $ShadowMargin - 8
    $window.Top = $area.Top + 120
}

# Po przeciągnięciu karta blisko krawędzi obszaru roboczego przykleja się do niej.
function Invoke-Snap {
    $screen = [Windows.Forms.Screen]::FromHandle($script:Hwnd)
    $scale = [Windows.PresentationSource]::FromVisual($window).CompositionTarget.TransformToDevice.M11
    $area = $screen.WorkingArea
    $left = $area.Left / $scale; $top = $area.Top / $scale; $right = $area.Right / $scale; $bottom = $area.Bottom / $scale
    $cardLeft = $window.Left + $ShadowMargin
    $cardRight = $window.Left + $window.ActualWidth - $ShadowMargin
    $cardTop = $window.Top + $ShadowMargin
    $cardBottom = $window.Top + $window.ActualHeight - $ShadowMargin
    if ([math]::Abs($right - $cardRight) -lt $SnapDistance) { $window.Left = $right - 8 - $window.ActualWidth + $ShadowMargin }
    elseif ([math]::Abs($cardLeft - $left) -lt $SnapDistance) { $window.Left = $left + 8 - $ShadowMargin }
    if ([math]::Abs($bottom - $cardBottom) -lt $SnapDistance) { $window.Top = $bottom - 8 - $window.ActualHeight + $ShadowMargin }
    elseif ([math]::Abs($cardTop - $top) -lt $SnapDistance) { $window.Top = $top + 8 - $ShadowMargin }
}

function Save-Settings {
    try {
        @{ left = $window.Left; top = $window.Top; size = $script:Size } | ConvertTo-Json | Set-Content -Path $ConfigPath -Encoding UTF8
    } catch {
        Write-WidgetLog "zapis ustawień: $_"
    }
}

function Restore-Settings {
    $config = $null
    if (Test-Path -LiteralPath $ConfigPath) {
        try { $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
    }
    $size = if ($config -and $Sizes -contains "$($config.size)") { "$($config.size)" } else { 'mini' }
    Set-Size $size $false

    # Zapisana pozycja może wskazywać na odłączony monitor — wtedy wraca domyślna.
    $left = [Windows.SystemParameters]::VirtualScreenLeft
    $top = [Windows.SystemParameters]::VirtualScreenTop
    $right = $left + [Windows.SystemParameters]::VirtualScreenWidth
    $bottom = $top + [Windows.SystemParameters]::VirtualScreenHeight
    if ($config -and $null -ne $config.left -and $config.left -ge ($left - $ShadowMargin) -and
        ($config.left + $window.ActualWidth - $ShadowMargin) -le $right -and
        $config.top -ge ($top - $ShadowMargin) -and ($config.top + 80) -le $bottom) {
        $window.Left = [double]$config.left
        $window.Top = [double]$config.top
    } else {
        Set-DefaultPosition
    }
}

# Widżet znika, gdy ukryjesz go z menu albo gdy na jego monitorze działa coś na pełnym ekranie
# (prezentacja, film, udostępnianie ekranu). Zasobnik zostaje zawsze.
function Update-Visibility {
    $hidden = $script:UserHidden -or $script:FullscreenHidden
    if ($hidden -and $window.IsVisible) { Close-Panel; $window.Hide() }
    elseif (-not $hidden -and -not $window.IsVisible) { $window.Show() }
    $TrayShow.Text = if ($script:UserHidden) { 'Pokaż widżet' } else { 'Ukryj widżet' }
}

function Update-Fullscreen {
    $foreground = [WidgetNative]::GetForegroundWindow()
    $fullscreen = $false
    if ($foreground -ne [IntPtr]::Zero -and $foreground -ne $script:Hwnd -and
        [WidgetNative]::ClassOf($foreground) -notin 'Progman', 'WorkerW', 'Shell_TrayWnd', 'Shell_SecondaryTrayWnd') {
        $fullscreen = [WidgetNative]::IsFullscreen($foreground) -and
            ([WidgetNative]::MonitorOf($foreground) -eq [WidgetNative]::MonitorOf($script:Hwnd))
    }
    if ($fullscreen -ne $script:FullscreenHidden) {
        $script:FullscreenHidden = $fullscreen
        Update-Visibility
    }
}

function Set-Autostart([bool]$enabled) {
    try {
        if ($enabled) {
            $shell = New-Object -ComObject WScript.Shell
            $link = $shell.CreateShortcut($StartupLink)
            $link.TargetPath = "$env:WINDIR\System32\wscript.exe"
            $link.Arguments = '"' + (Join-Path $PSScriptRoot 'start-widget.vbs') + '"'
            $link.WorkingDirectory = $PSScriptRoot
            $link.Description = 'Widżet Claude Code: sygnalizator sesji i limity'
            $link.Save()
        } else {
            Remove-Item -LiteralPath $StartupLink -ErrorAction SilentlyContinue
        }
    } catch {
        Write-WidgetLog "autostart: $_"
    }
    $TrayAutostart.Checked = Test-Path -LiteralPath $StartupLink
}

# --- panel -------------------------------------------------------------------

function Open-Panel {
    if ($Panel.IsOpen) { return }
    $script:PanelSignature = ''
    $Panel.IsOpen = $true
    try { Update-View } catch { Write-WidgetLog "panel: $_" }
    $HoverTimer.Start()
}

function Close-Panel {
    $script:Pinned = $false
    $PanelBody.BorderBrush = Get-Brush '#14FFFFFF'
    $Panel.IsOpen = $false
    $HoverTimer.Stop()
}

# Panel otwiera się dopiero po chwili nad sygnalizatorem, żeby przejechanie myszą do paska
# przewijania albo przycisków okna przy krawędzi ekranu go nie rozwijało.
$OpenTimer = New-Object Windows.Threading.DispatcherTimer
$OpenTimer.Interval = [TimeSpan]::FromMilliseconds(400)
$OpenTimer.Add_Tick({
        $OpenTimer.Stop()
        if ((Get-ActiveCard).IsMouseOver) { Open-Panel }
    })

$HoverTimer = New-Object Windows.Threading.DispatcherTimer
$HoverTimer.Interval = [TimeSpan]::FromMilliseconds(300)
$HoverTimer.Add_Tick({
        if (-not $script:Pinned -and -not (Get-ActiveCard).IsMouseOver -and -not $PanelBody.IsMouseOver) { Close-Panel }
    })

# --- menu --------------------------------------------------------------------

$menu = New-Object Windows.Controls.ContextMenu
$SizeItem = New-Object Windows.Controls.MenuItem
$SizeItem.Add_Click({ Switch-Size })
$dockItem = New-Object Windows.Controls.MenuItem
$dockItem.Header = 'Przyklej do prawej krawędzi'
$dockItem.Add_Click({ Set-DefaultPosition; Save-Settings })
$hideItem = New-Object Windows.Controls.MenuItem
$hideItem.Header = 'Ukryj (przywrócisz z zasobnika)'
$hideItem.Add_Click({ $script:UserHidden = $true; Update-Visibility })
$closeItem = New-Object Windows.Controls.MenuItem
$closeItem.Header = 'Zamknij widżet'
$closeItem.Add_Click({ $window.Close() })
foreach ($item in $SizeItem, $dockItem, $hideItem, $closeItem) { [void]$menu.Items.Add($item) }

$Tray = New-Object Windows.Forms.NotifyIcon
$trayMenu = New-Object Windows.Forms.ContextMenuStrip
$TrayShow = $trayMenu.Items.Add('Ukryj widżet')
$TrayShow.Add_Click({ $script:UserHidden = -not $script:UserHidden; Update-Visibility })
$TraySize = $trayMenu.Items.Add('Widok pełny')
$TraySize.Add_Click({ if ($script:UserHidden) { $script:UserHidden = $false; Update-Visibility }; Switch-Size })
$trayDock = $trayMenu.Items.Add('Przyklej do prawej krawędzi')
$trayDock.Add_Click({ if ($script:UserHidden) { $script:UserHidden = $false; Update-Visibility }; Set-DefaultPosition; Save-Settings })
$TrayAutostart = New-Object Windows.Forms.ToolStripMenuItem 'Uruchamiaj przy logowaniu'
$TrayAutostart.Checked = Test-Path -LiteralPath $StartupLink
$TrayAutostart.Add_Click({ Set-Autostart (-not $TrayAutostart.Checked) })
[void]$trayMenu.Items.Add($TrayAutostart)
[void]$trayMenu.Items.Add((New-Object Windows.Forms.ToolStripSeparator))
$trayClose = $trayMenu.Items.Add('Zamknij widżet')
$trayClose.Add_Click({ $window.Close() })
$Tray.ContextMenuStrip = $trayMenu
$Tray.Add_MouseClick({
        param($sender, $eventArgs)
        if ($eventArgs.Button -eq [Windows.Forms.MouseButtons]::Left) {
            $script:UserHidden = -not $script:UserHidden
            Update-Visibility
        }
    })

# --- zdarzenia ---------------------------------------------------------------

foreach ($surface in $Card, $Mini) {
    $surface.ContextMenu = $menu
    $surface.Add_MouseEnter({ $OpenTimer.Stop(); $OpenTimer.Start() })
    $surface.Add_MouseLeave({ $OpenTimer.Stop() })
    # Przeciągnięcie przesuwa widżet; kliknięcie w miejscu przypina albo zamyka panel.
    $surface.Add_MouseLeftButtonDown({
            $OpenTimer.Stop()
            $startLeft = $window.Left
            $startTop = $window.Top
            $window.DragMove()
            if ([math]::Abs($window.Left - $startLeft) -ge 1 -or [math]::Abs($window.Top - $startTop) -ge 1) {
                Close-Panel
                Invoke-Snap
                Save-Settings
            } elseif ($script:Pinned) {
                Close-Panel
            } else {
                Open-Panel
                $script:Pinned = $true
                $PanelBody.BorderBrush = Get-Brush '#40FFFFFF'
            }
        })
}

function Invoke-Safely([string]$what, [scriptblock]$action) {
    try {
        & $action
    } catch {
        # Ten sam błąd co sekundę zalałby log; zapisuje się tylko zmiana.
        $message = "${what}: $_"
        if ($message -ne $script:LastError) {
            $script:LastError = $message
            Write-WidgetLog $message
        }
    }
}
$script:LastError = ''

# Co sekundę: czasy („od 4 min”), procesy sesji, pełny ekran i przejrzane wyniki.
$RefreshTimer = New-Object Windows.Threading.DispatcherTimer
$RefreshTimer.Interval = [TimeSpan]::FromSeconds(1)
$RefreshTimer.Add_Tick({
        Invoke-Safely 'odświeżanie' { Update-View }
        Invoke-Safely 'pełny ekran' { Update-Fullscreen }
        Invoke-Safely 'przejrzenie' { Update-SeenByFocus ([double][DateTimeOffset]::Now.ToUnixTimeMilliseconds()) }
    })

# Co 0,2 s: czy hook albo statusline coś zapisały. Każdy zapis podmienia plik przez zmianę
# nazwy, więc wystarczy znacznik czasu katalogu — zmianę stanu widać od razu, nie po sekundzie.
$WatchTimer = New-Object Windows.Threading.DispatcherTimer
$WatchTimer.Interval = [TimeSpan]::FromMilliseconds(200)
$WatchTimer.Add_Tick({
        Invoke-Safely 'obserwacja' {
            if (-not (Test-Path -LiteralPath $StateDir)) { return }
            $stamp = [IO.Directory]::GetLastWriteTimeUtc($StateDir)
            if ($stamp -ne $script:StateStamp) {
                $script:StateStamp = $stamp
                Update-View
            }
        }
    })

$window.Add_Loaded({
        $script:Hwnd = (New-Object Windows.Interop.WindowInteropHelper $window).Handle
        Restore-Settings
        $hint = 'Kliknij sesję, aby przejść do jej terminala. Kliknij sygnalizator, aby przypiąć panel.'
        try {
            $script:Hotkey = New-Object WidgetHotkey -ArgumentList ([uint32]$HotkeyModifiers), ([uint32]$HotkeyKey)
            if ($script:Hotkey.Registered) {
                $script:Hotkey.Add_Pressed({ Invoke-Safely 'skrót' { Invoke-Jump } })
                $hint = "Kliknij sesję, aby przejść do jej terminala; $HotkeyLabel przenosi do tej, która czeka najdłużej. Kliknij sygnalizator, aby przypiąć panel."
            } else {
                Write-WidgetLog "skrót $HotkeyLabel jest zajęty przez inny program"
            }
        } catch {
            Write-WidgetLog "skrót: $_"
        }
        $PanelHint.Text = $hint
        Invoke-Safely 'start' { Update-View }
        $RefreshTimer.Start()
        $WatchTimer.Start()
    })

$window.Add_Closed({
        $RefreshTimer.Stop()
        $WatchTimer.Stop()
        $Tray.Visible = $false
        [Windows.Threading.Dispatcher]::CurrentDispatcher.InvokeShutdown()
    })

# Własna pętla zamiast ShowDialog: tylko tak okno da się ukryć i pokazać ponownie.
try {
    $Tray.Visible = $true
    $window.Show()
    [Windows.Threading.Dispatcher]::Run()
} catch {
    Write-WidgetLog "okno: $_"
} finally {
    if ($script:Hotkey) { $script:Hotkey.Dispose() }
    $Tray.Visible = $false
    $Tray.Dispose()
    if ($script:TrayHandle -ne [IntPtr]::Zero) { [void][WidgetNative]::DestroyIcon($script:TrayHandle) }
    $mutex.ReleaseMutex()
}
