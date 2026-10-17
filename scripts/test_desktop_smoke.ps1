param(
    [Parameter(Mandatory = $true)][string]$DesktopDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('V15.1', 'V19')][string]$Version,
    [string]$ScreenshotPath
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell/.NET Framework, STA. No process enumeration, attachment or native writes.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$directory = (Resolve-Path -LiteralPath $DesktopDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $directory 'SiemensMCPServer.exe'))
$publicStatic = [Reflection.BindingFlags]'Public,Static'
$privateInstance = [Reflection.BindingFlags]'NonPublic,Instance'
$resolver = $assembly.GetType('SiemensMCPServer.TiaPortalAssemblyResolver', $true)
$resolver.GetMethod('Initialize', $publicStatic).Invoke($null, @($Version))
$formType = $assembly.GetType('SiemensMCPServer.MainForm', $true)
$form = [Activator]::CreateInstance($formType, $privateInstance, $null, [object[]]@($false), $null)
$checks = [Collections.Generic.List[string]]::new()
function Assert-Ui([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Desktop smoke failed: $Description" }
    $checks.Add($Description)
}
function Field([string]$Name) { $formType.GetField($Name, $privateInstance).GetValue($form) }
function Own-Visible($Control) {
    [Windows.Forms.Control].GetMethod('GetState', $privateInstance).Invoke($Control, @([int]2))
}
try {
    $form.CreateControl()
    Assert-Ui ($form.Text.Contains('HMI / PLC')) 'Window identifies HMI and PLC support'
    Assert-Ui (-not (Field '_saveProjectButton').Enabled) 'Save is disabled without an attachment'
    Assert-Ui (-not (Field '_exportCompleteButton').Enabled) 'Classic transfers are disabled without a target'
    $panel = Field '_engineeringPanel'
    $panelFlags = [Reflection.BindingFlags]'NonPublic,Instance'
    foreach ($name in @('exportSelected', 'exportAll', 'importNative', 'importBundle')) {
        Assert-Ui (-not $panel.GetType().GetField($name, $panelFlags).GetValue($panel).Enabled) "$name disabled without an engineering target"
    }
    $targetType = $assembly.GetType('SiemensMCPServer.SoftwareReference', $true)
    foreach ($family in @('unified', 'plc')) {
        # Synthetic family-only reference tests presentation, never native handles or operations.
        $target = [Activator]::CreateInstance($targetType, $true)
        $targetType.GetProperty('Family').SetValue($target, $family, $null)
        $formType.GetField('_activeTarget', $privateInstance).SetValue($form, $target)
        $formType.GetMethod('UpdateActionState', $privateInstance).Invoke($form, $null)
        Assert-Ui (Own-Visible $panel) "$family uses the engineering resource panel"
        Assert-Ui (-not (Own-Visible (Field '_importCompleteButton'))) "$family hides classic Complete HMI import"
        Assert-Ui (-not (Own-Visible (Field '_screenTree'))) "$family hides the classic screen tree"
    }
    $formType.GetField('_activeTarget', $privateInstance).SetValue($form, $null)
    $formType.GetMethod('UpdateActionState', $privateInstance).Invoke($form, $null)
    Assert-Ui (Own-Visible (Field '_exportCompleteButton')) 'Classic controls remain available'
    Assert-Ui (-not (Own-Visible $panel)) 'Classic view hides the engineering resource panel'
    if ($ScreenshotPath) {
        # Render an invisible test window so child controls participate in layout/painting.
        $form.ShowInTaskbar = $false
        $form.Opacity = 0
        $form.Show()
        $form.PerformLayout()
        $bitmap = [Drawing.Bitmap]::new($form.Width, $form.Height)
        try { $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $form.Width, $form.Height)); $bitmap.Save($ScreenshotPath) }
        finally { $bitmap.Dispose() }
    }
    @{ version = $Version; checks = $checks.ToArray(); count = $checks.Count; projectAttached = $false; nativeWrites = $false } | ConvertTo-Json -Depth 4
}
finally { $form.Dispose() }
