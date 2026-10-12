# Windows PowerShell / PowerShell 7. Execute from project root with .NET 10 SDK installed.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
Write-Host 'Проверка наличия .NET SDK 10...'
$version = (& dotnet --version)
if ($LASTEXITCODE -ne 0 -or -not $version.StartsWith('10.')) { throw "Необходим .NET SDK 10: $version" }
Write-Host 'Сборка проекта...'
& dotnet build SpeedForge.slnx -c Release -warnaserror -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Сборка не пройдена' }
Write-Host 'Набор автономных испытаний...'
& dotnet run --project tests/SpeedForge.SelfTests/SpeedForge.SelfTests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Испытания не пройдены' }
Write-Host 'Публикация переносимого приложения без установщика...'
& dotnet publish src/SpeedForge.Desktop/SpeedForge.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw 'Публикация не пройдена' }
Write-Host 'Готово. Результат: publish/win-x64/SpeedForge.exe'
Write-Host 'iPerf2, iPerf3, зависимости, лицензии и исходники компонентов встроены в exe.'
