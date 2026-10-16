param(
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [string]$ExtensionRoot=(Join-Path $env:LOCALAPPDATA 'SiemensTiaOpenness\Extensions'),
    [string]$ExpectedManifestSha256=''
)
$ErrorActionPreference='Stop'
function Get-ExtensionHash([string]$Path) {
    $algorithm=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($Path)
    try{return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$algorithm.Dispose()}
}
function Resolve-SafePath([string]$Root,[string]$Relative) {
    if(-not $Relative -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains('\') -or $Relative.Contains(':') -or @($Relative.Split('/') | Where-Object {$_ -eq '' -or $_ -eq '.' -or $_ -eq '..'}).Count){throw 'Unsafe extension relative path'}
    $current=[IO.Path]::GetFullPath($Root)
    foreach($part in @('')+$Relative.Split('/')) {
        if($part){$current=Join-Path $current $part}
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked extension paths are not supported'}
    }
    return $current
}
function Read-Bundle([string]$Root) {
    $manifestPath=Resolve-SafePath $Root 'extension.json'
    if((Get-Item -LiteralPath $manifestPath).Length -gt 1MB){throw 'Extension manifest exceeds size limit'}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.format -ne 'tia-extension-v1' -or $manifest.protocol -ne 1 -or $manifest.module -cnotmatch '^[a-z][a-z0-9_-]{0,31}$' -or $manifest.entryPoint -cne 'SiemensTiaOpenness.ExtensionWorker.exe' -or ([version]$manifest.version).ToString(3) -cne $manifest.version){throw 'Incompatible extension manifest'}
    $files=@($manifest.files)
    if($files.Count -eq 0 -or $files.Count -gt 256){throw 'Invalid extension inventory'}
    $seen=@{};$total=0L
    foreach($file in $files) {
        $path=Resolve-SafePath $Root $file.path
        if($seen.ContainsKey($file.path) -or $file.path -ieq 'extension.json' -or [IO.Path]::GetFileName($file.path).StartsWith('Siemens.Engineering',[StringComparison]::OrdinalIgnoreCase)){throw 'Duplicate or forbidden extension member'}
        $seen[$file.path]=$true;$item=Get-Item -LiteralPath $path -Force;$total+=$item.Length
        if($item.PSIsContainer -or $item.Length -gt 64MB -or $total -gt 512MB -or $item.Length -ne $file.bytes -or (Get-ExtensionHash $path) -cne $file.sha256){throw 'Extension payload hash or length differs from manifest'}
    }
    if(-not $seen.ContainsKey($manifest.entryPoint)){throw 'Missing extension executable'}
    # Check directory entries explicitly before recursion; never follow links.
    $pending=[Collections.Generic.Queue[string]]::new();$pending.Enqueue($Root);$count=0
    while($pending.Count) {
        $directory=$pending.Dequeue()
        foreach($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked extension member'}
            $relative=$entry.FullName.Substring($Root.Length+1).Replace('\','/')
            if($relative.Split('/').Count -gt 32){throw 'Extension nesting limit exceeded'}
            if($entry.PSIsContainer){$pending.Enqueue($entry.FullName)}elseif($relative -cne 'extension.json'){
                $count++;if($count -gt 256 -or -not $seen.ContainsKey($relative)){throw 'Unlisted extension member'}
            }
        }
    }
    if($count -ne $files.Count){throw 'Extension inventory differs from payload'}
    return $manifest
}
$BundleDirectory=[IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\','/')
$ExtensionRoot=[IO.Path]::GetFullPath($ExtensionRoot).TrimEnd('\','/')
$manifest=Read-Bundle $BundleDirectory
$hash=Get-ExtensionHash (Join-Path $BundleDirectory 'extension.json')
if($ExpectedManifestSha256 -and $hash -cne $ExpectedManifestSha256){throw 'Manifest SHA-256 differs from expected value'}
$relative='releases/'+$manifest.module+'/'+$manifest.version+'-'+[Guid]::NewGuid().ToString('N')
$target=Resolve-SafePath $ExtensionRoot $relative
New-Item -ItemType Directory -Path $target | Out-Null
foreach($file in @($manifest.files)+@([pscustomobject]@{path='extension.json'})) {
    $destination=Join-Path $target $file.path
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $BundleDirectory $file.path) -Destination $destination
}
$null=Read-Bundle $target
if((Get-ExtensionHash (Join-Path $target 'extension.json')) -cne $hash){throw 'Manifest changed during staging'}
$registryPath=Resolve-SafePath $ExtensionRoot 'active.json'
$registry=[ordered]@{}
if(Test-Path -LiteralPath $registryPath) {
    if((Get-Item -LiteralPath $registryPath).Length -gt 64KB){throw 'Extension registry exceeds size limit'}
    (Get-Content -LiteralPath $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object {
        if($_.Name -cnotmatch '^[a-z][a-z0-9_-]{0,31}$'){throw 'Invalid module name in extension registry'}
        $null=Resolve-SafePath $ExtensionRoot $_.Value;$registry[$_.Name]=$_.Value
    }
}
$registry[$manifest.module]=$relative
if($registry.Count -gt 16){throw 'Extension registry module limit exceeded'}
$temporary=Join-Path $ExtensionRoot ('active-'+[Guid]::NewGuid().ToString('N')+'.tmp')
[IO.File]::WriteAllText($temporary,($registry | ConvertTo-Json -Depth 3),[Text.UTF8Encoding]::new($false))
if(Test-Path -LiteralPath $registryPath){[IO.File]::Replace($temporary,$registryPath,(Join-Path $ExtensionRoot 'active.previous.json'))}else{[IO.File]::Move($temporary,$registryPath)}
[ordered]@{module=$manifest.module;version=$manifest.version;manifestSha256=$hash;registry=$registryPath;bundleDirectory=$target;reloadRequired=$true;projectChanged=$false} | ConvertTo-Json
