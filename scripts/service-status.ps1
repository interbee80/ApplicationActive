<#
.SYNOPSIS
    Состояние службы ApplicationActive, агентов сеансов и Web-панели.

.DESCRIPTION
    Показывает состояние службы, адрес панели из appsettings.json, отвечает ли REST API,
    какие агенты сеансов на связи и последние строки журнала службы.

.PARAMETER Tail
    Сколько последних строк журнала показать (по умолчанию 15).

.EXAMPLE
    .\service-status.ps1
    .\service-status.ps1 -Tail 40
#>
[CmdletBinding()]
param(
    [int]$Tail = 15,
    [string]$InstallDir = ''
)

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $root 'src\AppActive.Service\appsettings.json'
if (-not $InstallDir) { $InstallDir = Join-Path ${env:ProgramFiles} 'AppActive' }

if (-not (Test-Path $settingsPath)) { throw "Не найден $settingsPath" }
$config = (Get-Content $settingsPath -Raw | ConvertFrom-Json).AppActive
$serviceName = $config.ServiceName
$dataDirectory = [Environment]::ExpandEnvironmentVariables($config.DataDirectory)
$url = if ($config.Urls -and $config.Urls.Count -gt 0) { $config.Urls[0] } else { '' }

Write-Host "ApplicationActive — состояние" -ForegroundColor Cyan

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Host ("  Служба:        {0} ({1}), запуск: {2}" -f $service.Status, $serviceName, $service.StartType)
} else {
    Write-Host "  Служба:        не установлена ($serviceName)" -ForegroundColor Yellow
}

Write-Host "  Адрес панели:  $url (из appsettings.json)"
Write-Host "  Каталог данных: $dataDirectory"

# Адрес для проверки API: 0.0.0.0 и + заменяем на loopback.
$probeUrl = $url
if ($probeUrl) {
    $uri = [Uri]$probeUrl
    $hostPart = if ($uri.Host -in @('0.0.0.0', '*', '+', '::')) { '127.0.0.1' } else { $uri.Host }
    $probeUrl = "$($uri.Scheme)://${hostPart}:$($uri.Port)"
}

if ($probeUrl) {
    try {
        $health = Invoke-RestMethod -Uri "$probeUrl/api/health" -TimeoutSec 6
        Write-Host "  REST API:      отвечает (версия $($health.version), база $([Math]::Round($health.databaseSizeBytes/1KB,1)) КБ)" -ForegroundColor Green
        Write-Host "  Учёт:          $(if ($health.monitorEnabled) { 'включён' } else { 'выключен' }), интервал опроса $($health.pollIntervalSeconds) с, N = $($health.activityIdleSeconds) с"
        if ($health.agents.Count -eq 0) {
            Write-Host "  Агенты сеансов: нет" -ForegroundColor Yellow
        } else {
            foreach ($agent in $health.agents) {
                $state = if ($agent.alive) { 'на связи' } else { 'НЕ ОТВЕЧАЕТ' }
                Write-Host ("  Агент:         сеанс {0}, {1}\{2}, PID {3}, {4}, отчётов {5}, ЦП {6}%, память {7} МБ" -f `
                    $agent.sessionId, $agent.domain, $agent.userName, $agent.pid, $state, $agent.reportsTotal, $agent.cpuPercent, $agent.workingSetMb)
            }
        }
    } catch {
        Write-Host "  REST API:      не отвечает ($($_.Exception.Message))" -ForegroundColor Red
    }
}

$logsDirectory = Join-Path $dataDirectory 'logs'
# Каталог данных закрыт правами SYSTEM и администраторов, поэтому без прав администратора
# он может быть недоступен — это не ошибка, просто показываем пояснение.
$logsAvailable = $false
try { $logsAvailable = Test-Path -LiteralPath $logsDirectory -ErrorAction Stop } catch { $logsAvailable = $false }

if ($logsAvailable) {
    $latest = Get-ChildItem $logsDirectory -Filter 'appactive-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        Write-Host "  Журнал:        $($latest.FullName)"
        Get-Content $latest.FullName -Tail $Tail | ForEach-Object { "    $_" }
    } else {
        Write-Host "  Журнал:        файлов в $logsDirectory пока нет"
    }
} elseif (Test-Path -LiteralPath $dataDirectory -ErrorAction SilentlyContinue) {
    Write-Host "  Журнал:        нет доступа к $logsDirectory (нужны права администратора)" -ForegroundColor Yellow
} else {
    Write-Host "  Журнал:        каталог $dataDirectory не найден (служба ещё не запускалась)" -ForegroundColor Yellow
}

Write-Host "  Диагностика:   $probeUrl/api/diagnostics" -ForegroundColor DarkGray
