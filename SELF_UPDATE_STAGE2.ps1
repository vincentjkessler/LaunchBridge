param(
    [Parameter(Mandatory=$false)]
    [string]$Stage
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Stage)) { $Stage = Split-Path -Parent $MyInvocation.MyCommand.Path }
Set-Location -LiteralPath $Stage

$install = Join-Path $env:LOCALAPPDATA 'DevMind\LaunchBridge'
$logRoot = Join-Path $install 'logs'
$build = Join-Path $Stage 'compiled'
$log = Join-Path $logRoot 'self-update-0.3.1.log'
$csc64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$csc32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$csc = if (Test-Path -LiteralPath $csc64) { $csc64 } else { $csc32 }

function Write-UpdateLog([string]$message) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    Add-Content -LiteralPath $log -Value ("[$stamp] $message") -Encoding UTF8
}

function Fail-Update([string]$message) {
    Write-UpdateLog ("ERROR: " + $message)
    try { Start-Process -FilePath 'notepad.exe' -ArgumentList ('"' + $log + '"') | Out-Null } catch { }
    exit 1
}

function Quote-Rsp([string]$value) {
    return '"' + $value.Replace('"','\\"') + '"'
}

try {
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    Set-Content -LiteralPath $log -Value 'LaunchBridge v0.3.1 detached PowerShell self-update' -Encoding UTF8
    Write-UpdateLog ("Stage: " + $Stage)

    Start-Sleep -Milliseconds 1200
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { Fail-Update 'Windows C# compiler was not found.' }
    Write-UpdateLog ("Compiler: " + $csc)

    if (Test-Path -LiteralPath $build) { Remove-Item -LiteralPath $build -Recurse -Force }
    New-Item -ItemType Directory -Path $build -Force | Out-Null
    New-Item -ItemType Directory -Path $install -Force | Out-Null

    $builtExe = Join-Path $build 'LaunchBridge.exe'
    $builtSmartClickHost = Join-Path $build 'LaunchBridgeSmartClickHost.exe'
    $icon = Join-Path $Stage 'assets\LaunchBridge.ico'
    $manifest = Join-Path $Stage 'app.manifest'
    $runtimeConfig = Join-Path $Stage 'LaunchBridge.exe.config'
    $smartClickHostSource = Join-Path $Stage 'src\SmartClickNativeHost.cs'
    $sources = @(
        (Join-Path $Stage 'src\Program.cs'),
        (Join-Path $Stage 'src\SingleInstanceCoordinator.cs'),
        (Join-Path $Stage 'src\Models.cs'),
        (Join-Path $Stage 'src\LaunchBridgeCore.cs'),
        (Join-Path $Stage 'src\MainForm.cs')
    )
    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { Fail-Update ("Missing compiler source: " + $source) }
    }
    if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { Fail-Update ("Missing icon: " + $icon) }
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { Fail-Update ("Missing application manifest: " + $manifest) }
    if (-not (Test-Path -LiteralPath $runtimeConfig -PathType Leaf)) { Fail-Update ("Missing runtime configuration: " + $runtimeConfig) }
    if (-not (Test-Path -LiteralPath $smartClickHostSource -PathType Leaf)) { Fail-Update ("Missing Smart Click native host source: " + $smartClickHostSource) }

    # A response file avoids Start-Process quoting corruption and works with paths containing spaces.
    $responseFile = Join-Path $build 'launchbridge.rsp'
    $responseLines = @(
        '/nologo',
        '/target:winexe',
        '/platform:anycpu',
        '/optimize+',
        ('/win32icon:' + (Quote-Rsp $icon)),
        ('/win32manifest:' + (Quote-Rsp $manifest)),
        ('/out:' + (Quote-Rsp $builtExe)),
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll',
        '/reference:System.Management.dll',
        '/reference:System.IO.Compression.dll',
        '/reference:System.IO.Compression.FileSystem.dll',
        '/reference:System.Web.Extensions.dll'
    ) + ($sources | ForEach-Object { Quote-Rsp $_ })
    Set-Content -LiteralPath $responseFile -Value $responseLines -Encoding ASCII

    $compilerLog = Join-Path $build 'compiler.full.log'
    Write-UpdateLog 'Compiling LaunchBridge through a csc response file before stopping the working installation.'
    Write-UpdateLog ("Response file: " + $responseFile)

    $compilerOutput = & $csc ('@' + $responseFile) 2>&1
    $compilerExit = $LASTEXITCODE
    $compilerOutput | Set-Content -LiteralPath $compilerLog -Encoding UTF8
    if ($compilerOutput) {
        foreach ($line in $compilerOutput) { Write-UpdateLog ("CSC: " + [string]$line) }
    }
    if ($compilerExit -ne 0) {
        Fail-Update ("Compilation failed with exit code " + $compilerExit + ". Full compiler diagnostics are in " + $compilerLog)
    }
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { Fail-Update 'Compiler returned without creating LaunchBridge.exe.' }
    Write-UpdateLog ("Compilation passed. Built SHA-256: " + (Get-FileHash -LiteralPath $builtExe -Algorithm SHA256).Hash)

    $hostResponseFile = Join-Path $build 'smart-click-host.rsp'
    $hostResponseLines = @(
        '/nologo',
        '/target:exe',
        '/platform:anycpu',
        '/optimize+',
        ('/out:' + (Quote-Rsp $builtSmartClickHost)),
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Web.Extensions.dll',
        (Quote-Rsp $smartClickHostSource)
    )
    Set-Content -LiteralPath $hostResponseFile -Value $hostResponseLines -Encoding ASCII
    Write-UpdateLog 'Compiling the Smart Click native messaging host.'
    $hostCompilerOutput = & $csc ('@' + $hostResponseFile) 2>&1
    $hostCompilerExit = $LASTEXITCODE
    if ($hostCompilerOutput) { foreach ($line in $hostCompilerOutput) { Write-UpdateLog ("HOST CSC: " + [string]$line) } }
    if ($hostCompilerExit -ne 0) { Fail-Update ("Smart Click host compilation failed with exit code " + $hostCompilerExit) }
    if (-not (Test-Path -LiteralPath $builtSmartClickHost -PathType Leaf)) { Fail-Update 'Compiler did not create LaunchBridgeSmartClickHost.exe.' }

    Write-UpdateLog 'Closing old LaunchBridge processes.'
    $processNames = @('LaunchBridge', 'DevMindLaunchBridge')
    Get-Process -Name $processNames -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Process -Name $processNames -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Name $processNames -ErrorAction SilentlyContinue) { Fail-Update 'The old LaunchBridge process did not exit within 45 seconds.' }

    $installedExe = Join-Path $install 'LaunchBridge.exe'
    $legacyInstalledExe = Join-Path $install 'DevMindLaunchBridge.exe'
    $copied = $false
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        try {
            Copy-Item -LiteralPath $builtExe -Destination $installedExe -Force
            if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
                $sourceHash = (Get-FileHash -LiteralPath $builtExe -Algorithm SHA256).Hash
                $installedHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
                if ($sourceHash -eq $installedHash) { $copied = $true; break }
            }
        }
        catch { Write-UpdateLog ("Copy attempt " + $attempt + " failed: " + $_.Exception.Message) }
        Start-Sleep -Milliseconds 500
    }
    if (-not $copied) { Fail-Update 'The updated executable could not be copied and verified after 40 attempts.' }
    Remove-Item -LiteralPath $legacyInstalledExe -Force -ErrorAction SilentlyContinue

    Copy-Item -LiteralPath (Join-Path $Stage 'assets\LaunchBridge.ico') -Destination (Join-Path $install 'LaunchBridge.ico') -Force
    Copy-Item -LiteralPath $runtimeConfig -Destination (Join-Path $install 'LaunchBridge.exe.config') -Force
    Copy-Item -LiteralPath $builtSmartClickHost -Destination (Join-Path $install 'LaunchBridgeSmartClickHost.exe') -Force
    $browserExtensionSource = Join-Path $Stage 'browser-extension'
    $browserExtensionDestination = Join-Path $install 'browser-extension'
    if (Test-Path -LiteralPath $browserExtensionDestination) { Remove-Item -LiteralPath $browserExtensionDestination -Recurse -Force }
    Copy-Item -LiteralPath $browserExtensionSource -Destination $browserExtensionDestination -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $Stage 'README.md') -Destination (Join-Path $install 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $Stage 'RELEASE_MANIFEST.json') -Destination (Join-Path $install 'RELEASE_MANIFEST.json') -Force

    # Replace visible legacy shortcut names without creating shortcuts the user did not already have.
    try {
        $shell = New-Object -ComObject WScript.Shell
        $desktop = [Environment]::GetFolderPath('Desktop')
        $legacyDesktopShortcut = Join-Path $desktop 'DevMind LaunchBridge.lnk'
        $desktopShortcut = Join-Path $desktop 'LaunchBridge.lnk'
        if (Test-Path -LiteralPath $legacyDesktopShortcut) {
            $shortcut = $shell.CreateShortcut($desktopShortcut)
            $shortcut.TargetPath = $installedExe
            $shortcut.WorkingDirectory = $install
            $shortcut.IconLocation = $installedExe + ',0'
            $shortcut.Save()
            Remove-Item -LiteralPath $legacyDesktopShortcut -Force -ErrorAction SilentlyContinue
        }

        $startMenuPrograms = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'
        $legacyMenuFolder = Join-Path $startMenuPrograms 'DevMind'
        $legacyMenuShortcut = Join-Path $legacyMenuFolder 'DevMind LaunchBridge.lnk'
        $menuFolder = Join-Path $startMenuPrograms 'LaunchBridge'
        $menuShortcut = Join-Path $menuFolder 'LaunchBridge.lnk'
        if (Test-Path -LiteralPath $legacyMenuShortcut) {
            New-Item -ItemType Directory -Path $menuFolder -Force | Out-Null
            $shortcut = $shell.CreateShortcut($menuShortcut)
            $shortcut.TargetPath = $installedExe
            $shortcut.WorkingDirectory = $install
            $shortcut.IconLocation = $installedExe + ',0'
            $shortcut.Save()
            Remove-Item -LiteralPath $legacyMenuShortcut -Force -ErrorAction SilentlyContinue
            if ((Test-Path -LiteralPath $legacyMenuFolder) -and -not (Get-ChildItem -LiteralPath $legacyMenuFolder -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $legacyMenuFolder -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch { Write-UpdateLog ("Shortcut migration warning: " + $_.Exception.Message) }

    Write-UpdateLog 'Re-registering package associations.'
    $register = Start-Process -FilePath $installedExe -ArgumentList '--install-defaults' -Wait -PassThru -WindowStyle Hidden
    Write-UpdateLog 'Registering the Smart Click native messaging host.'
    $smartClickRegister = Start-Process -FilePath $installedExe -ArgumentList '--install-smart-click' -Wait -PassThru -WindowStyle Hidden
    if ($smartClickRegister.ExitCode -ne 0) { Write-UpdateLog ('Smart Click registration warning: exit code ' + $smartClickRegister.ExitCode) }
    if ($register.ExitCode -ne 0) { Write-UpdateLog ("Association registration returned exit code " + $register.ExitCode + ".") }

    Write-UpdateLog 'Starting LaunchBridge v0.3.1.'
    $newProcess = Start-Process -FilePath $installedExe -ArgumentList '--update-complete 0.3.1' -PassThru
    if ($null -eq $newProcess) { Fail-Update 'Windows did not start the updated LaunchBridge.' }
    Write-UpdateLog ("Updated LaunchBridge PID: " + $newProcess.Id)
    Write-UpdateLog 'Update complete.'
    exit 0
}
catch {
    Fail-Update $_.Exception.ToString()
}
