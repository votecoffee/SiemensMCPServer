param([string]$OutputRoot='', [string]$TiaPortalVersion='V15.1', [string]$TiaPortalInstallVersion='', [string]$TiaPublicApiDir='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Version-Common.ps1')
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Get-NativeHash([string]$Path) {
    $sha=[Security.Cryptography.SHA256]::Create(); $stream=[IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $stream.Dispose(); $sha.Dispose() }
}
$release=Get-Content -LiteralPath (Join-Path $repoRoot 'plugin/app-version.json') -Raw | ConvertFrom-Json
$sourcePaths=@(& git -C $repoRoot -c core.quotepath=false ls-files --cached | Sort-Object -Unique)
if($LASTEXITCODE -ne 0 -or -not $sourcePaths.Count){throw 'Cannot inventory source'}
if($sourcePaths | Where-Object { $_ -match '(^|/)(\.local|bin|obj|artifacts|TestResults)/' }){throw 'Generated/private source is tracked'}
$artifactRoot=if($OutputRoot){[IO.Path]::GetFullPath($OutputRoot)}else{Join-Path $repoRoot 'artifacts'}
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$name='siemens-mcp-server-'+$release.package_version+'-native-direct-candidate-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$packageRoot=Join-Path $artifactRoot $name
[IO.Directory]::CreateDirectory($packageRoot) | Out-Null
foreach($source in $sourcePaths | Where-Object { $_.StartsWith('plugin-native/',[StringComparison]::Ordinal) }) {
    $target=Join-Path $packageRoot $source.Substring(14)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot $source) -Destination $target
}
if(-not (Test-Path -LiteralPath (Join-Path $packageRoot 'skills/edit-hmi-text/SKILL.md'))){throw 'Stage the native candidate sources before packaging'}
$utf8=[Text.UTF8Encoding]::new($false)
$readmePath=Join-Path $packageRoot 'README.md'
[IO.File]::WriteAllText($readmePath,[IO.File]::ReadAllText($readmePath).Replace('[LICENSE](../LICENSE)','[LICENSE](LICENSE)'),$utf8)
foreach($relative in @('plugin.json','.codex-plugin/plugin.json')) {
    $plugin=Get-Content -LiteralPath (Join-Path $repoRoot ('plugin/'+$relative)) -Raw | ConvertFrom-Json
    if(-not (Test-AppVersionEqual $plugin.version $release.package_version)){throw 'Plugin stamp mismatch'}
    $plugin.description='Direct native TIA Openness candidate; installation held pending disposable/native acceptance.'
    $target=Join-Path $packageRoot $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::WriteAllText($target,($plugin | ConvertTo-Json -Depth 8),$utf8)
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'plugin/app-version.json') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'plugin/licenses') -Destination $packageRoot -Recurse
$scripts=Join-Path $packageRoot 'scripts'; [IO.Directory]::CreateDirectory($scripts) | Out-Null
foreach($helper in @('Install-Extension.ps1','Write-ComfortPackage.ps1','Analyze-HmiReferences.ps1','Edit-ProjectTexts.ps1','Version-Common.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $helper) -Destination $scripts
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/prepare_native_pilot.py') -Destination $scripts
& py -3.13 -B (Join-Path $repoRoot 'scripts/build_native_guidance.py') --repo $repoRoot --destination (Join-Path $packageRoot 'server/native-guidance')
if($LASTEXITCODE -ne 0){throw 'Native guidance projection failed'}
$arguments=@('publish',(Join-Path $repoRoot 'Mcp/SiemensMCPServer.Mcp.csproj'),'-c','Release','--nologo','-v','minimal','-p:RestoreLockedMode=true','-p:StageFeatureExtensions=false',('-p:TiaPortalVersion='+$TiaPortalVersion),'-o',(Join-Path $packageRoot 'server'))
if($TiaPortalInstallVersion){$arguments+='-p:TiaPortalInstallVersion='+$TiaPortalInstallVersion}
if($TiaPublicApiDir){$arguments+='-p:TiaPublicApiDir='+$TiaPublicApiDir}
& dotnet @arguments
if($LASTEXITCODE -ne 0){throw 'Native publish failed; incomplete candidate retained'}
& (Join-Path $PSScriptRoot 'Build-Extensions.ps1') -Destination (Join-Path $packageRoot 'server/extensions') -CompactLayout
$executable=Join-Path $packageRoot 'server/SiemensMCPServer.Mcp.exe'
$info=[Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
if($info.ProductVersion -cne $release.display_version -or $info.FileVersion -cne $release.file_version -or [Reflection.AssemblyName]::GetAssemblyName($executable).Version.ToString() -cne $release.assembly_version){throw 'Native executable stamp mismatch'}
foreach($file in Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.exe') {
    $raw=[IO.File]::ReadAllBytes($file.FullName); $pe=[BitConverter]::ToInt32($raw,0x3c)
    if($pe -lt 0 -or $pe+94 -gt $raw.Length -or [BitConverter]::ToUInt32($raw,$pe) -ne 0x4550 -or [BitConverter]::ToUInt16($raw,$pe+24+68) -ne 2){throw 'Native/worker executables must use the Windows subsystem'}
    if([Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).ProductVersion -cne $release.display_version){throw 'Worker stamp mismatch'}
}
if(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Name -match '^Siemens\.Engineering|\.whl$|^launch\.py$' }){throw 'Vendor DLL/shared service runtime in native candidate'}
$tools=@(foreach($file in @('Mcp/McpTools.cs','Mcp/NativeGuidanceTools.cs')) {
    [regex]::Matches([IO.File]::ReadAllText((Join-Path $repoRoot $file)),'McpServerTool\(Name = "([^"]+)"') | ForEach-Object {$_.Groups[1].Value}
})
if($tools.Count -ne 56 -or @($tools | Sort-Object -Unique).Count -ne 56){throw 'Unexpected static native tool inventory'}
[IO.File]::WriteAllText((Join-Path $packageRoot 'tool-declarations.json'),([ordered]@{sourceOnly=$true;runtimeValidated=$false;tools=$tools}|ConvertTo-Json -Depth 5),$utf8)
$metadata=[ordered]@{profile='native-direct-candidate';pluginVersion=$release.package_version;displayVersion=$release.display_version;assemblyVersion=$release.assembly_version;fileVersion=$release.file_version; tiaPortalApi=$TiaPortalVersion;
    sourceCommit=(& git -C $repoRoot rev-parse HEAD);sourceHasLocalChanges=[bool](& git -C $repoRoot status --porcelain);mcpStarted=$false;nativeAcceptance='Pending coordinated disposable TIA pilot';installation='Held; accepted installed offline package unchanged';builtUtc=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText((Join-Path $packageRoot 'build-info.json'),($metadata | ConvertTo-Json),$utf8)
$sources=@($sourcePaths | ForEach-Object {[ordered]@{path=$_;sha256=Get-NativeHash (Join-Path $repoRoot $_)}})
[IO.File]::WriteAllText((Join-Path $packageRoot 'source-manifest.json'),(ConvertTo-Json -InputObject $sources -Depth 5),$utf8)
$files=@(Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force | Sort-Object FullName | ForEach-Object {[ordered]@{path=$_.FullName.Substring($packageRoot.Length+1).Replace('\','/');bytes=$_.Length;sha256=Get-NativeHash $_.FullName}})
[IO.File]::WriteAllText((Join-Path $packageRoot 'manifest.json'),(ConvertTo-Json -InputObject $files -Depth 5),$utf8)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath=Join-Path $artifactRoot ($name+'.zip'); $zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try { foreach($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse -Force){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$file.FullName.Substring($packageRoot.Length+1).Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal)|Out-Null} } finally {$zip.Dispose()}
$hash=Get-NativeHash $zipPath
[IO.File]::WriteAllText(($zipPath+'.sha256'),($hash+'  '+[IO.Path]::GetFileName($zipPath)+"`n"),$utf8)
[ordered]@{package=$zipPath;sha256=$hash;version=$release.display_version;profile='native-direct-candidate';mcpStarted=$false}|ConvertTo-Json
