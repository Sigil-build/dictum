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

$failed = $false
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Output "PASS: $what" } else { Write-Output "FAIL: $what"; $script:failed = $true }
}
$folder = Join-Path $env:LOCALAPPDATA 'Programs\Dictum'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Dictum.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Msiexec([string] $verb, [string] $path) {
    $log = "$path.$verb.log"
    $process = Start-Process msiexec.exe -ArgumentList "/$verb `"$path`" /qn /norestart /l*v `"$log`"" -Wait -PassThru
    return $process.ExitCode
}
function Entries {
    @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' |
        Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -eq 'Dictum' })
}
function DictumProcesses { @(Get-Process dictum, dictum-gui -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$folder\*" }) }

# Install.
Check ((Msiexec 'i' $Msi) -eq 0) 'the MSI installs for this user (exit 0, no reboot)'
foreach ($file in 'dictum.exe', 'dictum-gui.exe', 'runtimes\vulkan\win-x64\whisper.dll', 'uk\dictum-gui.resources.dll') {
    Check (Test-Path (Join-Path $folder $file)) "$file is installed in $folder"
}
Check (Test-Path $shortcut) 'the Start menu shortcut exists'
Check ((Entries).Count -eq 1) 'Apps & features lists Dictum once'
Check ((& (Join-Path $folder 'dictum.exe') --version) -like "$Version*") "dictum --version prints $Version"
$selfTest = Join-Path $env:RUNNER_TEMP 'dictum-self-test'
New-Item -ItemType Directory -Force $selfTest | Out-Null
$env:DICTUM_CONFIG_DIR = Join-Path $selfTest 'config'; $env:DICTUM_DATA_DIR = Join-Path $selfTest 'data'; $env:DICTUM_SOCKET = 'dictum-ci-self-test'
$self = Start-Process (Join-Path $folder 'dictum-gui.exe') -ArgumentList '--self-test' -Wait -PassThru
Remove-Item Env:\DICTUM_CONFIG_DIR, Env:\DICTUM_DATA_DIR, Env:\DICTUM_SOCKET
Check ($self.ExitCode -eq 0) 'the installed dictum-gui passes its self-test'

# A rebuilt MSI of the same version replaces the installed one (4b review I1).
Check ((Msiexec 'i' $RebuiltMsi) -eq 0) 'a rebuilt MSI of the same version installs'
Check ((Entries).Count -eq 1) 'Apps & features still lists Dictum once'

# Upgrade over a running daemon (4b review I2): QuitDictum stops it, nothing is in use, no reboot.
# The daemon uses the runner's default folders and socket, as the MSI's custom action does.
& (Join-Path $folder 'dictum.exe') init --yes --language en --stt whisper-tiny-en --llm none | Out-Null
$daemon = Start-Process (Join-Path $folder 'dictum.exe') -ArgumentList 'daemon' -PassThru -WindowStyle Hidden
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline -and -not ((& (Join-Path $folder 'dictum.exe') status 2>$null) -and $LASTEXITCODE -eq 0)) { Start-Sleep -Milliseconds 500 }
Check (-not $daemon.HasExited) 'the daemon runs from the install folder before the upgrade'
$upgrade = Msiexec 'i' $UpgradeMsi
Check ($upgrade -eq 0) "the upgrade over a running daemon exits 0 (got $upgrade; 3010 means a reboot was needed)"
Check ($daemon.HasExited -or $daemon.WaitForExit(5000)) 'the upgrade stopped the daemon'
Check ((Entries).Count -eq 1 -and (Get-ItemProperty (Entries)[0].PSPath).DisplayVersion -eq '99.0.0') 'Apps & features lists the upgraded Dictum once'

# Uninstall removes the program, the shortcut, its key and the login item; settings and models stay.
& (Join-Path $folder 'dictum.exe') daemon --install-autostart | Out-Null
Check ($null -ne (Get-ItemProperty $runKey -Name Dictum -ErrorAction SilentlyContinue)) 'the login item exists before the uninstall'
Check ((Msiexec 'x' $UpgradeMsi) -eq 0) 'the uninstall exits 0'
Check (-not (Test-Path $folder)) "$folder is gone, runtimes and uk included"
Check (-not (Test-Path $shortcut)) 'the Start menu shortcut is gone'
Check (-not (Test-Path 'HKCU:\Software\Dictum')) 'HKCU\Software\Dictum is gone'
Check ($null -eq (Get-ItemProperty $runKey -Name Dictum -ErrorAction SilentlyContinue)) 'the Run\Dictum value is gone'
Check ((Entries).Count -eq 0) 'Apps & features no longer lists Dictum'
Check ((DictumProcesses).Count -eq 0) 'no Dictum process runs from the install folder'
Check (Test-Path (Join-Path $env:APPDATA 'Dictum\config.json')) 'the settings stay'

if ($failed) { exit 1 }
exit 0
