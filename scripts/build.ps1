<#
.SYNOPSIS
    Сборка и публикация ApplicationActive.

.DESCRIPTION
    Собирает решение и (при -Publish) публикует службу вместе с агентом в отдельный каталог.
    Порт и адрес Web-панели берутся из appsettings.json — скрипт ничего не задаёт сам.

.PARAMETER Publish
    Выполнить публикацию в -Output (по умолчанию <корень>\publish).

.PARAMETER SelfContained
    Публиковать со средой выполнения .NET (не требует установленного .NET на целевой машине).

.PARAMETER Configuration
    Конфигурация сборки: Debug или Release (по умолчанию Release).

.PARAMETER Output
    Каталог публикации.

.PARAMETER Parallel
    Разрешить многопроцессную сборку MSBuild (по умолчанию выключено: в ограниченных окружениях
    многопроцессная сборка может падать на именованных каналах).

.EXAMPLE
    .\build.ps1 -Publish
    .\build.ps1 -Publish -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$SelfContained,
    [string]$Configuration = 'Release',
    [string]$Output = '',
    [switch]$Parallel
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$serviceProject = Join-Path $root 'src\AppActive.Service\AppActive.Service.csproj'
$settingsPath = Join-Path $root 'src\AppActive.Service\appsettings.json'

if (-not $Output) { $Output = Join-Path $root 'publish' }

# В ограниченных окружениях (песочницы, CI) многопроцессная сборка MSBuild использует именованные
# каналы и может завершаться молчаливой ошибкой — по умолчанию собираем в один процесс.
$msbuildArgs = @()
if (-not $Parallel) { $msbuildArgs += @('-m:1') }

Write-Host "ApplicationActive: сборка ($Configuration)" -ForegroundColor Cyan

if ($Publish) {
    $publishArgs = @(
        'publish', $serviceProject,
        '-c', $Configuration,
        '-o', $Output,
        '--nologo',
        # Проект не использует рабочие нагрузки; на SDK без манифестов нагрузок резолвер
        # печатает ошибку и возвращает код 1, хотя публикация проходит успешно.
        '/p:MSBuildEnableWorkloadResolver=false'
    ) + $msbuildArgs

    if ($SelfContained) {
        $publishArgs += @('-r', 'win-x64', '--self-contained', 'true')
    } else {
        $publishArgs += @('--self-contained', 'false')
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Публикация завершилась с кодом $LASTEXITCODE" }

    Write-Host "Публикация: $Output" -ForegroundColor Green
} else {
    $buildArgs = @('build', (Join-Path $root 'ApplicationActive.sln'), '-c', $Configuration, '--nologo') + $msbuildArgs
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с кодом $LASTEXITCODE" }
}

# Проверяем, что рядом со службой лежит агент и Web-панель — без них учёт работать не будет.
$checkDir = if ($Publish) { $Output } else { Join-Path $root "src\AppActive.Service\bin\$Configuration\net8.0" }
foreach ($required in @('AppActive.Service.exe', 'wwwroot\index.html', 'agent\AppActive.Agent.exe')) {
    $path = Join-Path $checkDir $required
    if (Test-Path $path) {
        Write-Host "  [OK]   $required" -ForegroundColor DarkGreen
    } else {
        Write-Warning "  [НЕТ]  $required (ожидался в $checkDir)"
    }
}

if (Test-Path $settingsPath) {
    $urls = (Get-Content $settingsPath -Raw | ConvertFrom-Json).AppActive.Urls
    Write-Host "Адрес Web-панели из appsettings.json: $($urls -join ', ')" -ForegroundColor Cyan
    Write-Host "Каталог данных из appsettings.json: $(((Get-Content $settingsPath -Raw | ConvertFrom-Json).AppActive.DataDirectory))"
}
