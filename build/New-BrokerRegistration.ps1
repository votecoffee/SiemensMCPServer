param(
    [Parameter(Mandatory = $true)][string]$PluginRoot,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string[]]$AllowedWriteTools = @()
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PluginRoot).Path
$server = Join-Path $root 'server\SiemensMCPServer.Mcp.exe'
$plugin = Get-Content -LiteralPath (Join-Path $root 'plugin.json') -Raw | ConvertFrom-Json
if ($plugin.name -cne 'siemens-mcp-server' -or [version]$plugin.version -lt [version]'0.14.0' -or -not (Test-Path -LiteralPath $server -PathType Leaf)) { throw 'A verified SiemensMCPServer 0.14.0 or newer package root is required.' }
foreach ($tool in $AllowedWriteTools) {
    if ($tool -cnotmatch '^(tia|hmi|plc)_[a-z_]{1,120}$') { throw 'Use exact native MCP tool names for the reviewed write allowlist.' }
}
$path = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $path) { throw 'Registration output already exists; retain it and choose a new output path.' }
$descriptor = [ordered]@{
    schema_version = 1; id = 'siemens-mcp-server'; display_name = 'SiemensMCPServer';
    description = 'TIA Portal HMI and offline PLC engineering. Requires an exclusive context across target selection, preview and apply.';
    transport = [ordered]@{ type = 'stdio'; command = $server; args = @(); cwd = $root; env_vars = @() };
    enabled = $true; allow_writes = $AllowedWriteTools.Count -gt 0; allowed_write_tools = @($AllowedWriteTools | Sort-Object -Unique);
    read_only_tools = @(); max_argument_bytes = 262144; timeout_seconds = 300;
    session_mode = 'exclusive'; lease_seconds = 900; resource_id = 'siemens-tia-openness';
    capability_catalog = @{ schema_version = 1; tool = 'tia_list_broker_capabilities'; arguments = @{} }
}
if ($AllowedWriteTools.Count -gt 0) { $descriptor.transport.args = @('--allow-writes') }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
$json = $descriptor | ConvertTo-Json -Depth 8
$stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
@{ state = 'broker-registration-prepared'; registration = $path; sessionMode = 'exclusive'; writesEnabled = $descriptor.allow_writes;
   codexPluginInstalled = $false; brokerSettingsChanged = $false; nativeProjectChanged = $false;
   nextStep = 'Review and explicitly register this descriptor with the selected broker. Retain direct integration until workflow/host/native migration acceptance is complete. Never switch routes during a prepared or uncertain operation.' } | ConvertTo-Json
