<#
.SYNOPSIS
    Установка ApplicationActive как службы Windows (от имени SYSTEM, автозапуск).

.DESCRIPTION
    Копирует опубликованные файлы в каталог установки, создаёт службу с запуском от LocalSystem
    и автоматическим стартом, настраивает перезапуск при сбое и (по желанию) правило брандмауэра.
    Все параметры (имя службы, адрес и порт Web-панели, каталог данных) берутся из appsettings.json.

    Требуются права администратора.

.PARAMETER SourcePath
    Каталог с опубликованными файлами (результат build.ps1 -Publish). По умолчанию <корень>\publish.

.PARAMETER InstallDir
    Каталог установки. По умолчанию C:\Program Files\AppActive.

.PARAMETER AddFirewallRule
    Добавить правило брандмауэра для порта Web-панели (нужно, если панель открывается с других машин).

.PARAMETER NoStart
    Не запускать службу после установки.

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -AddFirewallRule
#>
[CmdletBinding()]
param(
    [string]$SourcePath = '',
    [string]$InstallDir = '',
    [switch]$AddFirewallRule,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $root 'src\AppActive.Service\appsettings.json'
if (-not $SourcePath) { $SourcePath = Join-Path $root 'publish' }
if (-not $InstallDir) { $InstallDir = Join-Path ${env:ProgramFiles} 'AppActive' }

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Требуются права администратора. Запустите PowerShell от имени администратора и повторите."
    }
}

Assert-Administrator

# ---------------------------------------------------------------- конфигурация
if (-not (Test-Path $settingsPath)) { throw "Не найден $settingsPath" }
$config = (Get-Content $settingsPath -Raw | ConvertFrom-Json).AppActive
$serviceName = $config.ServiceName
$displayName = $config.ServiceDisplayName
$description = $config.ServiceDescription
$dataDirectory = [Environment]::ExpandEnvironmentVariables($config.DataDirectory)

if (-not $serviceName) { throw 'В appsettings.json не задано AppActive:ServiceName' }
if (-not $config.Urls -or $config.Urls.Count -eq 0) { throw 'В appsettings.json не задано AppActive:Urls' }

$url = $config.Urls[0]
$uri = [Uri]$url
$port = $uri.Port
$hostName = $uri.Host

Write-Host "Установка ApplicationActive" -ForegroundColor Cyan
Write-Host "  Имя службы:      $serviceName"
Write-Host "  Каталог установки: $InstallDir"
Write-Host "  Web-панель:      $url"
Write-Host "  Каталог данных:  $dataDirectory"
Write-Host "  Источник файлов: $SourcePath"

if (-not (Test-Path (Join-Path $SourcePath 'AppActive.Service.exe'))) {
    throw "В $SourcePath нет AppActive.Service.exe. Сначала выполните: scripts\build.ps1 -Publish"
}

# ---------------------------------------------------------------- файлы
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Write-Host "Копирование файлов…" -ForegroundColor Cyan
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallDir -Recurse -Force

# appsettings.json лежит рядом со службой — это единственная точка настройки.
$installedSettings = Join-Path $InstallDir 'appsettings.json'
if (-not (Test-Path $installedSettings)) { throw "После копирования не найден $installedSettings" }

# Каталог данных: создаём заранее, чтобы служба сразу могла писать (права уточнит сама служба).
if (-not (Test-Path $dataDirectory)) { New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null }

# ---------------------------------------------------------------- событие в журнале Windows
try {
    if (-not [System.Diagnostics.EventLog]::SourceExists($serviceName)) {
        New-EventLog -LogName Application -Source $serviceName -ErrorAction Stop
        Write-Host "Зарегистрирован источник журнала событий: $serviceName" -ForegroundColor DarkGreen
    }
} catch {
    Write-Warning "Не удалось зарегистрировать источник журнала событий: $($_.Exception.Message)"
}

# ---------------------------------------------------------------- служба
$exePath = Join-Path $InstallDir 'AppActive.Service.exe'
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Служба уже установлена — обновляем файлы и параметры" -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', '00:00:30')
    }
    & sc.exe config $serviceName binPath= "`"$exePath`"" start= auto obj= LocalSystem DisplayName= "`"$displayName`"" | Out-Null
} else {
    & sc.exe create $serviceName binPath= "`"$exePath`"" start= auto obj= LocalSystem DisplayName= "`"$displayName`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create завершился с кодом $LASTEXITCODE" }
    Write-Host "Служба создана: $serviceName (LocalSystem, автозапуск)" -ForegroundColor Green
}

if ($description) { & sc.exe description $serviceName "$description" | Out-Null }

# Перезапуск при сбое: через 1 минуту, три попытки, счётчик сбрасывается раз в сутки.
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
& sc.exe sidtype $serviceName unrestricted | Out-Null

# ---------------------------------------------------------------- брандмауэр
if ($AddFirewallRule) {
    $ruleName = "$serviceName Web"
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -Profile Any | Out-Null
    Write-Host "Добавлено правило брандмауэра для TCP-порта $port" -ForegroundColor Green
}

# ---------------------------------------------------------------- запуск
if (-not $NoStart) {
    Write-Host "Запуск службы…" -ForegroundColor Cyan
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', '00:00:30')
}

$service = Get-Service -Name $serviceName
Write-Host ""
Write-Host "Готово. Служба '$serviceName': $($service.Status), тип запуска: $($service.StartType)" -ForegroundColor Green
Write-Host "Web-панель: $url"

if ($hostName -eq '127.0.0.1' -or $hostName -eq 'localhost') {
    Write-Host "Панель доступна только с этой машины. Чтобы открыть её с других машин," -ForegroundColor Yellow
    Write-Host "замените в appsettings.json AppActive:Urls на http://0.0.0.0:$port, задайте" -ForegroundColor Yellow
    Write-Host "AppActive:Security:AdminKey и запустите этот скрипт повторно с -AddFirewallRule." -ForegroundColor Yellow
} else {
    Write-Host "Проверить доступность: http://<имя-машины>:$port" -ForegroundColor Yellow
}

Write-Host "Состояние и агенты: scripts\service-status.ps1" -ForegroundColor DarkGray
