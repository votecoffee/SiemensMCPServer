[CmdletBinding()]
param(
    [string]$Package,
    [string]$InstallRoot = (Join-Path $env:USERPROFILE 'AppData\Local\SiemensMCPServer\StandaloneMcp'),
    [string]$ExpectedSelectionSha256 = '',
    [Alias('h')][switch]$Help,
    [switch]$NoPause,
    [switch]$Native,
    [string]$AcceptanceReceipt='',
    [switch]$AllowWrites,
    [switch]$PilotOnly
)
$ErrorActionPreference = 'Stop'
if($Native) {
    $nativeArgs=@{Package=$Package;AcceptanceReceipt=$AcceptanceReceipt;ExpectedSelectionSha256=$ExpectedSelectionSha256;AllowWrites=$AllowWrites;PilotOnly=$PilotOnly;NoPause=$NoPause}
    if($PSBoundParameters.ContainsKey('InstallRoot')){$nativeArgs.InstallRoot=$InstallRoot}
    & (Join-Path $PSScriptRoot 'Install-StableNativeMcp.ps1') @nativeArgs
    exit $LASTEXITCODE
}
if ($Help) { Write-Output 'InstallMcp.cmd -Package <reviewed-offline.zip> [-InstallRoot <persistent-root>] [-ExpectedSelectionSha256 <hash-or-absent>] [-NoPause]. Installs/selects app-owned offline bytes and stages the standalone plugin; never edits Codex settings or starts a server.'; exit 0 }
try {
    if (-not $Package) { throw '-Package is required. Build/review an offline package first.' }
    $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    function Assert-NoLinks([string]$Path) {
        for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
            if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked installation paths are unsupported.' }
        }
    }
    function Get-Digest([string]$Path) {
        $stream = [IO.File]::OpenRead($Path); $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() } finally { $stream.Dispose(); $sha.Dispose() }
    }
    function Write-Json([string]$Path, $Value) {
        Assert-NoLinks $Path
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 20 -Compress))
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    }
    Assert-NoLinks $root
    [IO.Directory]::CreateDirectory($root) | Out-Null
    # File sharing exclusion also serializes different logical aliases of this root.
    $publishLock = [IO.File]::Open((Join-Path $root 'publication.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    # MSIX can virtualize individual AppData files while the directory's spelling
    # stays logical. Resolve the actual opened file, then publish its physical root.
    if (-not ('SiemensInstalledPath' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SiemensInstalledPath {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, uint length, uint flags);
    public static string FromHandle(IntPtr handle) {
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new InvalidOperationException("Cannot establish physical installation path.");
        string value = path.ToString();
        return value.StartsWith(@"\\?\UNC\") ? @"\\" + value.Substring(8) : value.StartsWith(@"\\?\") ? value.Substring(4) : value;
    }
}
'@
    }
    $root = [IO.Path]::GetDirectoryName([SiemensInstalledPath]::FromHandle($publishLock.SafeFileHandle.DangerousGetHandle()))
    Assert-NoLinks $root
    $mutexKey = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant())).Replace('\', '_').Replace('/', '_')
    $mutex = [Threading.Mutex]::new($false, ('Local\SiemensStablePublish-' + $mutexKey))
    $owned = $false
    try {
        try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { throw 'Another stable publication owns this root.' }
        $pointer = Join-Path $root 'launcher\current.json'
        Assert-NoLinks $pointer
        $prior = if (Test-Path -LiteralPath $pointer) { Get-Digest $pointer } else { 'absent' }
        if ($ExpectedSelectionSha256 -and $ExpectedSelectionSha256 -cne $prior) { throw 'Selection preimage changed; no retry.' }
        # Existing verified installer performs ZIP/manifest/profile/environment validation.
        $prepared = & (Join-Path $PSScriptRoot 'Install-CodexPlugin.ps1') -Package $Package -PrepareOnly -InstallRoot (Join-Path $root 'retained-packages') -AppHostRoot (Join-Path $root 'unused-app-host') | ConvertFrom-Json
        if ($prepared.state -ne 'prepared' -or $prepared.settingsChanged) { throw 'Package preparation failed.' }
        $source = $prepared.pluginSource
        $metadata = Get-Content -LiteralPath (Join-Path $source 'build-info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($metadata.profile -cne 'direct-offline') { throw 'Only the closed direct-offline profile is accepted.' }
        if (@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Prepared package contains links.' }
        $runtimeName = $metadata.displayVersion + '-' + $prepared.packageSha256.Substring(0,12)
        $runtimeRelative = 'runtimes/' + $runtimeName
        $runtime = Join-Path $root $runtimeRelative
        $entries = @(Get-ChildItem -LiteralPath (Join-Path $source 'server') -File -Recurse | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = $_.FullName.Substring((Join-Path $source 'server').Length + 1).Replace('\','/'); bytes = $_.Length; sha256 = Get-Digest $_.FullName }
        })
        Assert-NoLinks $runtime
        if (-not (Test-Path -LiteralPath $runtime)) {
            $stage = Join-Path $root ('runtimes\.stage-' + [Guid]::NewGuid().ToString('N'))
            [IO.Directory]::CreateDirectory($stage) | Out-Null
            foreach ($entry in $entries) {
                $target = Join-Path $stage $entry.path
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                Copy-Item -LiteralPath (Join-Path $source ('server/' + $entry.path)) -Destination $target
                if ((Get-Digest $target) -cne $entry.sha256) { throw 'Runtime changed while staging.' }
            }
            # Exact generated directory stays beneath this verified installation root.
            [IO.Directory]::Move($stage, $runtime)
        }
        if (@(Get-ChildItem -LiteralPath $runtime -File -Recurse).Count -ne $entries.Count) { throw 'Immutable runtime inventory differs.' }
        foreach ($entry in $entries) { Assert-NoLinks (Join-Path $runtime $entry.path); if ((Get-Digest (Join-Path $runtime $entry.path)) -cne $entry.sha256) { throw 'Immutable runtime bytes differ.' } }
        $assets = @(Get-ChildItem -LiteralPath (Join-Path $source 'launcher-assets') -File)
        if ($assets.Count -ne 5 -or -not ($assets.Name -contains 'SiemensMCPServer.Launcher.exe')) { throw 'Reviewed launcher assets are incomplete.' }
        $image = [IO.File]::ReadAllBytes((Join-Path $source 'launcher-assets\SiemensMCPServer.Launcher.exe'))
        if ($image.Length -lt 64) { throw 'Invalid stable apphost.' }
        $pe = [BitConverter]::ToInt32($image, 0x3c)
        if ($pe -lt 0 -or $pe + 94 -gt $image.Length -or [BitConverter]::ToUInt32($image, $pe) -ne 0x4550 -or [BitConverter]::ToUInt16($image, $pe + 4) -ne 0x8664 -or [BitConverter]::ToUInt16($image, $pe + 24 + 68) -ne 2) { throw 'Stable apphost must be windowless Windows x64.' }
        foreach ($asset in $assets) {
            $target = Join-Path $root $asset.Name; Assert-NoLinks $target
            if (Test-Path -LiteralPath $target) {
                if ((Get-Digest $target) -cne (Get-Digest $asset.FullName)) { throw 'Stable launcher ABI bytes differ. Retain live peers and perform a separately reviewed launcher maintenance update.' }
            } else { [IO.File]::Copy($asset.FullName, $target, $false) }
        }
        $snapshots = Join-Path $root 'launcher\snapshots'; Assert-NoLinks $snapshots
        [IO.Directory]::CreateDirectory($snapshots) | Out-Null
        $snapshot = [ordered]@{ schemaVersion = 1; profile = 'direct-offline'; version = $metadata.displayVersion; packageSha256 = $prepared.packageSha256; runtime = $runtimeRelative; files = $entries }
        $snapshotStage = Join-Path $snapshots ('.stage-' + [Guid]::NewGuid().ToString('N'))
        Write-Json $snapshotStage $snapshot
        $snapshotHash = Get-Digest $snapshotStage
        $snapshotPath = Join-Path $snapshots ($snapshotHash + '.json')
        if (Test-Path -LiteralPath $snapshotPath) { Assert-NoLinks $snapshotPath; if ((Get-Digest $snapshotPath) -cne $snapshotHash) { throw 'Existing snapshot differs.' } }
        else { [IO.File]::Move($snapshotStage, $snapshotPath) }
        # Derived app-owned standalone plugin. Original package/cache stays immutable.
        $rootSha = [Security.Cryptography.SHA256]::Create()
        try { $bindingId = [BitConverter]::ToString($rootSha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root))).Replace('-', '').ToLowerInvariant().Substring(0,8) } finally { $rootSha.Dispose() }
        $pluginName = $runtimeName + '-' + $bindingId
        $plugin = Join-Path $root ('CodexMarketplace\plugins\' + $pluginName)
        if (-not (Test-Path -LiteralPath $plugin)) { [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($plugin)) | Out-Null; Copy-Item -LiteralPath $source -Destination $plugin -Recurse }
        $launch = Get-Content -LiteralPath (Join-Path $source '.mcp.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $launch.mcpServers.'siemens-mcp-server'.command = Join-Path $root 'SiemensMCPServer.Launcher.exe'
        $launch.mcpServers.'siemens-mcp-server'.cwd = $root
        # Only newly created app-owned derived metadata is rendered; no cache mutation.
        foreach ($manifest in @('.mcp.json','mcp.json')) {
            $value = if ($manifest -eq '.mcp.json') { $launch } else { @{ '$schema'='https://agent-plugins.org/schemas/1.0.0/mcp.schema.json'; mcpServers=$launch.mcpServers } }
            if ($manifest -eq 'mcp.json') { $launch.mcpServers.'siemens-mcp-server' | Add-Member -NotePropertyName type -NotePropertyValue stdio -Force }
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($value | ConvertTo-Json -Depth 10))
            $manifestPath = Join-Path $plugin $manifest
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($manifestPath)) -cne [Convert]::ToBase64String($bytes)) {
                if ((Get-Digest $manifestPath) -cne (Get-Digest (Join-Path $source $manifest))) { throw 'Existing derived plugin differs.' }
                [IO.File]::WriteAllBytes($manifestPath, $bytes)
            }
        }
        $derivedFiles = @(Get-ChildItem -LiteralPath $source -Recurse -Force -File)
        foreach ($file in $derivedFiles) {
            $relative = $file.FullName.Substring($source.Length + 1)
            if ($relative -in @('manifest.json','.mcp.json','mcp.json')) { continue }
            $target = Join-Path $plugin $relative; Assert-NoLinks $target
            if (-not (Test-Path -LiteralPath $target) -or (Get-Digest $target) -cne (Get-Digest $file.FullName)) { throw 'Derived plugin payload differs.' }
        }
        $derivedManifest = @(Get-ChildItem -LiteralPath $plugin -Recurse -Force -File | Where-Object { $_.Name -ne 'manifest.json' } | Sort-Object FullName | ForEach-Object {
            @{ path=$_.FullName.Substring($plugin.Length + 1).Replace('\','/'); bytes=$_.Length; sha256=(Get-Digest $_.FullName) }
        })
        if ($derivedManifest.Count -ne $derivedFiles.Count - 1) { throw 'Derived plugin inventory differs.' }
        [IO.File]::WriteAllBytes((Join-Path $plugin 'manifest.json'), [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $derivedManifest -Depth 8)))
        $catalogRoot = Join-Path $root 'CodexMarketplace\.agents\plugins'; [IO.Directory]::CreateDirectory($catalogRoot) | Out-Null
        $catalog = Join-Path $catalogRoot 'marketplace.json'
        $catalogStage = $catalog + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        Write-Json $catalogStage @{ name='siemens-mcp-server-local'; interface=@{displayName='SiemensMCPServer (local)'}; plugins=@(@{ name='siemens-mcp-server'; source=@{source='local';path=('./plugins/' + $pluginName)}; policy=@{installation='AVAILABLE';authentication='ON_INSTALL'};category='Productivity' }) }
        $pointerStage = $pointer + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        Write-Json $pointerStage @{ schemaVersion=1; snapshotSha256=$snapshotHash }
        $readback = Get-Content -LiteralPath $pointerStage -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($readback.snapshotSha256 -cne $snapshotHash) { throw 'Pointer readback differs.' }
        $observed = if (Test-Path -LiteralPath $pointer) { Get-Digest $pointer } else { 'absent' }
        if ($observed -cne $prior) { throw 'Selection changed during preparation; no retry.' }
        $backup = $null
        if ($prior -eq 'absent') { [IO.File]::Move($pointerStage, $pointer) }
        elseif ((Get-Digest $pointerStage) -cne $prior) {
            $backup = Join-Path $root ('launcher\selection-before-' + [Guid]::NewGuid().ToString('N') + '.json')
            [IO.File]::Replace($pointerStage, $pointer, $backup)
        }
        if (Test-Path -LiteralPath $catalog) { [IO.File]::Replace($catalogStage, $catalog, ($catalog + '.' + [Guid]::NewGuid().ToString('N') + '.bak')) }
        else { [IO.File]::Move($catalogStage, $catalog) }
        $receipt = [ordered]@{ state='selected'; profile='direct-offline'; version=$metadata.displayVersion; packageSha256=$prepared.packageSha256; snapshotSha256=$snapshotHash; selectionSha256=(Get-Digest $pointer); priorSelectionSha256=$prior; selectionBackup=$backup; command=(Join-Path $root 'SiemensMCPServer.Launcher.exe'); args=@(); cwd=$root; env=$launch.mcpServers.'siemens-mcp-server'.env; runtime=$runtime; pluginSource=$plugin; marketplaceRoot=(Join-Path $root 'CodexMarketplace'); settingsChanged=$false; managementStatus='Not reporting'; restartAvailable=$false }
        $receiptPath = Join-Path $root ('installation-' + [Guid]::NewGuid().ToString('N') + '.json')
        Write-Json $receiptPath $receipt
        $receipt | ConvertTo-Json -Depth 12
    } finally { if ($owned) { $mutex.ReleaseMutex() }; $mutex.Dispose(); $publishLock.Dispose() }
} catch { [Console]::Error.WriteLine(('Siemens offline installation failed: ' + $_.Exception.Message)); exit 1 }
