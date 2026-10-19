param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\assets\SiemensMCPServer.ico'),
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\assets\siemens-plc')
)
$ErrorActionPreference = 'Stop'
# Package the supplied PNG frames verbatim. No resampling or drawing is performed.
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $frame = [IO.File]::ReadAllBytes((Join-Path $SourceDirectory ("siemens_plc_icon_{0}x{0}.png" -f $size)))
    if ($frame.Length -lt 33 -or [BitConverter]::ToString($frame, 0, 8) -cne '89-50-4E-47-0D-0A-1A-0A' -or
        [Text.Encoding]::ASCII.GetString($frame, 12, 4) -cne 'IHDR' -or
        [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($frame, 16)) -ne $size -or
        [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($frame, 20)) -ne $size) { throw "Invalid PNG frame for size $size." }
    $frames.Add($frame)
}
$path = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
$file = [IO.File]::Create($path); $writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Application icon: $path"
