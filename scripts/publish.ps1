<#
.SYNOPSIS
  Сборка, тесты, публикация MailClient.exe и (если установлен Inno Setup 6) установщика.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "0.1.0",
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$out = Join-Path $root "publish\$Runtime"

Write-Host "== Тесты" -ForegroundColor Cyan
dotnet test tests\MailClient.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "Тесты не пройдены" }

Write-Host "== Публикация в $out" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src\MailClient.App -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "Публикация не удалась" }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    Write-Host "== Установщик" -ForegroundColor Cyan
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$out" installer\MailClient.iss
    if ($LASTEXITCODE -ne 0) { throw "Сборка установщика не удалась" }
    Write-Host "Готово: publish\MailClient-Setup-$Version.exe" -ForegroundColor Green
} else {
    Write-Host "Inno Setup 6 не найден — установщик не собран. Программа: $out\MailClient.exe" -ForegroundColor Yellow
}
