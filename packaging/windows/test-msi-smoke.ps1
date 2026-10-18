<#
  Checks the helpers test-msi-install.ps1 uses (msi-smoke.ps1) on any Windows machine, without installing anything
  (phase 4 PR 4d final review C1, I1).
#>
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'msi-smoke.ps1')

$failed = $false
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Output "PASS: $what" } else { Write-Output "FAIL: $what"; $script:failed = $true }
}

# C1: the current user's per-user products, checked against where Windows Installer records them in the registry.
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$recorded = @(Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products\*\InstallProperties" -ErrorAction SilentlyContinue |
    Where-Object DisplayName)
if ($recorded.Count -gt 0) {
    $known = $recorded[0]
    $found = @(Get-InstalledProducts $known.DisplayName)
    $expected = @($recorded | Where-Object DisplayName -eq $known.DisplayName).Count
    Check ($found.Count -eq $expected) "a per-user product of this user is found by name ($($known.DisplayName), $expected)"
    Check ($found.Count -gt 0 -and $known.DisplayVersion.StartsWith($found[0].Version)) "its version is read ($($known.DisplayVersion))"
} else {
    Write-Output 'SKIP: this user has no per-user MSI products to look up'
}
Check (@(Get-InstalledProducts 'No such product, Dictum smoke').Count -eq 0) 'a name no product has finds none and does not throw'

# I1: Restart Manager off only where asked, so a daemon QuitDictum missed shows up as exit 3010.
$plain = Get-MsiexecArguments 'i' 'C:\a b\dictum.msi' 'C:\a b\dictum.msi.i.log'
$off = Get-MsiexecArguments 'i' 'C:\a b\dictum.msi' 'C:\a b\dictum.msi.i.log' -NoRestartManager
Check ($plain -eq '/i "C:\a b\dictum.msi" /qn /norestart /l*v "C:\a b\dictum.msi.i.log"') "msiexec arguments: $plain"
Check ($off -eq "$plain MSIRESTARTMANAGERCONTROL=Disable") "msiexec arguments without Restart Manager: $off"

# I1: what the verbose log says about QuitDictum, whose failures Return="ignore" keeps out of the exit code.
$ran = 'Action start 12:00:01: QuitDictum.', 'Action ended 12:00:03: QuitDictum. Return value 1.'
Check ((Get-QuitDictumResult $ran) -eq 'ok') 'a QuitDictum that ran and returned 0 reads ok'
$error1 = $ran + 'CustomAction QuitDictum returned actual error code 1 (note this may not be 100% accurate if translation happened inside sandbox)'
Check ((Get-QuitDictumResult $error1) -eq 'failed') 'a QuitDictum that returned 1 reads failed'
Check ((Get-QuitDictumResult @('Action start 12:00:01: InstallValidate.')) -eq 'missing') 'a log without QuitDictum reads missing'

if ($failed) { exit 1 }
exit 0
