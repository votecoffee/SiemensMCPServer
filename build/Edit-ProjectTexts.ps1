param(
    [Parameter(Mandatory=$true)][string]$RequestPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$WorkerDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $WorkerDirectory) {
    $extensions = Join-Path $root 'server\extensions'
    $registryPath = Join-Path $extensions 'active.json'
    if (Test-Path -LiteralPath $registryPath) {
        $registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
        $WorkerDirectory = Join-Path $extensions $registry.project
    } else { $WorkerDirectory = Join-Path $root 'Extensions\bin\Release\net48' }
}
$worker = Join-Path ([IO.Path]::GetFullPath($WorkerDirectory)) 'SiemensMCPServer.ExtensionWorker.exe'
if (-not (Test-Path -LiteralPath $worker -PathType Leaf)) { throw 'Build the feature worker or use an extracted plugin with the project-text command.' }
& $worker ('--project-texts=' + [IO.Path]::GetFullPath($RequestPath)) ('--output=' + [IO.Path]::GetFullPath($OutputDirectory))
if ($LASTEXITCODE -ne 0) { throw 'Project-text extraction/preview failed; no complete report manifest was produced.' }
