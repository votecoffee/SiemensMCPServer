param(
    [Parameter(Mandatory = $true)][string]$Package,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'SiemensMCPServer\CodexMarketplace'),
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-BytesHash([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() } finally { $algorithm.Dispose() }
}
function Get-SafePath([string]$Root, [string]$Relative) {
    if (-not $Relative -or $Relative.Contains('\') -or [IO.Path]::IsPathRooted($Relative)) { throw ('Unsafe package member path: ' + $Relative) }
    foreach ($part in $Relative.Split('/')) {
        if (-not $part -or $part -eq '.' -or $part -eq '..' -or $part -match '[<>:"|?*\x00-\x1f]' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { throw 'Unsafe package member path.' }
    }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $full.StartsWith($Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package member escapes its root.' }
    return $full
}
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$root = [IO.Path]::GetFullPath($InstallRoot)
$zipHash = Get-BytesHash ([IO.File]::ReadAllBytes($packagePath))
if (-not (Test-Path -LiteralPath ($packagePath + '.sha256'))) { throw 'Package SHA-256 sidecar is required.' }
$expectedHash = ((Get-Content -LiteralPath ($packagePath + '.sha256') -Raw).Trim() -split '\s+')[0]
if ($expectedHash -cne $zipHash) { throw 'Package SHA-256 differs from its sidecar.' }
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $payload = [Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$total = 0
    $legacySeparators = $false
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name -cne $entry.FullName) { $legacySeparators = $true }
        if ($name.EndsWith('/')) { continue }
        Get-SafePath $root $name | Out-Null
        if ($payload.ContainsKey($name)) { throw 'Duplicate/case-colliding package member.' }
        if (($entry.ExternalAttributes -band 0xF0000000) -eq 0xA0000000) { throw 'Linked package member is not supported.' }
        $total += $entry.Length
        if ($entry.Length -gt 64MB -or $total -gt 512MB -or $payload.Count -ge 1000) { throw 'Package exceeds transfer limits.' }
        if ([IO.Path]::GetFileName($name) -match '^Siemens\.Engineering.*\.dll$') { throw 'Package must not redistribute Siemens engineering assemblies.' }
        $entryStream = $entry.Open(); $buffer = [IO.MemoryStream]::new()
        try { $entryStream.CopyTo($buffer); $payload.Add($name, $buffer.ToArray()) } finally { $entryStream.Dispose(); $buffer.Dispose() }
    }
} finally { $archive.Dispose() }
if (-not $payload.ContainsKey('manifest.json')) { throw 'Package manifest is missing.' }
$utf8 = [Text.UTF8Encoding]::new($false)
$manifest = $utf8.GetString($payload['manifest.json']).TrimStart([char]0xFEFF) | ConvertFrom-Json
if (@($manifest).Count -ne $payload.Count - 1) { throw 'Package member set differs from its manifest.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest) {
    if (-not $seen.Add($file.path) -or -not $payload.ContainsKey($file.path) -or $file.path -eq 'manifest.json') { throw 'Manifest member set is invalid.' }
    if ($payload[$file.path].LongLength -ne $file.bytes -or (Get-BytesHash $payload[$file.path]) -cne $file.sha256) { throw ('Payload hash/length differs: ' + $file.path) }
}
$plugin = $utf8.GetString($payload['plugin.json']) | ConvertFrom-Json
$build = $utf8.GetString($payload['build-info.json']).TrimStart([char]0xFEFF) | ConvertFrom-Json
if ($plugin.name -cne 'siemens-mcp-server' -or $plugin.version -notmatch '^\d+\.\d+\.\d+$' -or $build.tiaPortalApi -notmatch '^V\d+(\.\d+)?$' -or $build.pluginVersion -cne $plugin.version) { throw 'Unexpected plugin identity/build metadata.' }
$slug = $plugin.version + '-' + $build.tiaPortalApi.Replace('.', '_') + '-' + $zipHash.Substring(0, 12)
$destination = Join-Path $root ('plugins\' + $slug)
if (Test-Path -LiteralPath $destination) {
    $existing = @(Get-ChildItem -LiteralPath $destination -File -Recurse -Force)
    if ($existing.Count -ne $payload.Count) { throw 'Existing installed source directory has unexpected files; retained for inspection.' }
    foreach ($name in $payload.Keys) {
        $path = Get-SafePath $destination $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-BytesHash ([IO.File]::ReadAllBytes($path))) -cne (Get-BytesHash $payload[$name])) { throw 'Existing installed source differs from the verified package.' }
    }
} else {
    foreach ($name in $payload.Keys) {
        $path = Get-SafePath $destination $name
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
        try { $output.Write($payload[$name], 0, $payload[$name].Length) } finally { $output.Dispose() }
    }
}
$catalogDirectory = Join-Path $root '.agents\plugins'
[IO.Directory]::CreateDirectory($catalogDirectory) | Out-Null
$catalogPath = Join-Path $catalogDirectory 'marketplace.json'
$backupDirectory = Join-Path $root ('backups\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
if (Test-Path -LiteralPath $catalogPath) {
    $oldCatalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    if ($oldCatalog.name -ne 'siemens-mcp-server-local' -or @($oldCatalog.plugins).Count -ne 1 -or $oldCatalog.plugins[0].name -ne 'siemens-mcp-server') { throw 'Install root contains an unrelated marketplace; choose another InstallRoot.' }
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    Copy-Item -LiteralPath $catalogPath -Destination (Join-Path $backupDirectory 'marketplace.json')
}
$catalog = [ordered]@{ name = 'siemens-mcp-server-local'; interface = @{ displayName = 'SiemensMCPServer (local)' }; plugins = @(@{
    name = 'siemens-mcp-server'; source = @{ source = 'local'; path = './plugins/' + $slug };
    policy = @{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }; category = 'Productivity'
}) }
[IO.File]::WriteAllText($catalogPath, ($catalog | ConvertTo-Json -Depth 8), $utf8)
if ($PrepareOnly) {
    @{ state = 'prepared'; marketplaceRoot = $root; pluginSource = $destination; packageSha256 = $zipHash; settingsChanged = $false; legacyZipSeparatorsNormalized = $legacySeparators } | ConvertTo-Json
    exit 0
}
$codex = (Get-Command codex -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$configurationRoot = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$cachePrefix = [IO.Path]::GetFullPath((Join-Path $configurationRoot 'plugins\cache\siemens-mcp-server-local\siemens-mcp-server')).TrimEnd('\') + '\'
$servers = & $codex mcp list --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect existing Codex MCP registration.' }
$legacyServers = @($servers | Where-Object { $_.name -eq 'siemens-tia-openness' -and $_.enabled })
if ($legacyServers.Count) { throw 'The old siemens-tia-openness server is still enabled. Remove its plugin or direct MCP registration through Codex before installing SiemensMCPServer to avoid duplicate tools. Existing settings were not changed.' }
$direct = @($servers | Where-Object { $_.name -eq 'siemens-mcp-server' -and -not $_.transport.env.PLUGIN_ROOT -and
    -not ($_.transport.cwd -and [IO.Path]::GetFullPath($_.transport.cwd).StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase)) })
if ($direct.Count) { throw 'A direct Siemens MCP registration already exists. Resolve it before installing the plugin to avoid duplicate tools.' }
$configuration = Join-Path $configurationRoot 'config.toml'
if (Test-Path -LiteralPath $configuration) {
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    Copy-Item -LiteralPath $configuration -Destination (Join-Path $backupDirectory 'codex-config.toml')
}
$registration = & $codex plugin marketplace add $root 2>&1
if ($LASTEXITCODE -ne 0) { throw ('Codex marketplace registration failed: ' + ($registration -join "`n")) }
$installation = & $codex plugin add 'siemens-mcp-server@siemens-mcp-server-local' --json
if ($LASTEXITCODE -ne 0) { throw 'Codex plugin installation failed. Verified source and configuration backup were retained.' }
$result = @{ state = 'installed'; version = $plugin.version; api = $build.tiaPortalApi; packageSha256 = $zipHash;
    marketplaceRoot = $root; pluginSource = $destination; configurationBackup = $backupDirectory;
    codex = ($installation | ConvertFrom-Json); nativeProjectChanged = $false;
    nextStep = 'Start a new Codex chat to load the plugin skills/tools. Native project attachment and edits require an explicit target.' }
$result | ConvertTo-Json -Depth 12 | Tee-Object -FilePath (Join-Path $root 'installation.json')
