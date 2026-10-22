param(
    [ValidateSet('V15.1','V16','V17','V18','V19','V20','V21')][string]$TiaPortalVersion = 'V15.1',
    [ValidatePattern('^$|^V\d+(\.\d+)?$')][string]$TiaPortalInstallVersion = '',
    [string]$TiaPublicApiDir = '',
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'SiemensMCPServer\Desktop'),
    [string]$ProgramsDirectory = [Environment]::GetFolderPath('Programs'),
    [switch]$NoShortcut,
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Desktop-Common.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = [IO.Path]::GetFullPath($InstallRoot)
Get-Command dotnet -CommandType Application -ErrorAction Stop | Out-Null
if (-not $TiaPortalInstallVersion) { $TiaPortalInstallVersion = $TiaPortalVersion }
if (-not $TiaPublicApiDir) {
    $programFilesRoot = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $automation = Join-Path $programFilesRoot 'Siemens\Automation'
    $suffix = if ($TiaPortalVersion -eq 'V21') { 'PublicAPI\V21\net48' } else { 'PublicAPI\' + $TiaPortalVersion }
    $TiaPublicApiDir = Join-Path (Join-Path $automation ('Portal ' + $TiaPortalInstallVersion.Replace('.', '_'))) $suffix
}
$baseAssembly = if ($TiaPortalVersion -eq 'V21') { 'Siemens.Engineering.Base.dll' } else { 'Siemens.Engineering.dll' }
if (-not (Test-Path -LiteralPath (Join-Path $TiaPublicApiDir $baseAssembly) -PathType Leaf)) {
    throw "TIA Openness API $TiaPortalVersion was not found at $TiaPublicApiDir. Select -TiaPortalInstallVersion or -TiaPublicApiDir explicitly."
}
if (-not $NoShortcut -and [string]::IsNullOrWhiteSpace($ProgramsDirectory)) { throw 'The current user Start menu folder could not be located. Use -NoShortcut or specify -ProgramsDirectory.' }
if (-not $NoShortcut) { $ProgramsDirectory = [IO.Path]::GetFullPath($ProgramsDirectory) }
$release = Get-Content -LiteralPath (Join-Path $repo 'plugin/app-version.json') -Raw | ConvertFrom-Json
$slug = $release.package_version + '-' + $TiaPortalVersion.Replace('.', '_') + '-' + [Guid]::NewGuid().ToString('N')
$relativeDirectory = 'apps/' + $slug
$destination = Join-Path $root $relativeDirectory
[IO.Directory]::CreateDirectory($destination) | Out-Null
$buildArguments = @('publish', (Join-Path $repo 'SiemensMCPServer.csproj'), '-c', 'Release', '--nologo', '-v', 'minimal', '-p:RestoreLockedMode=true',
    ('-p:TiaPortalVersion=' + $TiaPortalVersion), ('-p:TiaPortalInstallVersion=' + $TiaPortalInstallVersion), ('-p:TiaPublicApiDir=' + [IO.Path]::GetFullPath($TiaPublicApiDir)),
    ('-p:OutputPath=bin/Release/' + $TiaPortalVersion + '/'), ('-p:IntermediateOutputPath=obj/' + $TiaPortalVersion + '/Release/'), '-o', $destination)
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw "Desktop build failed. Previous installation and shortcuts are unchanged; incomplete build retained at $destination." }
$executable = Join-Path $destination 'SiemensMCPServer.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or -not (Test-Path -LiteralPath ($executable + '.config') -PathType Leaf)) { throw 'Desktop publish is incomplete. Previous installation remains selected.' }
if (Get-ChildItem -LiteralPath $destination -Filter 'Siemens.Engineering*' -File -Recurse) { throw 'Desktop publish unexpectedly contains Siemens engineering assemblies. Installation stopped.' }
$installedInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
if ($installedInfo.ProductVersion -cne $release.display_version -or $installedInfo.FileVersion -cne $release.file_version -or [Reflection.AssemblyName]::GetAssemblyName($executable).Version.ToString() -cne $release.assembly_version) { throw 'Desktop binary does not match the fixed release stamp. Previous selection was preserved.' }
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $destination 'LICENSE')
$shortcutPath = $null
if (-not $NoShortcut) {
    $shortcutDirectory = Join-Path $ProgramsDirectory 'SiemensMCPServer'
    [IO.Directory]::CreateDirectory($shortcutDirectory) | Out-Null
    $shortcutPath = Join-Path $shortcutDirectory ('SiemensMCPServer (' + $TiaPortalVersion + ').lnk')
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        try {
            $shortcut.TargetPath = $executable
            $shortcut.Arguments = '--tia-version=' + $TiaPortalInstallVersion
            $shortcut.WorkingDirectory = $destination
            $shortcut.IconLocation = $executable + ',0'
            $shortcut.Description = 'Comfort / Unified HMI and offline PLC import and export'
            $shortcut.Save()
        } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
}
$receipt = [ordered]@{ format = 'siemens-desktop-install-v1'; installedUtc = [DateTime]::UtcNow.ToString('O'); api = $TiaPortalVersion;
    engineeringVersion = $TiaPortalInstallVersion; directory = $relativeDirectory; executableSha256 = Get-DesktopFileHash $executable;
    version = $release.display_version; packageVersion = $release.package_version;
    assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($executable).Version.ToString();
    fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion;
    startMenuShortcut = $shortcutPath; codexSettingsChanged = $false; nativeProjectChanged = $false }
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $destination 'installation.json'), ($receipt | ConvertTo-Json -Depth 5), $utf8)
$activePath = Join-Path $root 'active.json'; $temporaryPath = Join-Path $root ('active-' + [Guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllText($temporaryPath, ($receipt | ConvertTo-Json -Depth 5), $utf8)
if (Test-Path -LiteralPath $activePath) {
    $previousPath = Join-Path $root ('active-previous-' + [Guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::Replace($temporaryPath, $activePath, $previousPath)
} else { [IO.File]::Move($temporaryPath, $activePath) }
Write-Host "Desktop installed: $executable"
if ($shortcutPath) { Write-Host "Start menu shortcut: $shortcutPath" }
if ($Run) { & (Join-Path $PSScriptRoot 'Start-Desktop.ps1') -InstallRoot $root }
