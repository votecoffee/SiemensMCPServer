param(
    [Parameter(Mandatory = $true)][ValidateSet('V15.1', 'V19')][string]$Version,
    [Parameter(Mandatory = $true)][string]$ServerDirectory,
    [ValidateSet('SiemensMCPServer.Mcp.exe', 'SiemensMCPServer.exe')][string]$ExecutableName = 'SiemensMCPServer.Mcp.exe'
)
$ErrorActionPreference = 'Stop'
# Run with Windows PowerShell/.NET Framework. Loads API metadata only; creates no TiaPortal instance.
$installVersion = $Version.Replace('.', '_')
$apiDirectory = Join-Path ${env:ProgramFiles} "Siemens\Automation\Portal $installVersion\PublicAPI\$Version"
$api = [Reflection.Assembly]::LoadFrom((Join-Path $apiDirectory 'Siemens.Engineering.dll'))
$server = [Reflection.Assembly]::LoadFrom((Join-Path (Resolve-Path $ServerDirectory) $ExecutableName))
$adapter = $server.GetType('SiemensMCPServer.Mcp.TransferNative', $true)
$export = $adapter.GetMethod('ExportMethod', [Reflection.BindingFlags]'NonPublic,Static', $null, [Type[]]@([Type]), $null)
$import = $adapter.GetMethod('ImportMethod', [Reflection.BindingFlags]'NonPublic,Static', $null, [Type[]]@([Type]), $null)
$checked = [Collections.Generic.List[string]]::new()
function Assert-Contract([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Native contract failed: $Description" }
    $checked.Add($Description)
}
$resources = @('Hmi.Globalization.MultiLingualGraphic', 'Hmi.Communication.Connection', 'Hmi.Cycle.Cycle', 'Hmi.RuntimeScripting.VBScript', 'Hmi.Tag.TagTable', 'Hmi.TextGraphicList.TextList', 'Hmi.TextGraphicList.GraphicList', 'Hmi.Screen.ScreenTemplate', 'Hmi.Screen.ScreenPopup', 'Hmi.Screen.ScreenSlidein', 'Hmi.Screen.ScreenOverview', 'Hmi.Screen.Screen')
foreach ($name in $resources) {
    $type = $api.GetType("Siemens.Engineering.$name", $true)
    $method = $export.Invoke($null, [object[]]@($type))
    Assert-Contract ($null -ne $method -and $method.GetParameters()[0].ParameterType -eq [IO.FileInfo]) "$name uses native XML export"
}
$folders = @('Hmi.RuntimeScripting.VBScriptUserFolderComposition', 'Hmi.Tag.TagUserFolderComposition', 'Hmi.Screen.ScreenTemplateUserFolderComposition', 'Hmi.Screen.ScreenPopupUserFolderComposition', 'Hmi.Screen.ScreenUserFolderComposition')
foreach ($name in $folders) {
    $type = $api.GetType("Siemens.Engineering.$name", $true)
    Assert-Contract ($null -ne $type.GetMethod('Create', [Type[]]@([string]))) "$name supports named folder creation"
}
$compositions = @('Hmi.Globalization.MultiLingualGraphicComposition', 'Hmi.Communication.ConnectionComposition', 'Hmi.Cycle.CycleComposition', 'Hmi.RuntimeScripting.VBScriptComposition', 'Hmi.Tag.TagTableComposition', 'Hmi.TextGraphicList.TextListComposition', 'Hmi.TextGraphicList.GraphicListComposition', 'Hmi.Screen.ScreenTemplateComposition', 'Hmi.Screen.ScreenPopupComposition', 'Hmi.Screen.ScreenSlideinComposition', 'Hmi.Screen.ScreenComposition', 'SW.Blocks.PlcBlockComposition', 'SW.Types.PlcTypeComposition', 'SW.Tags.PlcTagTableComposition')
$importOptions = $api.GetType('Siemens.Engineering.ImportOptions', $true)
foreach ($name in $compositions) {
    $type = $api.GetType("Siemens.Engineering.$name", $true)
    $method = $import.Invoke($null, [object[]]@($type))
    Assert-Contract ($null -ne $method -and $method.GetParameters()[0].ParameterType -eq [IO.FileInfo]) "$name uses native XML import dispatch"
}
foreach ($name in @('SW.ExternalSources.PlcExternalSource', 'SW.ExternalSources.PlcExternalSourceComposition')) {
    $type = $api.GetType("Siemens.Engineering.$name", $true)
    Assert-Contract ($null -eq $export.Invoke($null, [object[]]@($type)) -and $null -eq $import.Invoke($null, [object[]]@($type))) "$name has no generic native transfer route"
}
$hmi = $api.GetType('Siemens.Engineering.Hmi.HmiTarget', $true)
$provider = $api.GetType('Siemens.Engineering.IEngineeringServiceProvider', $true)
foreach ($name in @('Hmi.HmiTarget', 'SW.PlcSoftware', 'HW.DeviceItem', 'HW.Device')) {
    Assert-Contract ($provider.IsAssignableFrom($api.GetType("Siemens.Engineering.$name", $true))) "$name supports native compile-service lookup"
}
$compiler = $api.GetType('Siemens.Engineering.Compiler.ICompilable', $true)
Assert-Contract ($null -ne $compiler.GetMethod('Compile', [Type[]]@())) 'Compile service has a parameterless native invocation'
$sourceGroup = $api.GetType('Siemens.Engineering.SW.ExternalSources.PlcExternalSourceSystemGroup', $true)
$sourceMethod = @($sourceGroup.GetMethods() | Where-Object { $_.Name -eq 'GenerateSource' -and $_.GetParameters().Length -eq 3 })
Assert-Contract ($sourceMethod.Count -eq 1 -and $sourceMethod[0].GetParameters()[1].ParameterType -eq [IO.FileInfo]) 'Readable-source export exposes native dependency options and file output'
$sourceInterface = $api.GetType('Siemens.Engineering.SW.ExternalSources.IGenerateSource', $true)
foreach ($name in @('SW.Blocks.CodeBlock', 'SW.Blocks.DataBlock', 'SW.Types.PlcType')) {
    Assert-Contract ($sourceInterface.IsAssignableFrom($api.GetType("Siemens.Engineering.$name", $true))) "$name supports native readable-source export"
}
$referenceService = $api.GetType('Siemens.Engineering.CrossReference.CrossReferenceService', $false)
Assert-Contract (($null -ne $referenceService) -eq ($Version -eq 'V19')) 'Native cross-reference service availability matches this inspected profile'
if ($referenceService) {
    Assert-Contract (@($provider.GetMethods() | Where-Object { $_.Name -eq 'GetService' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Length -eq 0 }).Count -eq 1) 'Cross-reference service supports the guarded generic service lookup'
    Assert-Contract (@($referenceService.GetMethods() | Where-Object { $_.Name -eq 'GetCrossReferences' -and $_.GetParameters().Length -eq 1 -and $_.GetParameters()[0].ParameterType.IsEnum }).Count -eq 1) 'Cross-reference query has the native filter signature'
    foreach ($name in @('SourceObject', 'ReferenceObject', 'Location')) {
        $referenceType = $api.GetType("Siemens.Engineering.CrossReference.$name", $true)
        $fields = if ($name -eq 'Location') { @('Name','ReferenceLocation','ReferenceType','Access','ReferencedAsName','Address','TypeName') } else { @('Name','Address','TypeName','Device','Path') }
        foreach ($field in $fields) {
            $property = $referenceType.GetProperty($field)
            Assert-Contract ($null -ne $property -and ($property.PropertyType -eq [string] -or $property.PropertyType.IsEnum)) "$name exposes textual/enum field $field"
        }
    }
}
Assert-Contract ($null -ne $hmi.GetMethod('ImportScreenOverview', [Type[]]@([IO.FileInfo], $importOptions))) 'HmiTarget imports the permanent-area singleton'
$slidein = $api.GetType('Siemens.Engineering.Hmi.Screen.ScreenSlidein', $true)
Assert-Contract ($null -ne $slidein.GetProperty('SlideinType') -and $null -eq $slidein.GetProperty('Name')) 'Slide-in identity is its side/type, not a name'
if ($Version -eq 'V19') {
    foreach ($name in @('HmiUnified.UI.Screens.HmiScreen', 'HmiUnified.UI.Screens.HmiScreenComposition')) {
        $type = $api.GetType("Siemens.Engineering.$name", $true)
        Assert-Contract ($null -eq $export.Invoke($null, [object[]]@($type)) -and $null -eq $import.Invoke($null, [object[]]@($type))) "$name has no generic native transfer route in V19"
    }
    foreach ($name in @('HmiUnified.HmiTags.HmiTagComposition', 'HmiUnified.Scripts.HmiScriptModule', 'HmiUnified.Scripts.HmiScriptModuleComposition')) {
        $type = $api.GetType("Siemens.Engineering.$name", $true)
        $method = $export.Invoke($null, [object[]]@($type))
        Assert-Contract ($null -ne $method -and $method.GetParameters().Length -eq 1 -and $method.GetParameters()[0].ParameterType -eq [IO.DirectoryInfo]) "$name exports using native directory/default names"
        if ($name.EndsWith('Composition')) {
            $method = $import.Invoke($null, [object[]]@($type))
            Assert-Contract ($null -ne $method -and $method.GetParameters().Length -eq 1 -and $method.GetParameters()[0].ParameterType -eq [IO.DirectoryInfo]) "$name imports using native directory/default names"
        }
    }
}
@{ version = $Version; contracts = $checked.Count; checks = $checked.ToArray(); tiaPortalCreated = $false; projectAttached = $false; nativeWriteAcceptance = 'pending' } | ConvertTo-Json -Depth 5
