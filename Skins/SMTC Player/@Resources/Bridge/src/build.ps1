# Сборка smtc_bridge.exe из smtc_bridge.cs
# Нужен только компилятор из .NET Framework 4 (есть в любой Windows) и
# Windows.winmd из Windows SDK. Запускать: powershell -File build.ps1
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'smtc_bridge.cs'
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'smtc_bridge.exe'

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe не найден: $csc" }

# Windows.winmd — метаданные WinRT
$winmd = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata' -Recurse -Filter 'Windows.winmd' -ErrorAction SilentlyContinue |
         Where-Object { $_.DirectoryName -notmatch 'Facade' } |
         Sort-Object FullName -Descending | Select-Object -First 1
if (-not $winmd) { throw 'Windows.winmd не найден — нужен Windows SDK (UnionMetadata)' }

# Фасады WinRT-проекции. Targeting pack (Reference Assemblies) в системе может
# отсутствовать, поэтому берём сборки прямо из каталога рантайма — для csc этого
# достаточно.
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

$refs = @(
    '/r:System.dll'
    '/r:System.Core.dll'
    ('/r:"{0}"' -f $winmd.FullName)
    ('/r:"{0}"' -f (Join-Path $fw 'System.Runtime.WindowsRuntime.dll'))
    ('/r:"{0}"' -f (Join-Path $fw 'System.Runtime.dll'))
    ('/r:"{0}"' -f (Join-Path $fw 'System.Runtime.InteropServices.WindowsRuntime.dll'))
)

$args = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', ('/out:"{0}"' -f $out)) + $refs + ('"{0}"' -f $src)

Write-Host "csc $($args -join ' ')"
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
Write-Host "OK: $out"
