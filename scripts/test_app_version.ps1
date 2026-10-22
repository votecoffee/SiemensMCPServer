param([string[]]$Binaries = @(), [string]$WorkerPackage = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'build/Version-Common.ps1')
$release = Get-Content -LiteralPath (Join-Path $repo 'plugin/app-version.json') -Raw | ConvertFrom-Json
foreach ($vector in @(
    @('26.0103.000507','26.103.507','26.103.5.7'),
    @('00.0101.000000','0.101.0','0.101.0.0'),
    @('26.1231.235959','26.1231.235959','26.1231.2359.59')
)) {
    if (-not (Test-AppVersionEqual $vector[0] $vector[1]) -or (ConvertTo-AppAssemblyVersion $vector[0]) -cne $vector[2]) { throw 'Version boundary/normalization mismatch.' }
}
$instant = if ($release.stamped_utc -is [DateTime]) { [DateTimeOffset]$release.stamped_utc } else { [DateTimeOffset]::Parse($release.stamped_utc, [Globalization.CultureInfo]::InvariantCulture) }
$display = [TimeZoneInfo]::ConvertTime($instant, [TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time')).ToString('yy.MMdd.HHmmss')
if ($display -cne $release.display_version) { throw 'Fixed UTC/New York stamp mismatch.' }
foreach ($binary in $Binaries) {
    $path = (Resolve-Path -LiteralPath $binary).Path
    $file = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString()
    if ($file.ProductVersion -cne $release.display_version -or $file.FileVersion -cne $release.file_version -or $assembly -cne $release.assembly_version) { throw 'Actual PE metadata differs from the fixed checkpoint.' }
}
if ($WorkerPackage) {
    $registry = Get-Content -LiteralPath (Join-Path $WorkerPackage 'active.json') -Raw | ConvertFrom-Json
    foreach ($property in $registry.PSObject.Properties) {
        $manifest = Get-Content -LiteralPath (Join-Path (Join-Path $WorkerPackage $property.Value) 'extension.json') -Raw | ConvertFrom-Json
        if (-not (Test-AppVersionEqual $manifest.version $release.package_version) -or $manifest.displayVersion -cne $release.display_version) { throw 'Worker package identities differ.' }
    }
}
Write-Output 'Fixed stamp, normalization, loader bounds and supplied actual PE/package identities passed without app startup.'
