param(
    [ValidateSet('V15.1','V16','V17','V18','V19')][string]$Version = 'V15.1',
    [string]$TiaPortalInstallVersion = $Version,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Exercise real publishing and .lnk creation in isolated folders, never the real Start menu.
$scratch = Join-Path $repo ('.local\desktop-installer-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $scratch ('App path with spaces & ' + [char]0x00DC + 'nicode')
$programs = Join-Path $scratch ('Start menu ' + [char]0x00DC)
$installer = Join-Path $repo 'build\Install-Desktop.ps1'
$launcher = Join-Path $repo 'build\Start-Desktop.ps1'
$checks = [Collections.Generic.List[string]]::new()
function Assert-Install([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Desktop installer check failed: $Description" }
    $checks.Add($Description)
}
function Install-Test([string]$Root, [switch]$NoShortcut) {
    $arguments = @('-NoProfile', '-File', $installer, '-TiaPortalVersion', $Version, '-TiaPortalInstallVersion', $TiaPortalInstallVersion, '-InstallRoot', $Root, '-ProgramsDirectory', $programs)
    if ($NoShortcut) { $arguments += '-NoShortcut' }
    & powershell.exe @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Test desktop installation failed.' }
    return Get-Content -LiteralPath (Join-Path $Root 'active.json') -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Shortcut([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($Path)
        try { return @{ target = $link.TargetPath; arguments = $link.Arguments; workingDirectory = $link.WorkingDirectory; icon = $link.IconLocation } }
        finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
}
function Rejected-Script([string]$Path, [string[]]$Arguments) {
    # Windows PowerShell represents redirected native stderr as ErrorRecords.
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe -NoProfile -File $Path @Arguments 2>&1
    return @{ code = $LASTEXITCODE; text = ($output -join "`n") }
}
$first = Install-Test $install
$firstExe = Join-Path (Join-Path $install $first.directory) 'SiemensMCPServer.exe'
$firstHash = (Get-FileHash -LiteralPath $firstExe -Algorithm SHA256).Hash
Assert-Install ((Test-Path -LiteralPath ($firstExe + '.config')) -and (Test-Path -LiteralPath (Join-Path (Split-Path $firstExe) 'YamlDotNet.dll'))) 'GUI and runtime/config dependencies are published together'
Assert-Install (-not (Get-ChildItem -LiteralPath (Split-Path $firstExe) -File -Recurse -Filter 'Siemens.Engineering*')) 'Siemens engineering assemblies are not redistributed'
$link = Shortcut $first.startMenuShortcut
Assert-Install ($link.target -ceq $firstExe -and $link.arguments -ceq ('--tia-version=' + $TiaPortalInstallVersion) -and $link.workingDirectory -ceq (Split-Path $firstExe)) 'Start menu shortcut selects the installed GUI, working directory and engineering version'
Assert-Install ($link.icon -ceq ($firstExe + ',0')) 'Shortcut uses the embedded application icon'
Push-Location $scratch
try {
    $ready = & (Join-Path $repo 'Run.cmd') -InstallRoot $install -CheckOnly | ConvertFrom-Json
    Assert-Install ($LASTEXITCODE -eq 0 -and $ready.state -eq 'ready' -and $ready.executable -ceq $firstExe) 'Root Run.cmd works from another directory with spaces and ampersands in the installation path'
} finally { Pop-Location }
$policyPath = Join-Path $scratch 'Policy path with spaces/safeguards.json'
$settingsReady = & $launcher -InstallRoot $install -Safeguards -SettingsFile $policyPath -CheckOnly | ConvertFrom-Json
Assert-Install ($settingsReady.arguments -contains '--safeguards' -and $settingsReady.arguments -contains ('"--settings-file=' + $policyPath + '"')) 'Settings-only launcher preserves an isolated settings path with spaces'
Assert-Install (-not (Test-Path -LiteralPath $policyPath)) 'Checking the settings launch does not create or modify a policy'
$activeReceipt = Join-Path $install 'active.json'; $activeBytes = [IO.File]::ReadAllBytes($activeReceipt)
try {
    $legacy = $first | ConvertTo-Json | ConvertFrom-Json; $legacy.version = '1.2.2.0'
    [IO.File]::WriteAllText($activeReceipt, ($legacy | ConvertTo-Json))
    $failure = Rejected-Script $launcher @('-InstallRoot', $install, '-Safeguards', '-CheckOnly')
    Assert-Install ($failure.code -ne 0 -and $failure.text.Contains('1.3.0 or newer')) 'Settings launcher rejects older desktops rather than opening a different workflow'
} finally { [IO.File]::WriteAllBytes($activeReceipt, $activeBytes) }
$second = Install-Test $install
$secondExe = Join-Path (Join-Path $install $second.directory) 'SiemensMCPServer.exe'
Assert-Install ($first.directory -cne $second.directory -and (Get-FileHash -LiteralPath $firstExe -Algorithm SHA256).Hash -ceq $firstHash) 'Updating uses a fresh directory and preserves the previous executable'
Assert-Install ((Shortcut $second.startMenuShortcut).target -ceq $secondExe) 'Update repoints the same profile shortcut to the new executable'
$receipt = Join-Path $install 'active.json'; $before = [IO.File]::ReadAllBytes($receipt)
$shortcutHash = (Get-FileHash -LiteralPath $second.startMenuShortcut -Algorithm SHA256).Hash
$failure = Rejected-Script $installer @('-TiaPublicApiDir', (Join-Path $scratch 'missing-api'), '-InstallRoot', $install, '-ProgramsDirectory', $programs)
Assert-Install ($failure.code -ne 0 -and $failure.text.Contains('was not found')) 'Missing SDK fails before replacing the selected installation'
Assert-Install ([Convert]::ToBase64String([IO.File]::ReadAllBytes($receipt)) -ceq [Convert]::ToBase64String($before) -and (Get-FileHash -LiteralPath $second.startMenuShortcut -Algorithm SHA256).Hash -ceq $shortcutHash) 'Failed installation leaves the active receipt and shortcut unchanged'
$bad = $second | ConvertTo-Json | ConvertFrom-Json; $bad.directory = 'apps/../../elsewhere'
[IO.File]::WriteAllText($receipt, ($bad | ConvertTo-Json))
$failure = Rejected-Script $launcher @('-InstallRoot', $install, '-CheckOnly')
Assert-Install ($failure.code -ne 0 -and $failure.text.Contains('Invalid desktop installation receipt')) 'Launcher rejects a receipt pointing outside its application directory'
[IO.File]::WriteAllBytes($receipt, $before)
$bytes = [IO.File]::ReadAllBytes($secondExe); [IO.File]::WriteAllBytes($secondExe, [byte[]]@(0, 1, 2))
$failure = Rejected-Script $launcher @('-InstallRoot', $install, '-CheckOnly')
Assert-Install ($failure.code -ne 0 -and $failure.text.Contains('missing or changed')) 'Launcher rejects a changed executable'
[IO.File]::WriteAllBytes($secondExe, $bytes)
$withoutShortcut = Install-Test (Join-Path $scratch 'No shortcut') -NoShortcut
Assert-Install ($null -eq $withoutShortcut.startMenuShortcut -and -not $withoutShortcut.codexSettingsChanged) 'Shortcut creation is optional and installation leaves Codex settings alone'
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Icon]::ExtractAssociatedIcon($secondExe)
try { Assert-Install ($null -ne $icon -and $icon.Width -gt 0) 'Published executable exposes an extractable application icon' } finally { $icon.Dispose() }
$report = [ordered]@{ api = $Version; checks = $checks.ToArray(); count = $checks.Count; artifacts = $scratch; realStartMenuChanged = $false; guiLaunched = $false; projectAttached = $false; nativeWrites = $false }
if ($ReportPath) {
    $output = [IO.Path]::GetFullPath($ReportPath); [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    [IO.File]::WriteAllText($output, ($report | ConvertTo-Json -Depth 5))
}
$report | ConvertTo-Json -Depth 5
