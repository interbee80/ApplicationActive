<#
.SYNOPSIS
    Удаление службы ApplicationActive.

.DESCRIPTION
    Останавливает и удаляет службу, при необходимости удаляет правило брандмауэра.
    Каталог данных (база, журналы, конфигурации агентов) сохраняется, если не указан -PurgeData.

.PARAMETER PurgeData
    Удалить каталог данных (база и журналы будут потеряны безвозвратно).

.PARAMETER RemoveFirewallRule
    Удалить правило брандмауэра, созданное при установке.

.PARAMETER InstallDir
    Каталог установки (по умолчанию C:\Program Files\AppActive). Файлы удаляются, если указан -RemoveFiles.

.PARAMETER RemoveFiles
    Удалить файлы программы из каталога установки.

.EXAMPLE
    .\uninstall-service.ps1
    .\uninstall-service.ps1 -PurgeData -RemoveFiles -RemoveFirewallRule
#>
[CmdletBinding()]
param(
    [switch]$PurgeData,
    [switch]$RemoveFirewallRule,
    [switch]$RemoveFiles,
    [string]$InstallDir = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $root 'src\AppActive.Service\appsettings.json'
if (-not $InstallDir) { $InstallDir = Join-Path ${env:ProgramFiles} 'AppActive' }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Требуются права администратора. Запустите PowerShell от имени администратора и повторите.'
}

if (-not (Test-Path $settingsPath)) { throw "Не найден $settingsPath" }
$config = (Get-Content $settingsPath -Raw | ConvertFrom-Json).AppActive
$serviceName = $config.ServiceName
$dataDirectory = [Environment]::ExpandEnvironmentVariables($config.DataDirectory)
$port = if ($config.Urls -and $config.Urls.Count -gt 0) { ([Uri]$config.Urls[0]).Port } else { 0 }

Write-Host "Удаление ApplicationActive" -ForegroundColor Cyan

# До удаления службы полезно узнать путь к агенту, чтобы убрать «осиротевшие» процессы.
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Остановка службы…"
        Stop-Service -Name $serviceName -Force
        try { $service.WaitForStatus('Stopped', '00:00:30') } catch { Write-Warning 'Служба не остановилась за 30 секунд' }
    }
    & sc.exe delete $serviceName | Out-Null
    Write-Host "Служба удалена: $serviceName" -ForegroundColor Green
} else {
    Write-Host "Служба $serviceName не найдена — пропускаем" -ForegroundColor Yellow
}

# Агенты сеансов завершаются вместе со службой; подстраховываемся.
foreach ($process in Get-Process -Name 'AppActive.Agent' -ErrorAction SilentlyContinue) {
    try { $process.Kill(); Write-Host "Остановлен агент сеанса PID=$($process.Id)" } catch { }
}

if ($RemoveFirewallRule -and $port -gt 0) {
    $ruleName = "$serviceName Web"
    if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -DisplayName $ruleName
        Write-Host "Удалено правило брандмауэра для порта $port" -ForegroundColor Green
    }
}

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "Удалён каталог установки: $InstallDir" -ForegroundColor Green
}

if ($PurgeData -and (Test-Path $dataDirectory)) {
    Remove-Item -Path $dataDirectory -Recurse -Force
    Write-Host "Удалён каталог данных: $dataDirectory" -ForegroundColor Yellow
} elseif (Test-Path $dataDirectory) {
    Write-Host "Каталог данных сохранён: $dataDirectory (удалить: -PurgeData)" -ForegroundColor DarkGray
}

Write-Host 'Готово.' -ForegroundColor Green
