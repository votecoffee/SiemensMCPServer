[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Install','Run')][string]$Command,
    [Alias('h')][switch]$Help,
    [switch]$NoPause,
    [switch]$InstallOnly,
    [string]$InstallRoot='',
    [string]$TiaPortalVersion='V15.1',
    [string]$TiaPortalInstallVersion='',
    [string]$TiaPublicApiDir='',
    [string]$ProgramsDirectory='',
    [switch]$NoShortcut,
    [switch]$CheckOnly,
    [switch]$Safeguards,
    [string]$SettingsFile=''
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
if($Help) {
    @'
SiemensMCPServer desktop commands (GUI only; no Codex configuration changes)
  InstallAndRun.cmd [-InstallOnly] [-TiaPortalVersion V15.1] [-NoShortcut]
                    [-TiaPortalInstallVersion V19] [-TiaPublicApiDir "C:\API path"]
                    [-InstallRoot "C:\app path"] [-ProgramsDirectory "C:\shortcuts"]
  Run.cmd [-InstallRoot "C:\app path"] [-CheckOnly] [-Safeguards]
          [-SettingsFile "C:\policy path\safeguards.json"]
  Both: -Help (-h), -NoPause (unattended failure handling)
Install builds/installs then opens the GUI unless -InstallOnly is supplied.
Run -CheckOnly verifies the selected installation without opening it.
'@ | Write-Output
    exit 0
}
try {
    $parameters=@{}
    if($InstallRoot){$parameters.InstallRoot=$InstallRoot}
    if($Command -eq 'Install') {
        if($CheckOnly -or $Safeguards -or $SettingsFile){throw 'CheckOnly, Safeguards and SettingsFile are Run.cmd options.'}
        $parameters.TiaPortalVersion=$TiaPortalVersion
        if($TiaPortalInstallVersion){$parameters.TiaPortalInstallVersion=$TiaPortalInstallVersion}
        if($TiaPublicApiDir){$parameters.TiaPublicApiDir=$TiaPublicApiDir}
        if($ProgramsDirectory){$parameters.ProgramsDirectory=$ProgramsDirectory}
        $parameters.NoShortcut=[bool]$NoShortcut
        $parameters.Run=-not $InstallOnly
        & (Join-Path $PSScriptRoot 'Install-Desktop.ps1') @parameters
    } else {
        $installOptions=@('InstallOnly','TiaPortalVersion','TiaPortalInstallVersion','TiaPublicApiDir','ProgramsDirectory','NoShortcut')
        if(@($installOptions | Where-Object {$PSBoundParameters.ContainsKey($_)}).Count){throw 'Install options belong to InstallAndRun.cmd.'}
        $parameters.CheckOnly=[bool]$CheckOnly
        $parameters.Safeguards=[bool]$Safeguards
        if($SettingsFile){$parameters.SettingsFile=$SettingsFile}
        & (Join-Path $PSScriptRoot 'Start-Desktop.ps1') @parameters
    }
    if($LASTEXITCODE){exit ([int]$LASTEXITCODE)}
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if(-not $NoPause -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
        Read-Host 'Press Enter to close' | Out-Null
    }
    exit 1
}
