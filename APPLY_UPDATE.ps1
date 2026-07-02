$ErrorActionPreference = 'Stop'

$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$stageRoot = Join-Path $env:TEMP 'LaunchBridgeSelfUpdate'
$token = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $stageRoot ("v0_3_1_" + $token)
$bootLog = Join-Path $env:TEMP 'LaunchBridge_Update_v0_3_1_bootstrap.log'

function Write-BootLog([string]$message) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    Add-Content -LiteralPath $bootLog -Value ("[$stamp] $message") -Encoding UTF8
}

try {
    Set-Content -LiteralPath $bootLog -Value 'LaunchBridge v0.3.1 PowerShell recovery bootstrap' -Encoding UTF8
    Write-BootLog ("Source: " + $source)
    Write-BootLog ("Detached stage: " + $stage)

    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force
    }

    $stage2 = Join-Path $stage 'SELF_UPDATE_STAGE2.ps1'
    if (-not (Test-Path -LiteralPath $stage2 -PathType Leaf)) { throw 'SELF_UPDATE_STAGE2.ps1 was not copied.' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'src\MainForm.cs') -PathType Leaf)) { throw 'MainForm.cs was not copied.' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'assets\LaunchBridge.ico') -PathType Leaf)) { throw 'LaunchBridge.ico was not copied.' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'app.manifest') -PathType Leaf)) { throw 'app.manifest was not copied.' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'LaunchBridge.exe.config') -PathType Leaf)) { throw 'LaunchBridge.exe.config was not copied.' }

    $powershell = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $powershell)) { $powershell = 'powershell.exe' }
    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-WindowStyle', 'Hidden',
        '-File', ('"' + $stage2 + '"'),
        '-Stage', ('"' + $stage + '"')
    ) -join ' '

    Write-BootLog 'Starting detached stage 2.'
    $process = Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if ($null -eq $process) { throw 'Windows did not start the detached updater.' }
    Write-BootLog ("Detached stage 2 PID: " + $process.Id)
    exit 0
}
catch {
    Write-BootLog ("ERROR: " + $_.Exception.ToString())
    Start-Process -FilePath 'notepad.exe' -ArgumentList ('"' + $bootLog + '"') | Out-Null
    exit 1
}
