<#
  Installs the Dictum MSI for the current user, checks the result, reinstalls a rebuilt MSI of the same version,
  upgrades over a running daemon, and uninstalls, checking that nothing of the program is left and that user data stays.

  It works on this user's real Dictum:
  - The run ends with Dictum uninstalled. An installed Dictum MSI is replaced, then removed with HKCU\Software\Dictum
    and the HKCU Run value "Dictum", whichever copy wrote it. A newer installed Dictum makes the first install fail
    with the downgrade error: uninstall it first.
  - dictum init overwrites %APPDATA%\Dictum\config.json and downloads its model into %LOCALAPPDATA%\Dictum; the daemon
    runs on the default socket and logs there too.
  So stop a working Dictum and back up %APPDATA%\Dictum and the Run value first, and reinstall Dictum afterwards.
  It runs only with GITHUB_ACTIONS=true (the opt-in) and RUNNER_TEMP (a new folder for its logs) set.

  The three MSIs come from one staged folder. <out> is a new folder, <version> what
  `dotnet msbuild src/Dictum.Cli/Dictum.Cli.csproj -getProperty:VersionPrefix` prints. From the repository root, in
  Git Bash (the bash PowerShell finds may be WSL's):
    bash packaging/package.sh win-x64 '<out>'
  That writes <out>\dictum-<version>-win-x64.msi and the staged files in <out>\stage\dictum-<version>-win-x64. Then,
  in PowerShell:
    dotnet build packaging/windows/Dictum.Installer.wixproj -c Release --no-incremental `
      -p:StageDir=<out>\stage\dictum-<version>-win-x64 -o <out>\rebuilt
    dotnet build packaging/windows/Dictum.Installer.wixproj -c Release --no-incremental `
      -p:StageDir=<out>\stage\dictum-<version>-win-x64 -p:VersionPrefix=99.0.0 -o <out>\upgrade
    $env:GITHUB_ACTIONS = 'true'; $env:RUNNER_TEMP = '<a new folder>'
    ./packaging/windows/test-msi-install.ps1 -Msi <out>\dictum-<version>-win-x64.msi `
      -RebuiltMsi <out>\rebuilt\dictum.msi -UpgradeMsi <out>\upgrade\dictum.msi -Version <version>
  An upgrade MSI built with another VersionPrefix, above <version>, is named with -UpgradeVersion.
#>
param(
    [Parameter(Mandatory)] [string] $Msi,
    [Parameter(Mandatory)] [string] $RebuiltMsi,
    [Parameter(Mandatory)] [string] $UpgradeMsi,
    [Parameter(Mandatory)] [string] $Version,
    [string] $UpgradeVersion = '99.0.0'
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') {
    Write-Output 'test-msi-install.ps1 needs GITHUB_ACTIONS=true and RUNNER_TEMP: it installs and uninstalls Dictum for the current user.'
    exit 2
}
. (Join-Path $PSScriptRoot 'msi-smoke.ps1')

$failed = $false
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Output "PASS: $what" } else { Write-Output "FAIL: $what"; $script:failed = $true }
}
$folder = Join-Path $env:LOCALAPPDATA 'Programs\Dictum'
$dictum = Join-Path $folder 'dictum.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Dictum.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
# Every log the run writes, in one folder.
$logs = Join-Path $env:RUNNER_TEMP 'dictum-logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$daemonStartSeconds = 30
$statusPollMilliseconds = 500
$daemonExitMilliseconds = 5000
function Msiexec([string] $verb, [string] $path, [string] $name, [switch] $NoRestartManager) {
    $arguments = Get-MsiexecArguments $verb $path (Join-Path $logs "msiexec-$name.log") -NoRestartManager:$NoRestartManager
    return (Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru).ExitCode
}
function Products { @(Get-InstalledProducts 'Dictum') }
function DictumProcesses { @(Get-Process dictum, dictum-gui -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$folder\*" }) }

# Install.
Check ((Msiexec 'i' $Msi 'install') -eq 0) 'the MSI installs for this user (exit 0, no reboot)'
foreach ($file in 'dictum.exe', 'dictum-gui.exe', 'runtimes\vulkan\win-x64\whisper.dll', 'uk\dictum-gui.resources.dll') {
    Check (Test-Path (Join-Path $folder $file)) "$file is installed in $folder"
}
Check (Test-Path $shortcut) 'the Start menu shortcut exists'
$installed = Products
Check ($installed.Count -eq 1 -and $installed[0].Version -like "$Version*") "Windows Installer lists Dictum $Version once for this user"
Check ((& $dictum --version) -like "$Version*") "dictum --version prints $Version"
$selfTest = Join-Path $env:RUNNER_TEMP 'dictum-self-test'
New-Item -ItemType Directory -Force $selfTest | Out-Null
$env:DICTUM_CONFIG_DIR = Join-Path $selfTest 'config'; $env:DICTUM_DATA_DIR = Join-Path $selfTest 'data'; $env:DICTUM_SOCKET = 'dictum-ci-self-test'
$self = Start-Process (Join-Path $folder 'dictum-gui.exe') -ArgumentList '--self-test' -Wait -PassThru `
    -RedirectStandardOutput (Join-Path $logs 'self-test.log') -RedirectStandardError (Join-Path $logs 'self-test.err.log')
Remove-Item Env:\DICTUM_CONFIG_DIR, Env:\DICTUM_DATA_DIR, Env:\DICTUM_SOCKET
Check ($self.ExitCode -eq 0) 'the installed dictum-gui passes its self-test'

# A rebuilt MSI of the same version replaces the installed one.
Check ((Msiexec 'i' $RebuiltMsi 'rebuilt') -eq 0) 'a rebuilt MSI of the same version installs'
Check ((Products).Count -eq 1) 'Windows Installer still lists Dictum once'

# Upgrade over a running daemon: QuitDictum stops it, nothing is in use, no reboot.
# The daemon uses this user's default folders and socket, as the MSI's custom action does.
& $dictum init --yes --language en --stt whisper-tiny-en --llm none *> (Join-Path $logs 'init.log')
Check ($LASTEXITCODE -eq 0) "dictum init sets up this user's default folders (exit $LASTEXITCODE)"
$daemon = Start-Process $dictum -ArgumentList 'daemon' -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logs 'daemon.log') -RedirectStandardError (Join-Path $logs 'daemon.err.log')
$deadline = (Get-Date).AddSeconds($daemonStartSeconds)
while ((Get-Date) -lt $deadline -and -not ((& $dictum status 2>$null) -and $LASTEXITCODE -eq 0)) { Start-Sleep -Milliseconds $statusPollMilliseconds }
Check (-not $daemon.HasExited) 'the daemon runs from the install folder before the upgrade'
# Restart Manager off: under /qn it would close the daemon itself and hide a QuitDictum that failed.
$upgrade = Msiexec 'i' $UpgradeMsi 'upgrade' -NoRestartManager
Check ($upgrade -eq 0) "the upgrade over a running daemon exits 0 (got $upgrade; 3010 means a file was still in use)"
$quit = Get-QuitDictumResult (Get-Content (Join-Path $logs 'msiexec-upgrade.log'))
Check ($quit -eq 'ok') "QuitDictum ran and returned 0 during the upgrade (the log reads: $quit)"
Check ($daemon.HasExited -or $daemon.WaitForExit($daemonExitMilliseconds)) 'the upgrade stopped the daemon'
$upgraded = Products
Check ($upgraded.Count -eq 1 -and $upgraded[0].Version -like "$UpgradeVersion*") "Windows Installer lists the upgraded Dictum $UpgradeVersion once"

