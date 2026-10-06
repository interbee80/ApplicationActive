<#
.SYNOPSIS
    Готовит локальный офлайн-источник пакетов NuGet для сборки без интернета.

.DESCRIPTION
    Копирует нужные пакеты из глобального кэша NuGet (%USERPROFILE%\.nuget\packages)
    в каталог .packages рядом с решением. Он подключён в nuget.config первым источником,
    поэтому `dotnet build` работает без доступа к nuget.org.

    Запускать, если сборка ругается на отсутствующие пакеты (NU1101) при отсутствии интернета.

.EXAMPLE
    .\setup-offline-packages.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $root '.packages'
$cache = Join-Path $env:USERPROFILE '.nuget\packages'

# Версии привязаны к Directory.Build.props (net8.0) и ссылкам на пакеты в проектах.
$wanted = @(
    @{ Id = 'microsoft.data.sqlite.core';          Version = '7.0.13' },
    @{ Id = 'sqlitepclraw.bundle_e_sqlite3';       Version = '2.1.4' },
    @{ Id = 'sqlitepclraw.core';                   Version = '2.1.4' },
    @{ Id = 'sqlitepclraw.lib.e_sqlite3';          Version = '2.1.4' },
    @{ Id = 'sqlitepclraw.provider.e_sqlite3';     Version = '2.1.4' },
    @{ Id = 'microsoft.netcore.app.ref';           Version = '8.0.22' },
    @{ Id = 'microsoft.aspnetcore.app.ref';        Version = '8.0.22' },
    @{ Id = 'microsoft.windowsdesktop.app.ref';    Version = '8.0.22' },
    @{ Id = 'microsoft.netcore.app.host.win-x64';  Version = '8.0.22' }
)

if (-not (Test-Path $cache)) { throw "Не найден кэш NuGet: $cache" }
if (-not (Test-Path $feed)) { New-Item -ItemType Directory -Path $feed -Force | Out-Null }

$copied = 0
$missing = @()
foreach ($package in $wanted) {
    $source = Join-Path (Join-Path $cache $package.Id) $package.Version
    $nupkg = Get-ChildItem $source -Filter '*.nupkg' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($nupkg) {
        Copy-Item $nupkg.FullName -Destination $feed -Force
        $copied++
        Write-Host "  [OK]   $($nupkg.Name)"
    } else {
        $missing += "$($package.Id) $($package.Version)"
        Write-Warning "  [НЕТ]  $($package.Id) $($package.Version) (нет в кэше)"
    }
}

Write-Host ""
Write-Host "Скопировано пакетов: $copied в $feed" -ForegroundColor Green
if ($missing.Count -gt 0) {
    Write-Warning "Не найдены в кэше: $($missing -join ', '). Их нужно скачать при наличии интернета: dotnet restore"
} else {
    Write-Host 'Сборка без интернета готова: dotnet build ApplicationActive.sln -m:1' -ForegroundColor Green
}
