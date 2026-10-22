function ConvertTo-AppPackageVersion([string]$Value) {
    if (-not $Value -or $Value.Length -gt 64 -or $Value -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Use a three-part numeric application version.' }
    return (($Value.Split('.') | ForEach-Object { ([int]::Parse($_, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture)).ToString([Globalization.CultureInfo]::InvariantCulture) }) -join '.')
}
function Test-AppVersionEqual([string]$Left, [string]$Right) {
    return (ConvertTo-AppPackageVersion $Left) -ceq (ConvertTo-AppPackageVersion $Right)
}
function ConvertTo-AppAssemblyVersion([string]$Value) {
    $parts = (ConvertTo-AppPackageVersion $Value).Split('.')
    $display = ('{0:00}.{1:0000}.{2:000000}' -f [int]$parts[0], [int]$parts[1], [int]$parts[2])
    $parsed = [DateTime]::MinValue
    if ($display -cmatch '^[0-9]{2}\.[0-9]{4}\.[0-9]{6}$' -and [DateTime]::TryParseExact(('20' + $display.Replace('.', '')), 'yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsed)) {
        $clock = $display.Split('.')[2]
        return ('{0}.{1}.{2}.{3}' -f [int]$parts[0], [int]$parts[1], [int]$clock.Substring(0,4), [int]$clock.Substring(4,2))
    }
    if (@($parts | Where-Object { [int]$_ -gt 65534 }).Count) { throw 'Application version cannot fit a bounded .NET assembly identity.' }
    return (($parts -join '.') + '.0')
}
