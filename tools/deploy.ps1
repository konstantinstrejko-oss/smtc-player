# Копирует скин из репозитория в папку Rainmeter и перезагружает его.
# Нужно при разработке: правишь в репозитории — проверяешь вживую.
param([string]$SkinPath)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$src  = Join-Path $repo 'Skins\SMTC Player'

if (-not $SkinPath) {
    # путь к папке скинов Rainmeter берём из его же настроек
    $ini = "$env:APPDATA\Rainmeter\Rainmeter.ini"
    if (Test-Path $ini) {
        $m = Select-String -Path $ini -Pattern '^SkinPath=(.+)$' | Select-Object -First 1
        if ($m) { $SkinPath = $m.Matches[0].Groups[1].Value.Trim() }
    }
    if (-not $SkinPath) { $SkinPath = "$env:USERPROFILE\Documents\Rainmeter\Skins" }
}

$dst = Join-Path $SkinPath 'SMTC Player'
Write-Host "деплой -> $dst"

Get-Process smtc_bridge -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Force $dst | Out-Null }
# cmd.ini не трогаем: это рабочий канал команд, его создаёт мост
robocopy $src $dst /MIR /XF cmd.inc bridge.log /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy вернул $LASTEXITCODE" }

$rainmeter = Join-Path ${env:ProgramFiles} 'Rainmeter\Rainmeter.exe'
if (Test-Path $rainmeter) {
    & $rainmeter !Refresh 'SMTC Player\Player'
    Write-Host 'скин перезагружен'
} else {
    Write-Warning "Rainmeter.exe не найден — обнови скин вручную"
}
