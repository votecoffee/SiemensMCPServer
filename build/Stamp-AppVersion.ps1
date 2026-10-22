param([string]$Utc = '')
$ErrorActionPreference = 'Stop'
$checkpoint = if ($Utc) { [DateTimeOffset]::Parse($Utc, [Globalization.CultureInfo]::InvariantCulture) } else { [DateTimeOffset]::UtcNow }
$local = [TimeZoneInfo]::ConvertTime($checkpoint, [TimeZoneInfo]::FindSystemTimeZoneById('Eastern Standard Time'))
$display = $local.ToString('yy.MMdd.HHmmss', [Globalization.CultureInfo]::InvariantCulture)
& py -3.13 -B (Join-Path $PSScriptRoot '../scripts/stamp_app_version.py') --display $display --utc $checkpoint.ToUniversalTime().ToString('o') --offset-minutes ([int]$local.Offset.TotalMinutes)
if ($LASTEXITCODE -ne 0) { throw 'App checkpoint stamping failed.' }
