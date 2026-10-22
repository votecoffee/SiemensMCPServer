param([string]$OutputRoot = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Version-Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Get-OfflineHash([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create(); $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$release = Get-Content -LiteralPath (Join-Path $repoRoot 'plugin/app-version.json') -Raw | ConvertFrom-Json
$version = $release.package_version
$sourcePaths = @(& git -C $repoRoot -c core.quotepath=false ls-files --cached | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0 -or -not $sourcePaths.Count) { throw 'Cannot read tracked source inventory.' }
if ($sourcePaths | Where-Object { $_ -match '(^|/)(\.local|bin|obj|artifacts|TestResults)/' }) { throw 'Private/generated files are tracked.' }
$artifactRoot = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $repoRoot 'artifacts' }
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$packageName = 'siemens-mcp-server-' + $version + '-direct-offline-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$packageRoot = Join-Path $artifactRoot $packageName
[IO.Directory]::CreateDirectory($packageRoot) | Out-Null
foreach ($source in $sourcePaths | Where-Object { $_.StartsWith('plugin-offline/', [StringComparison]::Ordinal) }) {
    $destination = Join-Path $packageRoot $source.Substring(15)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot $source) -Destination $destination
}
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot '.mcp.json'))) { throw 'Stage the offline source files before packaging.' }
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($relative in @('plugin.json', '.codex-plugin/plugin.json')) {
    $plugin = Get-Content -LiteralPath (Join-Path $repoRoot ('plugin/' + $relative)) -Raw | ConvertFrom-Json
    if (-not (Test-AppVersionEqual $plugin.version $version)) { throw 'Plugin/release versions differ.' }
    $plugin.description = 'Direct offline Siemens export/reference/text-list analysis. No native TIA, controller, worker or shared service access.'
    $destination = Join-Path $packageRoot $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::WriteAllText($destination, ($plugin | ConvertTo-Json -Depth 8), $utf8)
}
$mcp = Get-Content -LiteralPath (Join-Path $packageRoot '.mcp.json') -Raw | ConvertFrom-Json
$mcp.mcpServers.'siemens-mcp-server' | Add-Member -NotePropertyName type -NotePropertyValue stdio
$portable = @{ '$schema' = 'https://agent-plugins.org/schemas/1.0.0/mcp.schema.json'; mcpServers = $mcp.mcpServers }
[IO.File]::WriteAllText((Join-Path $packageRoot 'mcp.json'), ($portable | ConvertTo-Json -Depth 8), $utf8)
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'plugin/app-version.json') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'plugin/licenses') -Destination $packageRoot -Recurse
$scripts = Join-Path $packageRoot 'scripts'; [IO.Directory]::CreateDirectory($scripts) | Out-Null
foreach ($helper in @('Install-CodexPlugin.ps1', 'Version-Common.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $helper) -Destination $scripts }
& dotnet publish (Join-Path $repoRoot 'Offline/SiemensMCPServer.Offline.csproj') -c Release --nologo -v minimal -p:RestoreLockedMode=true -o (Join-Path $packageRoot 'server')
if ($LASTEXITCODE -ne 0) { throw 'Offline publish failed; incomplete package retained.' }
$assembly = Join-Path $packageRoot 'server/SiemensMCPServer.Offline.dll'
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
if ($info.ProductVersion -cne $release.display_version -or $info.FileVersion -cne $release.file_version -or [Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString() -cne $release.assembly_version) { throw 'Offline assembly differs from the fixed stamp.' }
if (Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Name -match '^Siemens\.Engineering|^SiemensMCPServer\.(Mcp|ExtensionWorker)\.|\.whl$|\.py$' }) { throw 'Offline package contains a forbidden runtime route.' }
$metadata = [ordered]@{
    profile = 'direct-offline'; pluginVersion = $version; displayVersion = $release.display_version;
    assemblyVersion = $release.assembly_version; fileVersion = $release.file_version; tiaPortalApi = 'none';
    sourceCommit = (& git -C $repoRoot rev-parse HEAD); sourceHasLocalChanges = [bool](& git -C $repoRoot status --porcelain);
    nativeWriteAcceptance = 'Unavailable in this profile'; runtimeAcceptance = 'Requires disposable direct MCP and Codex loading checks';
    builtUtc = [DateTime]::UtcNow.ToString('o')
}
[IO.File]::WriteAllText((Join-Path $packageRoot 'build-info.json'), ($metadata | ConvertTo-Json), $utf8)
$sources = @($sourcePaths | ForEach-Object { [ordered]@{ path = $_; sha256 = Get-OfflineHash (Join-Path $repoRoot $_) } })
[IO.File]::WriteAllText((Join-Path $packageRoot 'source-manifest.json'), (ConvertTo-Json -InputObject $sources -Depth 5), $utf8)
$files = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/'); bytes = $_.Length; sha256 = Get-OfflineHash $_.FullName }
})
[IO.File]::WriteAllText((Join-Path $packageRoot 'manifest.json'), (ConvertTo-Json -InputObject $files -Depth 5), $utf8)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $artifactRoot ($packageName + '.zip')
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$hash = Get-OfflineHash $zipPath
[IO.File]::WriteAllText(($zipPath + '.sha256'), ($hash + '  ' + [IO.Path]::GetFileName($zipPath) + "`n"), $utf8)
[ordered]@{ package = $zipPath; sha256 = $hash; version = $release.display_version; profile = 'direct-offline'; mcpStarted = $false } | ConvertTo-Json
