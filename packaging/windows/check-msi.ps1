<#
  Checks a Dictum MSI without installing it (phase 4 PR 4b): the per-user scope, the version, the upgrade code, the
  Start menu shortcut, the custom actions and their conditions, and the files, from an administrative extract into a
  temp folder (nothing is registered). Exit 0 when every check passes.
#>
param(
    [Parameter(Mandatory)] [string] $Msi,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Stage
)
$ErrorActionPreference = 'Stop'
$failed = $false
function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Output "PASS: $what" } else { Write-Output "FAIL: $what"; $script:failed = $true }
}

if (-not (Test-Path $Msi)) { Write-Output "FAIL: no MSI at $Msi"; exit 1 }
$Msi = (Resolve-Path $Msi).Path
$Stage = (Resolve-Path $Stage).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
function Call($target, [string] $name, [object[]] $arguments) { $target.GetType().InvokeMember($name, 'InvokeMethod', $null, $target, $arguments) }
function Get($target, [string] $name, [object[]] $arguments) { $target.GetType().InvokeMember($name, 'GetProperty', $null, $target, $arguments) }
$db = Call $installer 'OpenDatabase' @($Msi, 0)
function Rows([string] $sql) {
    $view = Call $db 'OpenView' @($sql)
    Call $view 'Execute' $null | Out-Null
    $rows = @()
    while ($null -ne ($record = Call $view 'Fetch' $null)) {
        $count = Get $record 'FieldCount' $null
        # [int]: a pipeline variable reaches COM wrapped, which IDispatch rejects as a type mismatch.
        $rows += , @(1..$count | ForEach-Object { Get $record 'StringData' @([int]$_) })
    }
    Call $view 'Close' $null | Out-Null
    return , $rows
}
function Property([string] $name) { ((Rows "SELECT Value FROM Property WHERE Property = '$name'") | ForEach-Object { $_[0] }) }

Check ((Property 'ProductVersion') -eq $Version) "ProductVersion is $Version"
Check ((Property 'UpgradeCode') -eq '{7F2C9E44-3B1A-4C6D-9E0F-5A8B2D1C4E37}') 'the UpgradeCode is the fixed one'
Check ($null -eq (Property 'ALLUSERS')) 'no ALLUSERS: a per-user install'
$summary = Get $installer 'SummaryInformation' @($Msi, 0)
Check (((Get $summary 'Property' @(15)) -band 8) -ne 0) 'the summary says no elevation is needed'

$sequence = @{}
foreach ($row in (Rows 'SELECT Action, Condition, Sequence FROM InstallExecuteSequence')) { $sequence[$row[0]] = $row }
function Step([string] $action) { if ($sequence.ContainsKey($action)) { [int]$sequence[$action][2] } else { -1 } }
Check ($sequence.ContainsKey('QuitDictum') -and $sequence['QuitDictum'][1] -eq 'Installed OR WIX_UPGRADE_DETECTED' -and (Step 'QuitDictum') -lt (Step 'InstallValidate')) 'QuitDictum runs before InstallValidate on an upgrade or removal'
Check ($sequence.ContainsKey('RemoveLoginItem') -and $sequence['RemoveLoginItem'][1] -eq 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE' -and (Step 'RemoveLoginItem') -lt (Step 'RemoveFiles')) 'RemoveLoginItem runs before RemoveFiles on an uninstall, not on an upgrade'
Check ($sequence.ContainsKey('LaunchDictum') -and $sequence['LaunchDictum'][1] -eq 'NOT Installed AND UILevel > 2' -and (Step 'LaunchDictum') -gt (Step 'InstallFinalize')) 'LaunchDictum runs after an interactive install only'
$launch = (Rows "SELECT Target FROM CustomAction WHERE Action = 'LaunchDictum'") | ForEach-Object { $_[0] }
Check ($launch -eq '"[INSTALLFOLDER]dictum-gui.exe"') 'LaunchDictum starts dictum-gui.exe'
$quit = (Rows "SELECT Target FROM CustomAction WHERE Action = 'QuitDictum'") | ForEach-Object { $_[0] }
Check ($quit -eq '"[INSTALLFOLDER]dictum-gui.exe" --quit') 'QuitDictum runs dictum-gui.exe --quit'

$shortcut = Rows 'SELECT Directory_, Target FROM Shortcut'
Check ($shortcut.Count -eq 1 -and $shortcut[0][0] -eq 'ProgramMenuFolder' -and $shortcut[0][1] -eq '[INSTALLFOLDER]dictum-gui.exe') 'one Start menu shortcut to dictum-gui.exe'
Check ((Property 'ARPPRODUCTICON') -eq 'dictum.ico') 'Apps & features shows the Dictum icon'

$target = Join-Path ([IO.Path]::GetTempPath()) ('dictum-msi-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    $process = Start-Process msiexec.exe -ArgumentList "/a `"$Msi`" /qn TARGETDIR=`"$target`"" -Wait -PassThru
    Check ($process.ExitCode -eq 0) 'the administrative extract succeeds'
    $folder = Join-Path $target 'LocalApp\Programs\Dictum'
    foreach ($file in 'dictum.exe', 'dictum-gui.exe', 'runtimes\vulkan\win-x64\whisper.dll', 'runtimes\win-x64\whisper.dll', 'uk\dictum-gui.resources.dll') {
        Check (Test-Path (Join-Path $folder $file)) "the MSI installs $file under %LOCALAPPDATA%\Programs\Dictum"
    }
    Check (-not (Get-ChildItem -Recurse $folder -Include '*.pdb', '*.dbg')) 'no debug symbols in the MSI'

    # The MSI holds exactly the staged files, byte for byte: a stale incremental build or a missing file fails here
    # (phase 4 PR 4b final review C1, I3).
    function Hashes([string] $root) {
        $map = @{}
        Get-ChildItem -Recurse -File $root | ForEach-Object { $map[$_.FullName.Substring($root.Length).TrimStart([char]92)] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
        return $map
    }
    $want = Hashes $Stage
    $got = Hashes $folder
    $missing = @($want.Keys | Where-Object { -not $got.ContainsKey($_) })
    $extra = @($got.Keys | Where-Object { -not $want.ContainsKey($_) })
    $different = @($want.Keys | Where-Object { $got.ContainsKey($_) -and $got[$_] -ne $want[$_] })
    Check ($missing.Count -eq 0 -and $extra.Count -eq 0 -and $different.Count -eq 0) "the MSI holds exactly the $($want.Count) staged files, byte for byte (missing: $($missing -join ', '); extra: $($extra -join ', '); different: $($different -join ', '))"
}
finally {
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
}

if ($failed) { exit 1 }
exit 0
