$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if (!(Test-Path $msbuild)) {
    $msbuild = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
}

Get-Process -Name "ScreenTranslator" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

Write-Host "=== 1. Сборка основного приложения (Release x64) ===" -ForegroundColor Cyan
& $msbuild "$root\src\ScreenTranslator\ScreenTranslator.csproj" /p:Configuration=Release /v:minimal

Write-Host "=== 2. Сборка и запуск автоматических тестов ===" -ForegroundColor Cyan
& $msbuild "$root\tests\ScreenTranslator.Tests\ScreenTranslator.Tests.csproj" /p:Configuration=Release /v:minimal
& "$root\bin\Tests\ScreenTranslator.Tests.exe"
if ($LASTEXITCODE -ne 0) {
    throw "Автоматические тесты завершились с ошибкой."
}

Write-Host "=== 3. Сборка установщика ScreenTranslatorSetup.exe ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$root\dist" | Out-Null
Copy-Item "$root\bin\Release\ScreenTranslator.exe" "$root\dist\ScreenTranslator.exe" -Force
& $msbuild "$root\installer\ScreenTranslatorSetup.csproj" /p:Configuration=Release /v:minimal

Write-Host "=== Сборка успешно завершена! Файлы в папке dist\ ===" -ForegroundColor Green
Get-ChildItem "$root\dist\*.exe" | Select-Object Name, Length, FullName
