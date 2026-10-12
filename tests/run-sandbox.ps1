param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\sandbox-output'),
    [string]$DesktopExe = (Join-Path $PSScriptRoot '..\publish\win-x64\SpeedForge.exe'),
    [switch]$NoBuild,
    [switch]$NoUi,
    [switch]$UiOnly,
    [switch]$ResetOnly,
    [switch]$GeoOnly
)
$ErrorActionPreference = 'Stop'
if ($UiOnly -and $NoUi) { throw 'UiOnly и NoUi несовместимы' }
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dotnet = (Get-Command dotnet).Source
$owned = @()
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
    $owned += Background 'sandbox-proxy' $dotnet @((Quoted $tests),'--sandbox-serve',(Quoted (Join-Path $output 'proxy')))
    Start-Sleep -Milliseconds 1200
    foreach ($process in $owned) { if ($process.HasExited) { throw ('Сервис песочницы завершился, PID='+$process.Id) } }
    if (!$UiOnly) {
    & $dotnet $tests --http --iperf --iperf2 2>&1 | Tee-Object -FilePath (Join-Path $output 'regression-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Регрессионный набор не прошёл' }
    & $dotnet $tests --sandbox-test (Join-Path $output 'core') 2>&1 | Tee-Object -FilePath (Join-Path $output 'sandbox-tests.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Сценарии песочницы не прошли' }
    }
    if (!$NoUi) {
        if (!(Test-Path -LiteralPath $DesktopExe)) { throw 'Сначала соберите portable exe или передайте -DesktopExe' }
        $guiFlag = if($ResetOnly) { '--verify-reset' } elseif($GeoOnly) { '--verify-geo' } else { '--verify-sandbox' }
        $gui = Background 'desktop' $DesktopExe @($guiFlag,(Quoted (Join-Path $output 'ui')))
        $owned += $gui
        $gui.WaitForExit()
        if ($gui.ExitCode -ne 0) { throw ('Проверки интерфейса не прошли: '+(Join-Path $output 'ui\ui-tests.json')) }
        Get-Content -LiteralPath (Join-Path $output 'ui\ui-summary.txt')
    }
    Write-Output 'Песочница: все сценарии пройдены.'
}
finally {
    foreach ($process in $owned) { if (!$process.HasExited) { Stop-Process -Id $process.Id } }
}
