param(
    [Parameter(Mandatory = $true)][ValidateSet('V15.1', 'V16', 'V17', 'V18', 'V19', 'V20', 'V21')][string]$Version,
    [Parameter(Mandatory = $true)][string]$ServerDirectory,
    [string]$TiaPortalInstallVersion = $Version,
    [string]$TiaPublicApiDir,
    [ValidateSet('SiemensMCPServer.Mcp.exe', 'SiemensMCPServer.exe')][string]$ExecutableName = 'SiemensMCPServer.Mcp.exe'
)
$ErrorActionPreference = 'Stop'
# Run with Windows PowerShell/.NET Framework. Loads API metadata only; creates no TiaPortal instance.
$installVersion = $TiaPortalInstallVersion.Replace('.', '_')
$apiDirectory = if ($TiaPublicApiDir) { $TiaPublicApiDir } else { Join-Path ${env:ProgramFiles} "Siemens\Automation\Portal $installVersion\PublicAPI\$Version" }
if ($Version -eq 'V21' -and -not $TiaPublicApiDir) { $apiDirectory = Join-Path $apiDirectory 'net48' }
$assemblies = @()
$modules = if ($Version -eq 'V21') { @('Base', 'WinCC', 'WinCC.Extension', 'Step7', 'WinCCUnified') } else { @('') }
foreach ($module in $modules) {
    $filename = if ($module) { "Siemens.Engineering.$module.dll" } else { 'Siemens.Engineering.dll' }
    $path = Join-Path $apiDirectory $filename
    if ($module -eq 'WinCCUnified' -and -not (Test-Path -LiteralPath $path)) { continue }
    $assemblies += [Reflection.Assembly]::LoadFrom($path)
}
$api = $assemblies[0]
function Get-ApiType([string]$Name, [bool]$Required = $true) {
    foreach ($assembly in $assemblies) {
        $type = $assembly.GetType($Name, $false)
        if ($type) { return $type }
    }
    if ($Required) { throw "Missing native API type: $Name" }
    return $null
}
$server = [Reflection.Assembly]::LoadFrom((Join-Path (Resolve-Path $ServerDirectory) $ExecutableName))
$adapter = $server.GetType('SiemensMCPServer.Mcp.TransferNative', $true)
$export = $adapter.GetMethod('ExportMethod', [Reflection.BindingFlags]'NonPublic,Static', $null, [Type[]]@([Type]), $null)
$import = $adapter.GetMethod('ImportMethod', [Reflection.BindingFlags]'NonPublic,Static', $null, [Type[]]@([Type]), $null)
$checked = [Collections.Generic.List[string]]::new()
function Assert-Contract([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Native contract failed: $Description" }
    $checked.Add($Description)
}
$baseReference = @($server.GetReferencedAssemblies() | Where-Object { $_.Name -eq $api.GetName().Name })
Assert-Contract ($baseReference.Count -eq 1 -and $baseReference[0].FullName -eq $api.GetName().FullName) 'Executable references the exact inspected native API identity'
$resources = @('Hmi.Globalization.MultiLingualGraphic', 'Hmi.Communication.Connection', 'Hmi.Cycle.Cycle', 'Hmi.RuntimeScripting.VBScript', 'Hmi.Tag.TagTable', 'Hmi.TextGraphicList.TextList', 'Hmi.TextGraphicList.GraphicList', 'Hmi.Screen.ScreenTemplate', 'Hmi.Screen.ScreenPopup', 'Hmi.Screen.ScreenSlidein', 'Hmi.Screen.ScreenOverview', 'Hmi.Screen.Screen')
foreach ($name in $resources) {
    $type = Get-ApiType "Siemens.Engineering.$name"
    $method = $export.Invoke($null, [object[]]@($type))
    Assert-Contract ($null -ne $method -and $method.GetParameters()[0].ParameterType -eq [IO.FileInfo]) "$name uses native XML export"
}
$folders = @('Hmi.RuntimeScripting.VBScriptUserFolderComposition', 'Hmi.Tag.TagUserFolderComposition', 'Hmi.Screen.ScreenTemplateUserFolderComposition', 'Hmi.Screen.ScreenPopupUserFolderComposition', 'Hmi.Screen.ScreenUserFolderComposition')
foreach ($name in $folders) {
    $type = Get-ApiType "Siemens.Engineering.$name"
    Assert-Contract ($null -ne $type.GetMethod('Create', [Type[]]@([string]))) "$name supports named folder creation"
}
$compositions = @('Hmi.Globalization.MultiLingualGraphicComposition', 'Hmi.Communication.ConnectionComposition', 'Hmi.Cycle.CycleComposition', 'Hmi.RuntimeScripting.VBScriptComposition', 'Hmi.Tag.TagTableComposition', 'Hmi.TextGraphicList.TextListComposition', 'Hmi.TextGraphicList.GraphicListComposition', 'Hmi.Screen.ScreenTemplateComposition', 'Hmi.Screen.ScreenPopupComposition', 'Hmi.Screen.ScreenSlideinComposition', 'Hmi.Screen.ScreenComposition', 'SW.Blocks.PlcBlockComposition', 'SW.Types.PlcTypeComposition', 'SW.Tags.PlcTagTableComposition')
$importOptions = Get-ApiType 'Siemens.Engineering.ImportOptions'
foreach ($name in $compositions) {
    $type = Get-ApiType "Siemens.Engineering.$name"
    $method = $import.Invoke($null, [object[]]@($type))
    Assert-Contract ($null -ne $method -and $method.GetParameters()[0].ParameterType -eq [IO.FileInfo]) "$name uses native XML import dispatch"
}
foreach ($name in @('SW.ExternalSources.PlcExternalSource', 'SW.ExternalSources.PlcExternalSourceComposition')) {
    $type = Get-ApiType "Siemens.Engineering.$name"
    Assert-Contract ($null -eq $export.Invoke($null, [object[]]@($type)) -and $null -eq $import.Invoke($null, [object[]]@($type))) "$name has no generic native transfer route"
}
$hmi = Get-ApiType 'Siemens.Engineering.Hmi.HmiTarget'
$provider = Get-ApiType 'Siemens.Engineering.IEngineeringServiceProvider'
foreach ($name in @('Hmi.HmiTarget', 'SW.PlcSoftware', 'HW.DeviceItem', 'HW.Device')) {
    Assert-Contract ($provider.IsAssignableFrom((Get-ApiType "Siemens.Engineering.$name"))) "$name supports native compile-service lookup"
}
$compiler = Get-ApiType 'Siemens.Engineering.Compiler.ICompilable'
Assert-Contract ($null -ne $compiler.GetMethod('Compile', [Type[]]@())) 'Compile service has a parameterless native invocation'
$sourceGroup = Get-ApiType 'Siemens.Engineering.SW.ExternalSources.PlcExternalSourceSystemGroup'
$sourceMethod = @($sourceGroup.GetMethods() | Where-Object { $_.Name -eq 'GenerateSource' -and $_.GetParameters().Length -eq 3 })
Assert-Contract ($sourceMethod.Count -eq 1 -and $sourceMethod[0].GetParameters()[1].ParameterType -eq [IO.FileInfo]) 'Readable-source export exposes native dependency options and file output'
$sourceInterface = Get-ApiType 'Siemens.Engineering.SW.ExternalSources.IGenerateSource'
foreach ($name in @('SW.Blocks.CodeBlock', 'SW.Blocks.DataBlock', 'SW.Types.PlcType')) {
    Assert-Contract ($sourceInterface.IsAssignableFrom((Get-ApiType "Siemens.Engineering.$name"))) "$name supports native readable-source export"
}
$referenceService = Get-ApiType 'Siemens.Engineering.CrossReference.CrossReferenceService' $false
$knownCrossReferences = @{ 'V15.1' = $false; 'V16' = $false; 'V17' = $false; 'V18' = $true; 'V19' = $true }
if ($knownCrossReferences.ContainsKey($Version)) {
    Assert-Contract (($null -ne $referenceService) -eq $knownCrossReferences[$Version]) 'Native cross-reference service availability matches this inspected profile'
}
if ($referenceService) {
    Assert-Contract (@($provider.GetMethods() | Where-Object { $_.Name -eq 'GetService' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Length -eq 0 }).Count -eq 1) 'Cross-reference service supports the guarded generic service lookup'
    Assert-Contract (@($referenceService.GetMethods() | Where-Object { $_.Name -eq 'GetCrossReferences' -and $_.GetParameters().Length -eq 1 -and $_.GetParameters()[0].ParameterType.IsEnum }).Count -eq 1) 'Cross-reference query has the native filter signature'
    foreach ($name in @('SourceObject', 'ReferenceObject', 'Location')) {
        $referenceType = Get-ApiType "Siemens.Engineering.CrossReference.$name"
        $fields = if ($name -eq 'Location') { @('Name','ReferenceLocation','ReferenceType','Access','ReferencedAsName','Address','TypeName') } else { @('Name','Address','TypeName','Device','Path') }
        foreach ($field in $fields) {
            $property = $referenceType.GetProperty($field)
            Assert-Contract ($null -ne $property -and ($property.PropertyType -eq [string] -or $property.PropertyType.IsEnum)) "$name exposes textual/enum field $field"
        }
    }
}
Assert-Contract ($null -ne $hmi.GetMethod('ImportScreenOverview', [Type[]]@([IO.FileInfo], $importOptions))) 'HmiTarget imports the permanent-area singleton'
$slidein = Get-ApiType 'Siemens.Engineering.Hmi.Screen.ScreenSlidein'
Assert-Contract ($null -ne $slidein.GetProperty('SlideinType') -and $null -eq $slidein.GetProperty('Name')) 'Slide-in identity is its side/type, not a name'
if ($Version -eq 'V19') {
    foreach ($name in @('HmiUnified.UI.Screens.HmiScreen', 'HmiUnified.UI.Screens.HmiScreenComposition')) {
        $type = Get-ApiType "Siemens.Engineering.$name"
        Assert-Contract ($null -eq $export.Invoke($null, [object[]]@($type)) -and $null -eq $import.Invoke($null, [object[]]@($type))) "$name has no generic native transfer route in V19"
    }
    foreach ($name in @('HmiUnified.HmiTags.HmiTagComposition', 'HmiUnified.Scripts.HmiScriptModule', 'HmiUnified.Scripts.HmiScriptModuleComposition')) {
        $type = Get-ApiType "Siemens.Engineering.$name"
        $method = $export.Invoke($null, [object[]]@($type))
        Assert-Contract ($null -ne $method -and $method.GetParameters().Length -eq 1 -and $method.GetParameters()[0].ParameterType -eq [IO.DirectoryInfo]) "$name exports using native directory/default names"
        if ($name.EndsWith('Composition')) {
            $method = $import.Invoke($null, [object[]]@($type))
            Assert-Contract ($null -ne $method -and $method.GetParameters().Length -eq 1 -and $method.GetParameters()[0].ParameterType -eq [IO.DirectoryInfo]) "$name imports using native directory/default names"
        }
    }
}
$projectType = Get-ApiType 'Siemens.Engineering.Project'
$graphicsProperty = $projectType.GetProperty('Graphics')
if ($Version -eq 'V21') {
    $graphicsProvider = Get-ApiType 'Siemens.Engineering.Hmi.Globalization.GraphicsProvider'
    Assert-Contract ($null -eq $graphicsProperty -and $null -ne $graphicsProvider.GetProperty('Graphics')) 'Project graphics use the V21 GraphicsProvider service'
} else {
    Assert-Contract ($null -ne $graphicsProperty) 'Project graphics use the legacy project property'
}
Assert-Contract ($null -ne $server.GetType('SiemensMCPServer.NativeProjectResources', $true).GetMethod('Graphics')) 'GUI/MCP includes the shared project graphics adapter'
$unified = Get-ApiType 'Siemens.Engineering.HmiUnified.HmiSoftware' $false
if ($Version -in @('V16', 'V17', 'V18', 'V19')) {
    $screen = Get-ApiType 'Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen'
    $button = Get-ApiType 'Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton'
    Assert-Contract ($null -ne $unified.GetProperty('Screens') -and $null -ne $screen.GetProperty('ScreenItems')) 'Unified exposes screen and item compositions'
    Assert-Contract (($null -ne $unified.GetProperty('ScreenGroups')) -eq ($Version -in @('V18', 'V19'))) 'Unified flat/grouped screen layout matches the inspected profile'
    foreach ($name in @('Text', 'Left', 'Top', 'Width', 'Height', 'Dynamizations')) {
        Assert-Contract ($null -ne $button.GetProperty($name)) "Unified button exposes $name for the dedicated adapter"
    }
}
@{ version = $Version; apiVersion = $api.GetName().Version.ToString(); apiDirectory = $apiDirectory; contracts = $checked.Count; checks = $checked.ToArray(); crossReferences = $null -ne $referenceService; tiaPortalCreated = $false; projectAttached = $false; nativeWriteAcceptance = 'pending' } | ConvertTo-Json -Depth 5
