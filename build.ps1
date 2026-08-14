# Сборка установочного пакета SMTC Player (.rmskin)
# ---------------------------------------------------------------------------
# .rmskin — это обычный ZIP, к которому дописан 16-байтовый хвост:
#   8 байт  uint64 little-endian — размер ZIP-части
#   1 байт  версия формата (0)
#   7 байт  "RMSKIN\0"
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -SkipBridge   без пересборки exe
param([switch]$SkipBridge)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root    = $PSScriptRoot
$skinSrc = Join-Path $root 'Skins\SMTC Player'
$dist    = Join-Path $root 'dist'
$staging = Join-Path $root 'obj\staging'

# версия берётся из RMSKIN.ini, чтобы не расходилась с манифестом
$version = (Select-String -Path (Join-Path $root 'RMSKIN.ini') -Pattern '^Version=(.+)$').Matches[0].Groups[1].Value.Trim()
if (-not $version) { $version = '1.0.0' }

if (-not $SkipBridge) {
    Write-Host '== сборка моста =='
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $skinSrc '@Resources\Bridge\src\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'мост не собрался' }
}

Write-Host '== подготовка содержимого =='
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $staging 'Skins') | Out-Null

Copy-Item (Join-Path $root 'RMSKIN.ini') $staging -Force
Copy-Item (Join-Path $root 'RMSKIN.bmp') $staging -Force
Copy-Item $skinSrc (Join-Path $staging 'Skins\SMTC Player') -Recurse -Force

# рабочие файлы конкретной машины в пакет не идут
foreach ($junk in @('Player\cmd.inc', '@Resources\Bridge\bridge.log', 'obj')) {
    $p = Join-Path $staging "Skins\SMTC Player\$junk"
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}

Write-Host '== упаковка =='
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Force $dist | Out-Null }
# имя без версии: ссылка /releases/latest/download/SMTC-Player.rmskin должна
# оставаться рабочей от релиза к релизу
$outFile = Join-Path $dist "SMTC-Player.rmskin"
if (Test-Path $outFile) { Remove-Item $outFile -Force }

$zipPath = Join-Path $root "obj\package.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# пишем записи вручную: нужны разделители "/" внутри архива
$zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
$archive   = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
$prefix    = (Resolve-Path $staging).Path.TrimEnd('\') + '\'
foreach ($file in Get-ChildItem $staging -Recurse -File) {
    $rel = $file.FullName.Substring($prefix.Length).Replace('\', '/')
    $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
    $es = $entry.Open()
    $fs = [System.IO.File]::OpenRead($file.FullName)
    $fs.CopyTo($es)
    $fs.Dispose(); $es.Dispose()
    Write-Host "   + $rel"
}
$archive.Dispose(); $zipStream.Dispose()

# ZIP + хвост Rainmeter
$zipBytes = [System.IO.File]::ReadAllBytes($zipPath)
$footer   = New-Object byte[] 16
[Array]::Copy([BitConverter]::GetBytes([uint64]$zipBytes.Length), 0, $footer, 0, 8)
$footer[8] = 0
[Array]::Copy([System.Text.Encoding]::ASCII.GetBytes("RMSKIN`0"), 0, $footer, 9, 7)

$out = [System.IO.File]::Open($outFile, [System.IO.FileMode]::Create)
$out.Write($zipBytes, 0, $zipBytes.Length)
$out.Write($footer, 0, $footer.Length)
$out.Dispose()

Remove-Item $zipPath -Force
Remove-Item (Join-Path $root 'obj') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ("Готово: {0} ({1:n0} КБ)" -f $outFile, ((Get-Item $outFile).Length / 1KB))
