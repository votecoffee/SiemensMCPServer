param(
    [Parameter(Mandatory = $true)][string]$PluginRoot,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$SettingsFile = '',
    [string]$ArtifactRoot = '',
    [string]$ExtensionsRoot = '',
    [string[]]$AllowedWriteTools = @()
)
$ErrorActionPreference = 'Stop'
# Preparation only: no framework startup, registration, dependency install or native calls.
$root = (Resolve-Path -LiteralPath $PluginRoot).Path
$output = [IO.Path]::GetFullPath($OutputPath)
if ($output.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep publications outside the immutable plugin payload.' }
if (Test-Path -LiteralPath $output) { throw 'Publication output exists; retain it and choose another path.' }
function Payload-Hash([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create(); $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$manifestPath = Join-Path $root 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'A complete verified plugin payload with manifest.json is required.' }
if ((Get-Item -LiteralPath $manifestPath).Length -gt 1MB -or ((Get-Item -LiteralPath $manifestPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid payload manifest.' }
if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Payload root must not be linked.' }
$entries = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($entries -isnot [Array] -or $entries.Count -lt 1 -or $entries.Count -gt 1000) { throw 'Invalid payload manifest size.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$total = 0L
foreach ($entry in $entries) {
    $relative = [string]$entry.path
    if (-not $relative -or $relative.Contains('\') -or $relative.Contains(':') -or $relative.StartsWith('/') -or @($relative.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or -not $_ }).Count -ne 0 -or -not $seen.Add($relative)) { throw 'Unsafe or duplicate payload path.' }
    $joined = Join-Path $root $relative
    try { $file = [IO.Path]::GetFullPath($joined) }
    catch { throw 'Cannot resolve a payload path. Use an ordinary local package directory supported by this PowerShell runtime.' }
    if (-not $file.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload path escapes root.' }
    $item = Get-Item -LiteralPath $file
    $ancestor = $item
    while ($ancestor -and $ancestor.FullName.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payload paths are not supported.' }
        $ancestor = if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent }
    }
    $total += $item.Length
    if ($item.PSIsContainer -or $item.Length -gt 64MB -or $total -gt 512MB -or $item.Length -ne $entry.bytes -or (Payload-Hash $file) -cne $entry.sha256) { throw 'Payload bytes differ from manifest or exceed limits.' }
}
foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    if ($relative -cne 'manifest.json' -and -not $seen.Contains($relative)) { throw 'Unexpected payload file.' }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$scratch = Join-Path ([IO.Path]::GetDirectoryName($output)) ('framework-prepare-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$descriptor = Join-Path $scratch 'service.json'
try {
    & (Join-Path $PSScriptRoot 'New-BrokerRegistration.ps1') -PluginRoot $root -OutputPath $descriptor -AllowedWriteTools $AllowedWriteTools | Out-Null
    $service = Get-Content -LiteralPath $descriptor -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($SettingsFile) { $service.transport.args = @($service.transport.args) + @('--settings-file=' + [IO.Path]::GetFullPath($SettingsFile)) }
    if ($ArtifactRoot) { $service.transport.args = @($service.transport.args) + @('--artifact-root=' + [IO.Path]::GetFullPath($ArtifactRoot)) }
    if ($ExtensionsRoot) { $service.transport.args = @($service.transport.args) + @('--extensions-root=' + [IO.Path]::GetFullPath($ExtensionsRoot)) }
    $server = [string]$service.transport.command
    $binding = [Text.Encoding]::UTF8.GetBytes((Payload-Hash $manifestPath) + ($service | ConvertTo-Json -Depth 12 -Compress))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $revision = 'payload-' + [BitConverter]::ToString($sha.ComputeHash($binding)).Replace('-', '').ToLowerInvariant().Substring(0, 24) } finally { $sha.Dispose() }
    $publication = [ordered]@{
        schema_version = 1; id = 'siemens-engineering'; display_name = 'SiemensMCPServer';
        instructions = 'Discover this app with search_tools and describe_tool. Inspect Siemens initialization guidance and workflow readers. Acquire the exclusive siemens-mcp-server service before project operations. Preserve explicit TIA process/project/target IDs, preview revisions and candidate hashes. Apply, compile and save are separate. Never replay an uncertain write. App safeguards and Siemens/client permissions remain required.';
        backends = @(@{ revision = $revision; artifact = @{ path = $server; sha256 = Payload-Hash $server }; service = $service })
    }
    $stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($publication | ConvertTo-Json -Depth 16)); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    @{ state = 'framework-publication-prepared'; frameworkVersion = '0.13.0'; publication = $output; backendRevision = $revision;
       writesEnabled = $service.allow_writes; standalonePluginChanged = $false; liveRegistrationChanged = $false; nativeProjectChanged = $false } | ConvertTo-Json
} finally {
    if (Test-Path -LiteralPath $descriptor) { Remove-Item -LiteralPath $descriptor }
    [IO.Directory]::Delete($scratch)
}
