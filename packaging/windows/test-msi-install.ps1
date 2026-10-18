<#
  CI only (a throwaway GitHub Actions Windows runner): installs the Dictum MSI for the runner's user, checks the result,
  reinstalls a rebuilt MSI of the same version, upgrades over a running daemon, and uninstalls, checking that nothing
  of the program is left and that user data stays (phase 4 PR 4b review recommendation 4). It refuses to run anywhere
  else: its uninstall removes the real HKCU Run value "Dictum".
#>
param(
    [Parameter(Mandatory)] [string] $Msi,
    [Parameter(Mandatory)] [string] $RebuiltMsi,
    [Parameter(Mandatory)] [string] $UpgradeMsi,
    [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') {
    Write-Output 'test-msi-install.ps1 runs only on a CI runner: it installs and uninstalls Dictum for the current user.'
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
# Every log the run writes; the job uploads this folder when a step fails (phase 4 PR 4d final review I2).
$logs = Join-Path $env:RUNNER_TEMP 'dictum-logs'
New-Item -ItemType Directory -Force $logs | Out-Null
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

# A rebuilt MSI of the same version replaces the installed one (4b review I1).
Check ((Msiexec 'i' $RebuiltMsi 'rebuilt') -eq 0) 'a rebuilt MSI of the same version installs'
Check ((Products).Count -eq 1) 'Windows Installer still lists Dictum once'

# Upgrade over a running daemon (4b review I2): QuitDictum stops it, nothing is in use, no reboot.
# The daemon uses the runner's default folders and socket, as the MSI's custom action does.
& $dictum init --yes --language en --stt whisper-tiny-en --llm none *> (Join-Path $logs 'init.log')
Check ($LASTEXITCODE -eq 0) "dictum init sets up the runner's default folders (exit $LASTEXITCODE)"
$daemon = Start-Process $dictum -ArgumentList 'daemon' -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $logs 'daemon.log') -RedirectStandardError (Join-Path $logs 'daemon.err.log')
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline -and -not ((& $dictum status 2>$null) -and $LASTEXITCODE -eq 0)) { Start-Sleep -Milliseconds 500 }
Check (-not $daemon.HasExited) 'the daemon runs from the install folder before the upgrade'
# Restart Manager off: under /qn it would close the daemon itself and hide a QuitDictum that failed (final review I1).
$upgrade = Msiexec 'i' $UpgradeMsi 'upgrade' -NoRestartManager
Check ($upgrade -eq 0) "the upgrade over a running daemon exits 0 (got $upgrade; 3010 means a file was still in use)"
$quit = Get-QuitDictumResult (Get-Content (Join-Path $logs 'msiexec-upgrade.log'))
Check ($quit -eq 'ok') "QuitDictum ran and returned 0 during the upgrade (the log reads: $quit)"
Check ($daemon.HasExited -or $daemon.WaitForExit(5000)) 'the upgrade stopped the daemon'
$upgraded = Products
Check ($upgraded.Count -eq 1 -and $upgraded[0].Version -like '99.0.0*') 'Windows Installer lists the upgraded Dictum once'

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
    # The program's own logs join the uploaded folder; the key MSI lines and the rest go to the job log.
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
