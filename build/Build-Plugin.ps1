param(
    [ValidatePattern('^V\d+(\.\d+)?$')][string]$TiaPortalVersion = 'V15.1',
    [string]$TiaPortalInstallVersion = '',
    [string]$TiaPublicApiDir = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePaths = @(& git -C $repoRoot -c core.quotepath=false ls-files --cached | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0 -or $sourcePaths.Count -eq 0) { throw 'Cannot read the tracked public source inventory.' }
$privatePaths = @($sourcePaths | Where-Object { $_ -match '(^|/)(\.local|\.vs|bin|obj|__pycache__|TestResults|artifacts)/' })
if ($privatePaths.Count) { throw ('Private or generated paths are tracked; remove them from the index before packaging: ' + ($privatePaths -join ', ')) }
$artifactRoot = Join-Path $repoRoot 'artifacts'
$versionSlug = $TiaPortalVersion.Replace('.', '_')
$pluginVersion = (Get-Content -LiteralPath (Join-Path $repoRoot 'plugin\plugin.json') -Raw | ConvertFrom-Json).version
$packageName = 'siemens-mcp-server-' + $pluginVersion + '-' + $versionSlug + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$packageRoot = Join-Path $artifactRoot $packageName
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
foreach ($sourcePath in $sourcePaths | Where-Object { $_.StartsWith('plugin/', [StringComparison]::Ordinal) }) {
    $destination = Join-Path $packageRoot $sourcePath.Substring(7)
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot $sourcePath) -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE')
$helperDirectory = Join-Path $packageRoot 'scripts'
New-Item -ItemType Directory -Path $helperDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'build\Install-CodexPlugin.ps1') -Destination $helperDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'build\Install-Extension.ps1') -Destination $helperDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\test_codex_integration.py') -Destination $helperDirectory
$buildArgs = @('publish', (Join-Path $repoRoot 'Mcp\SiemensMCPServer.Mcp.csproj'), '-c', 'Release', '--nologo', '-v', 'minimal',
    '-p:RestoreLockedMode=true', '-p:StageFeatureExtensions=false', ('-p:TiaPortalVersion=' + $TiaPortalVersion), '-o', (Join-Path $packageRoot 'server'))
if ($TiaPortalInstallVersion) { $buildArgs += '-p:TiaPortalInstallVersion=' + $TiaPortalInstallVersion }
if ($TiaPublicApiDir) { $buildArgs += '-p:TiaPublicApiDir=' + $TiaPublicApiDir }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'MCP publish failed; the incomplete package was retained for diagnostics.' }
& (Join-Path $repoRoot 'build\Build-Extensions.ps1') -Destination (Join-Path $packageRoot 'server\extensions')
if (Get-ChildItem -LiteralPath (Join-Path $packageRoot 'server') -Filter 'Siemens.Engineering*' -Recurse) {
    throw 'A Siemens engineering assembly was copied into the package. Packaging stopped.'
}
$assets = Get-Content -LiteralPath (Join-Path $repoRoot 'Mcp\obj\project.assets.json') -Raw | ConvertFrom-Json
$target = ($assets.targets.PSObject.Properties | Where-Object { $_.Name -eq '.NETFramework,Version=v4.8' }).Value
$dependencyNotices = @()
foreach ($entry in $target.PSObject.Properties) {
    if (-not ($entry.Value.runtime.PSObject.Properties | Where-Object { $_.Name.EndsWith('.dll') })) { continue }
    $package = $assets.libraries.PSObject.Properties[$entry.Name].Value
    if ($package.type -ne 'package') { continue }
    $cacheRoot = $assets.packageFolders.PSObject.Properties | Where-Object { Test-Path -LiteralPath (Join-Path $_.Name $package.path) } | Select-Object -First 1
    if (-not $cacheRoot) { throw ('NuGet package cache missing: ' + $entry.Name) }
    $cachePath = Join-Path $cacheRoot.Name $package.path
    $nuspecFile = Get-ChildItem -LiteralPath $cachePath -Filter '*.nuspec' -File | Select-Object -First 1
    [xml]$nuspec = Get-Content -LiteralPath $nuspecFile.FullName -Raw
    $dependencyNotices += [ordered]@{ package = $entry.Name; license = [string]$nuspec.package.metadata.license.'#text' }
    Get-ChildItem -LiteralPath $cachePath -File | Where-Object { $_.Name -match '^THIRD.PARTY.NOTICES' } | ForEach-Object {
        $noticeName = $entry.Name.Replace('/', '-') + '-' + $_.Name
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $packageRoot ('licenses\' + $noticeName))
    }
}
ConvertTo-Json -InputObject $dependencyNotices -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot 'licenses\dependencies.json') -Encoding UTF8
$metadata = [ordered]@{
    pluginVersion = $pluginVersion; tiaPortalApi = $TiaPortalVersion; tiaPortalInstallVersion = $TiaPortalInstallVersion;
    sourceCommit = (& git -C $repoRoot rev-parse HEAD); sourceHasLocalChanges = [bool](& git -C $repoRoot status --porcelain);
    nativeWriteAcceptance = 'Selected Comfort V15.1 workflows tested; see NATIVE-ACCEPTANCE.md. Other profiles/workflows pending.'; builtUtc = [DateTime]::UtcNow.ToString('o')
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'build-info.json') -Encoding UTF8
$sourceFiles = @($sourcePaths | ForEach-Object {
    $sourcePath = Join-Path $repoRoot $_
    if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
})
ConvertTo-Json -InputObject $sourceFiles -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot 'source-manifest.json') -Encoding UTF8
$files = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/');
        bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
ConvertTo-Json -InputObject $files -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$zipPath = Join-Path $artifactRoot ($packageName + '.zip')
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force) {
        $entryName = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
($zipHash + '  ' + [IO.Path]::GetFileName($zipPath)) | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ASCII
Write-Output ('Plugin ZIP: ' + $zipPath)
Write-Output ('SHA256: ' + $zipHash)
