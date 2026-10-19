param([string]$OutputPath = (Join-Path $PSScriptRoot '..\assets\SiemensMCPServer.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# Original HMI-screen/PLC artwork. No Siemens corporate logo or external image assets.
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 24, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#142D40'))
    $accent = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#00D5BF'))
    $white = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#F1F7FA'))
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.ScaleTransform(($size / 256.0), ($size / 256.0))
        $graphics.FillRectangle($background, 8, 8, 240, 240)
        $graphics.FillRectangle($accent, 34, 40, 164, 120)
        $graphics.FillRectangle($background, 45, 51, 142, 90)
        $graphics.FillRectangle($accent, 96, 157, 22, 24)
        $graphics.FillRectangle($accent, 70, 177, 76, 9)
        $graphics.FillRectangle($background, 120, 117, 110, 108)
        $graphics.FillRectangle($white, 130, 127, 90, 88)
        $graphics.FillRectangle($background, 140, 139, 70, 18)
        foreach ($x in @(142, 165, 188)) {
            $graphics.FillRectangle($accent, $x, 170, 13, 13)
            $graphics.FillRectangle($background, $x, 193, 13, 9)
        }
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
    } finally {
        $stream.Dispose(); $white.Dispose(); $accent.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
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