# Uninstall removes the program, the shortcut, its key and the login item; settings and models stay.
& $dictum daemon --install-autostart | Out-Null
Check ($null -ne (Get-ItemProperty $runKey -Name Dictum -ErrorAction SilentlyContinue)) 'the login item exists before the uninstall'
Check ((Msiexec 'x' $UpgradeMsi 'uninstall' -NoRestartManager) -eq 0) 'the uninstall exits 0'
Check (-not (Test-Path $folder)) "$folder is gone, runtimes and uk included"
Check (-not (Test-Path $shortcut)) 'the Start menu shortcut is gone'
Check (-not (Test-Path 'HKCU:\Software\Dictum')) 'HKCU\Software\Dictum is gone'
Check ($null -eq (Get-ItemProperty $runKey -Name Dictum -ErrorAction SilentlyContinue)) 'the Run\Dictum value is gone'
Check ((Products).Count -eq 0) 'Windows Installer no longer lists Dictum'
Check ((DictumProcesses).Count -eq 0) 'no Dictum process runs from the install folder'
Check (Test-Path (Join-Path $env:APPDATA 'Dictum\config.json')) 'the settings stay'

if ($failed) {
    # Dictum's own logs join $logs; the key MSI lines and the end of every other log go to the output.
    Copy-Item (Join-Path $env:LOCALAPPDATA 'Dictum\logs') (Join-Path $logs 'dictum') -Recurse -ErrorAction SilentlyContinue
    foreach ($log in Get-ChildItem $logs -Filter 'msiexec-*.log') {
        Write-Output "--- $($log.Name), key lines"
        Select-String -Path $log.FullName -Pattern 'QuitDictum|RemoveLoginItem|RESTART MANAGER|Return value 3|Error \d{4}' |
            ForEach-Object { $_.Line }
    }
    foreach ($log in Get-ChildItem $logs -Filter '*.log' | Where-Object Name -notlike 'msiexec-*') {
        Write-Output "--- $($log.Name), last lines"
        Get-Content $log.FullName -Tail 40
    }
    exit 1
}
exit 0
