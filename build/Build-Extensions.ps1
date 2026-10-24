param(
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$Configuration='Release',
    [string]$WorkerVersion='',
    [string]$WorkerDirectory='',
    [ValidateSet('comfort','unified','plc','project')][string[]]$Modules=@('comfort','unified','plc','project'),
    [switch]$CompactLayout
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Version-Common.ps1')
function Get-ExtensionHash([string]$Path) {
    $algorithm=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($Path)
    try{return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$algorithm.Dispose()}
}
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $WorkerDirectory) {
    $WorkerDirectory=Join-Path $repoRoot ('artifacts\extension-build-'+[Guid]::NewGuid().ToString('N'))
    $buildArguments=@('publish',(Join-Path $repoRoot 'Extensions\SiemensMCPServer.ExtensionWorker.csproj'),'-c',$Configuration,'-p:RestoreLockedMode=true','--nologo','-v','minimal','-o',$WorkerDirectory)
    if($WorkerVersion){
        $canonical = ConvertTo-AppPackageVersion $WorkerVersion
        $assembly = ConvertTo-AppAssemblyVersion $WorkerVersion
        $buildArguments+=@(('-p:Version='+$canonical), ('-p:InformationalVersion='+$WorkerVersion), ('-p:AssemblyVersion='+$assembly), ('-p:FileVersion='+$assembly))
    }
    & dotnet @buildArguments
    if($LASTEXITCODE -ne 0){throw 'Extension worker build failed'}
}
$displayVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $WorkerDirectory 'SiemensMCPServer.ExtensionWorker.exe')).ProductVersion.Split('+')[0]
$version=ConvertTo-AppPackageVersion $displayVersion
$Destination=[IO.Path]::GetFullPath($Destination)
$registry=[ordered]@{}
$registryPath=Join-Path $Destination 'active.json'
if(Test-Path -LiteralPath $registryPath){
    (Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object {$registry[$_.Name]=$_.Value}
}
foreach($module in $Modules) {
    $releaseName = if ($CompactLayout) { [Guid]::NewGuid().ToString('N').Substring(0,12) } else { $version+'-'+[Guid]::NewGuid().ToString('N') }
    $relative='releases/'+$module+'/'+$releaseName
    $directory=Join-Path $Destination $relative
    New-Item -ItemType Directory -Path $directory | Out-Null
    Get-ChildItem -LiteralPath $WorkerDirectory -Force | Copy-Item -Destination $directory -Recurse
    if(Get-ChildItem -LiteralPath $directory -Filter 'Siemens.Engineering*' -Recurse){throw 'Extension worker must not include Siemens engineering assemblies'}
    $files=@(Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($directory.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-ExtensionHash $_.FullName)}
    })
    $manifest=[ordered]@{format='tia-extension-v1';protocol=1;module=$module;version=$version;displayVersion=$displayVersion;entryPoint='SiemensMCPServer.ExtensionWorker.exe';files=$files}
    [IO.File]::WriteAllText((Join-Path $directory 'extension.json'),($manifest | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    $registry[$module]=$relative
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$temporary=Join-Path $Destination ('active-'+[Guid]::NewGuid().ToString('N')+'.tmp')
[IO.File]::WriteAllText($temporary,($registry | ConvertTo-Json -Depth 3),[Text.UTF8Encoding]::new($false))
if(Test-Path -LiteralPath $registryPath){[IO.File]::Replace($temporary,$registryPath,(Join-Path $Destination 'active.previous.json'))}else{[IO.File]::Move($temporary,$registryPath)}
Write-Output ('Extension registry: '+$registryPath)
Write-Output ('Worker version: '+$version)
