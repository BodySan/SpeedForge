param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\sandbox-output'),
    [string]$DesktopExe = (Join-Path $PSScriptRoot '..\publish\win-x64\SpeedForge.exe'),
    [switch]$NoBuild,
    [switch]$NoUi,
    [switch]$UiOnly,
    [switch]$ResetOnly,
    [switch]$GeoOnly,
    [switch]$ArchiveProfilesOnly
)
$ErrorActionPreference = 'Stop'
# The Windows single-file host expects native separators in its extraction path.
if ($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR) {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = [IO.Path]::GetFullPath($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR).Replace('/', '\')
}
if ($UiOnly -and $NoUi) { throw 'UiOnly и NoUi несовместимы' }
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dotnet = (Get-Command dotnet).Source
$owned = @()
$legacyOwned = @()
function Quoted([string]$path) { '"' + $path + '"' }
function Background([string]$name, [string]$exe, [string[]]$arguments) {
    Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output ($name+'.stdout.txt')) -RedirectStandardError (Join-Path $output ($name+'.stderr.txt'))
}
try {
    if (!$NoBuild) {
        & $dotnet build (Join-Path $project 'SpeedForge.slnx') -c Release -m:1 -p:UseSharedCompilation=false -warnaserror
        if ($LASTEXITCODE -ne 0) { throw 'Сборка не прошла' }
    }
    $tests = Join-Path $project 'tests\SpeedForge.SelfTests\bin\Release\net10.0\SpeedForge.SelfTests.dll'
    $endpoint = Join-Path $project 'src\SpeedForge.Endpoint\bin\Release\net10.0\SpeedForge.Endpoint.dll'
    & $dotnet $tests --tools-json (Join-Path $output 'tools.json')
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось проверить встроенные измерители' }
    $tools = Get-Content -LiteralPath (Join-Path $output 'tools.json') -Raw | ConvertFrom-Json
    $owned += Background 'endpoint' $dotnet @((Quoted $endpoint))
    $owned += Background 'sandbox-http' $dotnet @((Quoted $endpoint),'--sandbox=true')
    $owned += Background 'iperf3' $tools.Iperf3.Path @('-s','-B','127.0.0.1','-p','55201','--forceflush')
    $owned += Background 'iperf2' $tools.Iperf2.Path @('-s','-u','-U','-B','127.0.0.1','-p','55202')
    $owned += Background 'iperf2-pool' $tools.Iperf2.Path @('-s','-u','-B','127.0.0.1','-p','55301-55340')
    $owned += Background 'iperf2-limited' $tools.Iperf2.Path @('-s','-u','-U','-B','127.0.0.1','-p','55211')
    foreach ($port in 55401..55529) {
        $process = Background ('iperf2-legacy-'+$port) $tools.Iperf2Legacy.Path @('-s','-u','-B','127.0.0.1','-p',$port.ToString())
        $legacyOwned += $process; $owned += $process
    }
    $owned += Background 'sandbox-proxy' $dotnet @((Quoted $tests),'--sandbox-serve',(Quoted (Join-Path $output 'proxy')))
    Start-Sleep -Milliseconds 1200
    foreach ($process in $owned) { if ($process.HasExited) { throw ('Сервис песочницы завершился, PID='+$process.Id) } }
    if ($ArchiveProfilesOnly) {
        & $dotnet $tests --audit-beeline (Join-Path $output 'archive-profiles') --local --archive-rates 2>&1 | Tee-Object -FilePath (Join-Path $output 'archive-profiles.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Команды архива не прошли' }
        $audit = Get-Content -LiteralPath (Join-Path $output 'archive-profiles\beeline-profiles.json') -Raw | ConvertFrom-Json
        if (@($audit.Runs | Select-Object -First 12 | Where-Object { $_.Status -ne 'Measured' }).Count -gt 0) { throw 'Не все исходные профили подтвердили приём' }
    }
    elseif (!$UiOnly) {
    & $dotnet $tests --http --iperf --iperf2 2>&1 | Tee-Object -FilePath (Join-Path $output 'regression-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Регрессионный набор не прошёл' }
    & $dotnet $tests --sandbox-test (Join-Path $output 'core') 2>&1 | Tee-Object -FilePath (Join-Path $output 'sandbox-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Сценарии песочницы не прошли' }
    }
    if (!$NoUi -and !$ArchiveProfilesOnly) {
        if (!$UiOnly) {
            # Old Windows iPerf2 receivers can retain completed UDP sessions.
            # Give UI tests a fresh native pool, independent of the core tests.
            foreach ($process in $legacyOwned) {
                if (!$process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue; $process.WaitForExit() }
            }
            foreach ($port in 55401..55529) {
                $owned += Background ('iperf2-legacy-ui-'+$port) $tools.Iperf2Legacy.Path @('-s','-u','-B','127.0.0.1','-p',$port.ToString())
            }
            Start-Sleep -Milliseconds 400
        }
        if (!(Test-Path -LiteralPath $DesktopExe)) { throw 'Сначала соберите portable exe или передайте -DesktopExe' }
        $guiFlag = if($ResetOnly) { '--verify-reset' } elseif($GeoOnly) { '--verify-geo' } else { '--verify-sandbox' }
        $gui = Background 'desktop' $DesktopExe @($guiFlag,(Quoted (Join-Path $output 'ui')))
        $owned += $gui
        $gui.WaitForExit()
        if ($gui.ExitCode -ne 0) { throw ('Проверки интерфейса не прошли (exit='+$gui.ExitCode+'): '+(Join-Path $output 'ui\ui-tests.json')) }
        Get-Content -LiteralPath (Join-Path $output 'ui\ui-summary.txt')
    }
    Write-Output 'Песочница: все сценарии пройдены.'
}
finally {
    foreach ($process in $owned) { if (!$process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue } }
}
