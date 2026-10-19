param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'SiemensMCPServer\Desktop'),
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallRoot)
$receiptPath = Join-Path $root 'active.json'
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'Desktop GUI is not installed here. Run InstallAndRun.cmd first.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($receipt.format -cne 'siemens-desktop-install-v1' -or $receipt.directory -notmatch '^apps/[a-zA-Z0-9._-]+$' -or
    $receipt.engineeringVersion -notmatch '^V\d+(\.\d+)?$' -or $receipt.executableSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'Invalid desktop installation receipt. Reinstall the desktop GUI.'
}
$directory = [IO.Path]::GetFullPath((Join-Path $root $receipt.directory))
if (-not $directory.StartsWith((Join-Path $root 'apps') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Desktop installation path escapes its application directory.' }
$executable = Join-Path $directory 'SiemensMCPServer.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.executableSha256) {
    throw 'Installed desktop executable is missing or changed. Run InstallAndRun.cmd to reinstall.'
}
if ($CheckOnly) {
    [ordered]@{ state = 'ready'; executable = $executable; api = $receipt.api; engineeringVersion = $receipt.engineeringVersion } | ConvertTo-Json
} else {
    # The user requested the interactive desktop application, so show its normal window.
    $process = Start-Process -FilePath $executable -ArgumentList @('--tia-version=' + $receipt.engineeringVersion) -WorkingDirectory $directory -PassThru
    [ordered]@{ state = 'started'; processId = $process.Id; executable = $executable; nativeProjectAttached = $false } | ConvertTo-Json
}
