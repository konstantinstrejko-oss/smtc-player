# Сборка smtc_bridge.exe — мост для Rainmeter и Shard Player в трее.
# Нужен только компилятор из .NET Framework 4 (есть в любой Windows) и
# Windows.winmd из Windows SDK. Запускать: powershell -File build.ps1
#
# Шейдер стекла лежит в репозитории уже скомпилированным (Glass\glass.ps):
# fxc.exe нужен только тому, кто правит сам glass.fx.
$ErrorActionPreference = 'Stop'

$srcDir    = $PSScriptRoot
$bridgeDir = Split-Path $srcDir -Parent
$resDir    = Split-Path $bridgeDir -Parent
$out       = Join-Path $bridgeDir 'smtc_bridge.exe'

$sources = @(
    'smtc_bridge.cs'
    'player_state.cs'
    'player_audio.cs'
    'player_graphics.cs'
    'player_tray.cs'
    'player_window.cs'
    'player_app.cs'
) | ForEach-Object { Join-Path $srcDir $_ }

foreach ($s in $sources) { if (-not (Test-Path $s)) { throw "нет исходника: $s" } }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe не найден: $csc" }

# Windows.winmd — метаданные WinRT
$winmd = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata' -Recurse -Filter 'Windows.winmd' -ErrorAction SilentlyContinue |
         Where-Object { $_.DirectoryName -notmatch 'Facade' } |
         Sort-Object FullName -Descending | Select-Object -First 1
if (-not $winmd) { throw 'Windows.winmd не найден — нужен Windows SDK (UnionMetadata)' }

# Фасады WinRT-проекции. Targeting pack (Reference Assemblies) в системе может
# отсутствовать, поэтому берём сборки прямо из каталога рантайма — для csc этого
# достаточно. Сборки WPF лежат в подкаталоге WPF, а не рядом с остальными.
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

$refs = @(
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Xaml.dll'
    ('/r:"{0}"' -f $winmd.FullName)
    ('/r:"{0}"' -f (Join-Path $fw 'System.Runtime.WindowsRuntime.dll'))
    ('/r:"{0}"' -f (Join-Path $fw 'System.Runtime.dll'))
    ('/r:"{0}"' -f (Join-Path $fw 'System.Runtime.InteropServices.WindowsRuntime.dll'))
    ('/r:"{0}"' -f (Join-Path $fw 'WPF\PresentationFramework.dll'))
    ('/r:"{0}"' -f (Join-Path $fw 'WPF\PresentationCore.dll'))
    ('/r:"{0}"' -f (Join-Path $fw 'WPF\WindowsBase.dll'))
)

# Шрифты, шейдер и шум вшиты в exe: так один файл работает и внутри пакета
# Rainmeter, и отдельно из zip, где папки @Resources рядом нет.
$assets = @(
    @{ Path = (Join-Path $resDir 'Fonts\Aquatico.otf');  Name = 'Fonts.Aquatico.otf' }
    @{ Path = (Join-Path $resDir 'Fonts\Quicksand.otf'); Name = 'Fonts.Quicksand.otf' }
    @{ Path = (Join-Path $resDir 'Glass\glass.ps');      Name = 'Glass.glass.ps' }
    @{ Path = (Join-Path $resDir 'Glass\noise.png');     Name = 'Glass.noise.png' }
)

$resArgs = @()
foreach ($a in $assets) {
    if (-not (Test-Path $a.Path)) { throw ("нет ассета: {0}" -f $a.Path) }
    $resArgs += ('/resource:"{0}",{1}' -f $a.Path, $a.Name)
}

$icon = Join-Path $resDir 'Glass\app.ico'
$iconArg = @()
if (Test-Path $icon) { $iconArg = @(('/win32icon:"{0}"' -f $icon)) }

$args = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', ('/out:"{0}"' -f $out)) +
        $refs + $resArgs + $iconArg + ($sources | ForEach-Object { '"{0}"' -f $_ })

Write-Host "csc -> $out"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $csc
$psi.Arguments = ($args -join ' ')
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$p = [System.Diagnostics.Process]::Start($psi)
$stdout = $p.StandardOutput.ReadToEnd()
$stderr = $p.StandardError.ReadToEnd()
$p.WaitForExit()

if ($stdout) { Write-Host $stdout }
if ($stderr) { Write-Host $stderr }
if ($p.ExitCode -ne 0) { throw "сборка провалилась, код $($p.ExitCode)" }
Write-Host ("OK: {0} ({1:N0} байт)" -f $out, (Get-Item $out).Length)
