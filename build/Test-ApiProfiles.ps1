param(
    [ValidateSet('V15.1', 'V16', 'V17', 'V18', 'V19', 'V20', 'V21')]
    [string[]]$Versions = @('V15.1', 'V16', 'V17', 'V18', 'V19', 'V20', 'V21'),
    [string]$AutomationRoot = (Join-Path ${env:ProgramFiles} 'Siemens\Automation'),
    [string]$ReportPath,
    [switch]$SkipDesktop,
    [switch]$SkipProtocol
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $ReportPath) { $ReportPath = Join-Path $repo ("artifacts\api-profiles-" + [guid]::NewGuid().ToString('N') + '.json') }
$reportFile = [IO.Path]::GetFullPath($ReportPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportFile)) | Out-Null
$records = [Collections.Generic.List[object]]::new()
$script:commandNumber = 0
$script:logDirectory = Join-Path ([IO.Path]::GetDirectoryName($reportFile)) ([IO.Path]::GetFileNameWithoutExtension($reportFile) + '-logs')
[IO.Directory]::CreateDirectory($script:logDirectory) | Out-Null
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    $script:commandNumber++
    $log = Join-Path $script:logDirectory ($script:commandNumber.ToString('D3') + '.log')
    Write-Host ("  " + $Command + ' ' + ($Arguments -join ' '))
    & $Command @Arguments > $log 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE; inspect $log" }
}
Push-Location $repo
try {
    # Compile and metadata/protocol checks only. No attachment, compilation of a
    # Siemens project, import, save, download or online controller connection.
    $installations = @(Get-ChildItem -LiteralPath $AutomationRoot -Directory -Filter 'Portal V*' |
        Sort-Object { [version]($_.Name.Substring(8).Replace('_', '.') + '.0') } -Descending)
    foreach ($version in $Versions) {
        $suffix = if ($version -eq 'V21') { "PublicAPI\$version\net48" } else { "PublicAPI\$version" }
        $baseFile = if ($version -eq 'V21') { 'Siemens.Engineering.Base.dll' } else { 'Siemens.Engineering.dll' }
        $candidates = @($installations | Where-Object { Test-Path -LiteralPath (Join-Path (Join-Path $_.FullName $suffix) $baseFile) })
        $preferred = @($candidates | Where-Object { $_.Name -eq ('Portal ' + $version.Replace('.', '_')) })
        if ($preferred.Count) { $candidates = $preferred + @($candidates | Where-Object { $_ -ne $preferred[0] }) }
        if (-not $candidates.Count) {
            $records.Add([ordered]@{ api = $version; status = 'sdk-missing'; nativeProjectAcceptance = 'pending' })
            Write-Host "$version : SDK missing; validation skipped"
            continue
        }
        $apiDirectory = Join-Path $candidates[0].FullName $suffix
        $record = [ordered]@{ api = $version; apiDirectory = $apiDirectory; status = 'failed'; desktop = 'skipped'; protocol = 'skipped'; nativeProjectAcceptance = 'pending' }
        try {
            Write-Host "$version : $apiDirectory"
            $common = @('-c', 'Release', '-p:RestoreLockedMode=true', "-p:TiaPortalVersion=$version", "-p:TiaPublicApiDir=$apiDirectory")
            Invoke-Checked 'dotnet' (@('build', 'Mcp/SiemensMCPServer.Mcp.csproj') + $common)
            $serverDirectory = Join-Path $repo "Mcp\bin\Release\$version\net48"
            Invoke-Checked 'powershell.exe' @('-NoProfile', '-File', 'scripts/test_native_contracts.ps1', '-Version', $version, '-ServerDirectory', $serverDirectory, '-TiaPublicApiDir', $apiDirectory)
            if (-not $SkipProtocol) {
                Invoke-Checked 'py' @('-3', '-B', '-X', 'utf8', 'scripts/test_mcp_protocol.py', (Join-Path $serverDirectory 'SiemensMCPServer.Mcp.exe'), '--native-read-only')
                $record.protocol = 'passed'
            }
            if (-not $SkipDesktop) {
                $desktopDirectory = "bin/Release/$version/"
                Invoke-Checked 'dotnet' (@('build', 'SiemensMCPServer.csproj') + $common + @("-p:OutputPath=$desktopDirectory", "-p:IntermediateOutputPath=obj/$version/Release/"))
                $engineeringVersion = $candidates[0].Name.Substring(7).Replace('_', '.')
                Invoke-Checked 'powershell.exe' @('-NoProfile', '-STA', '-File', 'scripts/test_desktop_smoke.ps1', '-DesktopDirectory', $desktopDirectory, '-Version', $version, '-TiaPortalInstallVersion', $engineeringVersion)
                Invoke-Checked 'powershell.exe' @('-NoProfile', '-File', 'scripts/test_native_contracts.ps1', '-Version', $version, '-ServerDirectory', $desktopDirectory, '-ExecutableName', 'SiemensMCPServer.exe', '-TiaPublicApiDir', $apiDirectory)
                $record.desktop = 'passed'
            }
            $record.status = 'passed'
        } catch { $record['error'] = $_.Exception.Message; Write-Warning "$version : $($_.Exception.Message)" }
        $records.Add($record)
    }
} finally {
    Pop-Location
    [ordered]@{ createdUtc = [DateTime]::UtcNow.ToString('O'); profiles = $records.ToArray(); logs = $script:logDirectory; projectAttached = $false; nativeWrites = $false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportFile -Encoding UTF8
    Write-Host "Profile report: $reportFile"
}
if (@($records | Where-Object { $_.status -eq 'failed' }).Count) { exit 1 }
