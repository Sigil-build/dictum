# Helpers for test-msi-install.ps1, apart so test-msi-smoke.ps1 can check them on any machine
# (phase 4 PR 4d final review C1, I1).

# The comma keeps PowerShell from unrolling a returned COM collection into an array.
function Get-ComProperty($object, [string] $name, [object[]] $arguments = $null) {
    , $object.GetType().InvokeMember($name, 'GetProperty', $null, $object, $arguments)
}

# The current user's per-user installs of the product with this name, as Windows Installer lists them. Their Apps &
# features keys sit under HKLM\...\Uninstall and their data under Installer\UserData\<SID>, never under HKCU, so the
# installer's own API is the reliable count.
function Get-InstalledProducts([string] $Name) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $products = Get-ComProperty $installer 'ProductsEx' @('', $sid, 2)   # 2: msiInstallContextUserUnmanaged
    $count = Get-ComProperty $products 'Count'
    for ($i = 0; $i -lt $count; $i++) {
        $product = Get-ComProperty $products 'Item' @($i)
        if ((Get-ComProperty $product 'InstallProperty' @('ProductName')) -eq $Name) {
            [pscustomobject]@{
                Name = $Name
                Version = Get-ComProperty $product 'InstallProperty' @('VersionString')
                ProductCode = Get-ComProperty $product 'ProductCode'
            }
        }
    }
}

# msiexec's arguments for a quiet run with a verbose log. -NoRestartManager stops Restart Manager from closing a
# running daemon itself under /qn, so a daemon QuitDictum did not stop shows up as exit 3010 (files in use).
function Get-MsiexecArguments([string] $Verb, [string] $Path, [string] $Log, [switch] $NoRestartManager) {
    $arguments = "/$Verb `"$Path`" /qn /norestart /l*v `"$Log`""
    if ($NoRestartManager) { $arguments += ' MSIRESTARTMANAGERCONTROL=Disable' }
    $arguments
}

# What a verbose MSI log says about QuitDictum: 'ok', 'failed' (it ran and returned an error, which Return="ignore"
# keeps out of msiexec's exit code) or 'missing' (it never ran).
function Get-QuitDictumResult([string[]] $Log) {
    if ($Log -match 'CustomAction QuitDictum returned actual error code') { return 'failed' }
    if ($Log -match 'Action start [0-9:]+: QuitDictum\.') { return 'ok' }
    'missing'
}
