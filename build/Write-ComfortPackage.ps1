param(
    [Parameter(Mandatory = $true)][string]$PackageJsonPath,
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedPackageSha256
)
$ErrorActionPreference = 'Stop'
function Hash-Bytes([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$inputFile = Get-Item -LiteralPath $PackageJsonPath
if ($inputFile.Length -gt 32MB) { throw 'Package reply exceeds 32 MiB.' }
$payload = [IO.File]::ReadAllText($inputFile.FullName, $utf8) | ConvertFrom-Json
for ($depth = 0; $depth -lt 5 -and $payload.format -ne 'tia-comfort-package-v1'; $depth++) {
    if ($payload.isError) { throw 'The MCP reply is an error.' }
    if ($payload.structuredContent) { $payload = $payload.structuredContent }
    elseif ($payload.result) { $payload = $payload.result }
    elseif ($payload.data) { $payload = $payload.data }
    else { break }
}
if ($payload.format -ne 'tia-comfort-package-v1' -or -not $payload.validation.valid -or $payload.nativeImportAccepted -or $payload.sha256 -ne $ExpectedPackageSha256) {
    throw 'Select a valid reviewed Comfort package reply and its exact package SHA-256.'
}
$destinationPath = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
if (Test-Path -LiteralPath $destinationPath) { throw 'Destination must not exist. Choose a new output directory.' }
$parent = [IO.Directory]::GetParent($destinationPath)
if (-not $parent -or -not $parent.Exists) { throw 'Destination parent must already exist.' }
for ($ancestor = $parent; $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked destination directories are not supported.' }
}
$files = @($payload.files)
if ($files.Count -lt 3 -or $files.Count -gt 52) { throw 'Unexpected package file count.' }
$known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$hashLines = [Collections.Generic.List[string]]::new()
$prepared = [Collections.Generic.List[object]]::new()
[long]$total = 0
foreach ($file in $files) {
    $relative = [string]$file.path
    $parts = $relative.Split('/')
    if ([string]::IsNullOrEmpty($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or $relative.Contains(':') -or -not $known.Add($relative)) { throw 'Unsafe or duplicate package path.' }
    foreach ($part in $parts) {
        if ($part -in @('', '.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { throw 'Unsafe Windows filename.' }
    }
    $target = [IO.Path]::GetFullPath((Join-Path $destinationPath $relative))
    if (-not $target.StartsWith($destinationPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes destination.' }
    if ($file.content -isnot [string] -or $file.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Missing file content/hash.' }
    $bytes = $utf8.GetBytes($file.content); $total += $bytes.Length
    if ($total -gt 4MB -or (Hash-Bytes $bytes) -ne $file.sha256) { throw 'Package size or file hash differs.' }
    $prepared.Add(@{ relative = $relative; target = $target; bytes = $bytes })
    $hashLines.Add($relative + ':' + $file.sha256)
}
foreach ($relative in $known) {
    $parts = $relative.Split('/')
    for ($count = 1; $count -lt $parts.Length; $count++) {
        if ($known.Contains(($parts[0..($count - 1)] -join '/'))) { throw 'A package file collides with a directory.' }
    }
}
$hashLines.Sort([StringComparer]::Ordinal)
if ((Hash-Bytes $utf8.GetBytes(($hashLines -join "`n"))) -ne $ExpectedPackageSha256) { throw 'Package inventory differs from the reviewed hash.' }
if (-not $known.Contains('export-index.json') -or -not $known.Contains('package-report.json')) { throw 'Missing native bundle index or report.' }
$index = ($files | Where-Object { $_.path -ceq 'export-index.json' }).content | ConvertFrom-Json
if ($index.format -ne 'tia-engineering-export-v1' -or $index.family -ne 'classic' -or @($index.failures).Count -ne 0 -or @($index.entries).Count -ne ($files.Count - 2)) { throw 'Invalid native bundle index.' }
$indexed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $index.entries) {
    if ($entry.nativeType -notin @('Siemens.Engineering.Hmi.Screen.Screen', 'Siemens.Engineering.Hmi.Tag.TagTable')) { throw 'Unexpected native resource type.' }
    $native = [string]$entry.sourceDirectory + '/payload.xml'
    if (-not $known.Contains($native) -or -not $indexed.Add($native)) { throw 'Unindexed or duplicate native payload.' }
}
# Every payload is checked before the first write. The index is written last, so a partial
# filesystem failure does not leave a ready-to-import bundle index.
[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
foreach ($file in ($prepared | Sort-Object { $_.relative -ceq 'export-index.json' })) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file.target)) | Out-Null
    $stream = [IO.File]::Open($file.target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($file.bytes, 0, $file.bytes.Length) } finally { $stream.Dispose() }
}
@{ artifactDirectory = $destinationPath; sha256 = $ExpectedPackageSha256; files = $files.Count; nativeImportAccepted = $false; projectAttached = $false } | ConvertTo-Json
