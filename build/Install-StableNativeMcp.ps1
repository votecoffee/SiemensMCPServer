[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$InstallRoot=(Join-Path $env:USERPROFILE 'AppData\Local\SiemensMCPServer\StandaloneNativeMcp'),
    [string]$AcceptanceReceipt='',
    [string]$ExpectedSelectionSha256='',
    [switch]$AllowWrites,
    [switch]$PilotOnly,
    [switch]$NoPause
)
$ErrorActionPreference='Stop'
[AppContext]::SetSwitch('Switch.System.IO.UseLegacyPathHandling',$false)
[AppContext]::SetSwitch('Switch.System.IO.BlockLongPaths',$false)
function Assert-Ordinary([string]$Path) {
    for($p=[IO.Path]::GetFullPath($Path);$p;$p=[IO.Path]::GetDirectoryName($p)) {
        if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked native deployment path'}
    }
}
function Get-Digest([string]$Path) {
    $s=[IO.File]::OpenRead($Path);$h=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($h.ComputeHash($s)).Replace('-','').ToLowerInvariant()}finally{$s.Dispose();$h.Dispose()}
}
function Write-Record([string]$Path,$Value) {
    Assert-Ordinary $Path
    $raw=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 30 -Compress))
    $s=[IO.File]::Open($Path,[IO.FileMode]::CreateNew)
    try{$s.Write($raw,0,$raw.Length);$s.Flush($true)}finally{$s.Dispose()}
}
try {
    $root=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\');Assert-Ordinary $root
    if($PilotOnly -and (-not $PSBoundParameters.ContainsKey('InstallRoot') -or $root -eq (Join-Path $env:USERPROFILE 'AppData\Local\SiemensMCPServer\StandaloneNativeMcp'))){throw 'A pilot requires a distinct explicit private root'}
    $zipHash=Get-Digest ((Resolve-Path -LiteralPath $Package).Path)
    if(-not $PilotOnly) {
        if(-not $AcceptanceReceipt){throw 'Exact-package native acceptance receipt is required'}
        $accepted=Get-Content -LiteralPath $AcceptanceReceipt -Raw -Encoding UTF8|ConvertFrom-Json
        if($accepted.candidateZipSha256 -cne $zipHash -or $accepted.metadataPassed -cne $true -or $accepted.pureWorkersPassed -cne $true -or
           $accepted.nativeEditReadback -cne $true -or $accepted.compileSucceeded -cne $true -or $accepted.restoreVerified -cne $true -or
           $accepted.saveReopenVerified -cne $true -or $accepted.disposableTargetsOnly -cne $true -or $accepted.uncertaintyOutstanding -cne $false){throw 'Native acceptance is missing, uncertain or for different bytes'}
    }
    [IO.Directory]::CreateDirectory($root)|Out-Null
    $lock=[IO.File]::Open((Join-Path $root 'publication.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        if(-not ('SiemensNativePhysicalPath' -as [type])){Add-Type -TypeDefinition @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class SiemensNativePhysicalPath {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(IntPtr h,StringBuilder p,uint n,uint f);
 public static string Resolve(IntPtr h){var b=new StringBuilder(32768);uint n=GetFinalPathNameByHandle(h,b,(uint)b.Capacity,0);if(n==0||n>=b.Capacity)throw new InvalidOperationException("Physical native path unavailable");string p=b.ToString();return p.StartsWith(@"\\?\UNC\")?@"\\"+p.Substring(8):p.StartsWith(@"\\?\")?p.Substring(4):p;}
}
'@}
        $root=[IO.Path]::GetDirectoryName([SiemensNativePhysicalPath]::Resolve($lock.SafeFileHandle.DangerousGetHandle()));Assert-Ordinary $root
        $pointer=Join-Path $root 'launcher\current.json';Assert-Ordinary $pointer
        $prior=if(Test-Path -LiteralPath $pointer){Get-Digest $pointer}else{'absent'}
        if($ExpectedSelectionSha256 -and $ExpectedSelectionSha256 -cne $prior){throw 'Native pointer preimage differs; no retry'}
        $prepared=& (Join-Path $PSScriptRoot 'Install-CodexPlugin.ps1') -Package $Package -PrepareOnly -NativeCandidate -InstallRoot (Join-Path $root 'p')|ConvertFrom-Json
        if($prepared.state -ne 'prepared' -or $prepared.settingsChanged -or $prepared.packageSha256 -cne $zipHash){throw 'Native package preparation failed'}
        $source=$prepared.pluginSource;$build=Get-Content -LiteralPath (Join-Path $source 'build-info.json') -Raw -Encoding UTF8|ConvertFrom-Json
        if($build.profile -cne 'native-direct-candidate' -or $build.sourceHasLocalChanges){throw 'Native package source is not a clean candidate'}
        $runtimeRelative='r/'+$zipHash.Substring(0,16);$runtime=Join-Path $root $runtimeRelative;Assert-Ordinary $runtime
        $server=Join-Path $source 'server'
        $entries=@(Get-ChildItem -LiteralPath $server -Recurse -Force -File|Sort-Object FullName|ForEach-Object{
            Assert-Ordinary $_.FullName
            [ordered]@{path=$_.FullName.Substring($server.Length+1).Replace('\','/');bytes=$_.Length;sha256=Get-Digest $_.FullName}
        })
        if($entries.Count -lt 4 -or $entries.Count -gt 250){throw 'Native runtime inventory bound'}
        if(-not(Test-Path -LiteralPath $runtime)){
            $stage=Join-Path $root ('r\.stage-'+[Guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($stage)|Out-Null
            foreach($entry in $entries){$target=Join-Path $stage $entry.path;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null;[IO.File]::Copy((Join-Path $server $entry.path),$target,$false);if((Get-Digest $target) -cne $entry.sha256){throw 'Native staging bytes differ'}}
            [IO.Directory]::Move($stage,$runtime)
        }
        if(@(Get-ChildItem -LiteralPath $runtime -Recurse -Force -File).Count -ne $entries.Count){throw 'Native immutable inventory differs'}
        foreach($entry in $entries){$target=Join-Path $runtime $entry.path;Assert-Ordinary $target;if((Get-Digest $target) -cne $entry.sha256){throw 'Native immutable bytes differ'}}
        $assets=@(Get-ChildItem -LiteralPath (Join-Path $source 'native-launcher-assets') -File)
        if($assets.Count -lt 3 -or $assets.Count -gt 20 -or -not($assets.Name -contains 'SiemensMCPServer.NativeLauncher.exe')){throw 'Native stable assets missing'}
        foreach($asset in $assets){
            $target=Join-Path $root $asset.Name;Assert-Ordinary $target
            if(Test-Path -LiteralPath $target){if((Get-Digest $target) -cne (Get-Digest $asset.FullName)){throw 'Native launcher ABI bytes differ; retain live peers for reviewed maintenance'}}
            else{[IO.File]::Copy($asset.FullName,$target,$false)}
        }
        $settings=Join-Path $root 'settings\safeguards.json';Assert-Ordinary $settings
        if(-not(Test-Path -LiteralPath $settings)){
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($settings))|Out-Null
            $policy=Get-Content -LiteralPath (Join-Path $source 'scripts/fixtures/native-pilot-policy.json') -Raw|ConvertFrom-Json
            if($AllowWrites){foreach($field in @('desktopWritesEnabled','mcpWritesEnabled','allowApply','allowCompile','allowSave','allowArchive','allowReopen','allowDelete','allowOverwrite','allowSourceReplacement','allowProjectTextImport')){$policy.$field=$true}}
            Write-Record $settings $policy
        }
        $snapshots=Join-Path $root 'launcher\snapshots';[IO.Directory]::CreateDirectory($snapshots)|Out-Null
        $snapshot=[ordered]@{schemaVersion=1;profile='direct-native';version=$build.displayVersion;packageSha256=$zipHash;runtime=$runtimeRelative;tiaVersion=$build.tiaPortalApi;allowWrites=[bool]$AllowWrites;files=$entries}
        $snapshotStage=Join-Path $snapshots ('.stage-'+[Guid]::NewGuid().ToString('N'));Write-Record $snapshotStage $snapshot;$snapshotHash=Get-Digest $snapshotStage
        $snapshotPath=Join-Path $snapshots ($snapshotHash+'.json')
        if(Test-Path -LiteralPath $snapshotPath){if((Get-Digest $snapshotPath) -cne $snapshotHash){throw 'Native snapshot differs'}}else{[IO.File]::Move($snapshotStage,$snapshotPath)}
        $pluginName=$build.displayVersion+'-'+$zipHash.Substring(0,12);$plugin=Join-Path $root ('CodexMarketplace\plugins\'+$pluginName);Assert-Ordinary $plugin
        $environment=[ordered]@{DOTNET_EnableDiagnostics='0';DOTNET_DbgEnableMiniDump='0';COMPlus_EnableDiagnostics='0';COMPlus_DbgEnableMiniDump='0';DOTNET_STARTUP_HOOKS='';DOTNET_ADDITIONAL_DEPS='';DOTNET_SHARED_STORE='';CORECLR_ENABLE_PROFILING='0';COR_ENABLE_PROFILING='0'}
        $launch=@{mcpServers=@{'siemens-mcp-server'=@{command=(Join-Path $root 'SiemensMCPServer.NativeLauncher.exe');args=@();cwd=$root;env=$environment}}}
        if(-not(Test-Path -LiteralPath $plugin)){
            [IO.Directory]::CreateDirectory($plugin)|Out-Null
            foreach($relative in @('plugin.json','.codex-plugin/plugin.json','LICENSE')){$target=Join-Path $plugin $relative;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null;[IO.File]::Copy((Join-Path $source $relative),$target,$false)}
            foreach($directory in @('skills','licenses')){Copy-Item -LiteralPath (Join-Path $source $directory) -Destination $plugin -Recurse}
            Write-Record (Join-Path $plugin '.mcp.json') $launch
            $portable=@{mcpServers=@{'siemens-mcp-server'=@{type='stdio';command=(Join-Path $root 'SiemensMCPServer.NativeLauncher.exe');args=@();cwd=$root;env=$environment}}};Write-Record (Join-Path $plugin 'mcp.json') $portable
            $derived=@(Get-ChildItem -LiteralPath $plugin -Recurse -Force -File|Sort-Object FullName|ForEach-Object{[ordered]@{path=$_.FullName.Substring($plugin.Length+1).Replace('\','/');bytes=$_.Length;sha256=Get-Digest $_.FullName}})
            Write-Record (Join-Path $plugin 'manifest.json') $derived
        }else{
            $existing=Get-Content -LiteralPath (Join-Path $plugin '.mcp.json') -Raw|ConvertFrom-Json
            $server=$existing.mcpServers.'siemens-mcp-server'
            if(@($existing.mcpServers.PSObject.Properties).Count -ne 1 -or $server.command -cne $launch.mcpServers.'siemens-mcp-server'.command -or @($server.args).Count -ne 0 -or $server.cwd -cne $root -or @($server.env.PSObject.Properties).Count -ne $environment.Count){throw 'Derived native plugin binding differs'}
            foreach($key in $environment.Keys){if($server.env.$key -cne $environment[$key]){throw 'Derived native environment differs'}}
            foreach($skill in Get-ChildItem -LiteralPath (Join-Path $source 'skills') -Recurse -File){$relative=$skill.FullName.Substring((Join-Path $source 'skills').Length+1);if((Get-Digest (Join-Path $plugin ('skills/'+$relative))) -cne (Get-Digest $skill.FullName)){throw 'Derived native skill bytes differ'}}
            foreach($relative in @('plugin.json','.codex-plugin/plugin.json','LICENSE')){if((Get-Digest (Join-Path $plugin $relative)) -cne (Get-Digest (Join-Path $source $relative))){throw 'Derived native metadata differs'}}
            $derived=Get-Content -LiteralPath (Join-Path $plugin 'manifest.json') -Raw|ConvertFrom-Json
            if(@(Get-ChildItem -LiteralPath $plugin -Recurse -Force -File).Count -ne @($derived).Count+1){throw 'Derived native inventory differs'}
            foreach($entry in $derived){$target=Join-Path $plugin $entry.path;Assert-Ordinary $target;if(-not $target.StartsWith($plugin+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-Digest $target) -cne $entry.sha256){throw 'Derived native inventory hash differs'}}
        }
        if($AcceptanceReceipt){$acceptance=Join-Path $root 'acceptance';[IO.Directory]::CreateDirectory($acceptance)|Out-Null;$acceptanceHash=Get-Digest $AcceptanceReceipt;$target=Join-Path $acceptance ($acceptanceHash+'.json');if(-not(Test-Path -LiteralPath $target)){[IO.File]::Copy((Resolve-Path -LiteralPath $AcceptanceReceipt).Path,$target,$false)}elseif((Get-Digest $target) -cne $acceptanceHash){throw 'Retained acceptance differs'}}
        $catalogRoot=Join-Path $root 'CodexMarketplace\.agents\plugins';[IO.Directory]::CreateDirectory($catalogRoot)|Out-Null
        $catalog=Join-Path $catalogRoot 'marketplace.json';$catalogStage=$catalog+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
        Write-Record $catalogStage @{name='siemens-mcp-server-local';interface=@{displayName='SiemensMCPServer (local)'};plugins=@(@{name='siemens-mcp-server';source=@{source='local';path=('./plugins/'+$pluginName)};policy=@{installation='AVAILABLE';authentication='ON_INSTALL'};category='Productivity'})}
        $pointerStage=$pointer+'.'+[Guid]::NewGuid().ToString('N')+'.tmp';Write-Record $pointerStage @{schemaVersion=1;snapshotSha256=$snapshotHash}
        $observed=if(Test-Path -LiteralPath $pointer){Get-Digest $pointer}else{'absent'}
        if($observed -cne $prior){throw 'Native pointer changed while staging; no retry'}
        $backup=$null
        if($prior -eq 'absent'){[IO.File]::Move($pointerStage,$pointer)}elseif((Get-Digest $pointerStage) -cne $prior){$backup=Join-Path $root ('launcher\selection-before-'+[Guid]::NewGuid().ToString('N')+'.json');[IO.File]::Replace($pointerStage,$pointer,$backup)}
        if(Test-Path -LiteralPath $catalog){[IO.File]::Replace($catalogStage,$catalog,($catalog+'.'+[Guid]::NewGuid().ToString('N')+'.bak'))}else{[IO.File]::Move($catalogStage,$catalog)}
        $receipt=[ordered]@{state='selected';profile='direct-native';pilotOnly=[bool]$PilotOnly;version=$build.displayVersion;packageSha256=$zipHash;snapshotSha256=$snapshotHash;selectionSha256=Get-Digest $pointer;priorSelectionSha256=$prior;selectionBackup=$backup;command=(Join-Path $root 'SiemensMCPServer.NativeLauncher.exe');args=@();cwd=$root;env=$environment;runtime=$runtime;pluginSource=$plugin;marketplaceRoot=(Join-Path $root 'CodexMarketplace');safeguardsFile=$settings;settingsChanged=$false;nativeAcceptanceReceiptSha256=$(if($AcceptanceReceipt){Get-Digest $AcceptanceReceipt}else{$null})}
        Write-Record (Join-Path $root ('installation-'+[Guid]::NewGuid().ToString('N')+'.json')) $receipt
        $receipt|ConvertTo-Json -Depth 12
    }finally{$lock.Dispose()}
}catch{[Console]::Error.WriteLine('Siemens native installation failed: '+$_.Exception.Message);exit 1}
