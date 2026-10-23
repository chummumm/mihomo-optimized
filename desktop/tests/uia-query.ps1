param(
    [Parameter(Mandatory=$true)][int]$ApplicationProcessId,
    [Parameter(Mandatory=$true)][string]$AutomationId,
    [ValidateSet('read', 'invoke')][string]$Action = 'read'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$processCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ApplicationProcessId)
$window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [System.Windows.Automation.TreeScope]::Children, $processCondition)
if (-not $window) { @{found=$false} | ConvertTo-Json -Compress; exit 0 }
if ($AutomationId -eq '*') {
    $visible = @()
    $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Subtree,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($item in $elements) {
        if ($item.Current.IsOffscreen) { continue }
        $row = @{id=$item.Current.AutomationId; name=$item.Current.Name}
        $pattern = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            $row.value = $pattern.Current.Value
        }
        $visible += $row
    }
    @{found=$true; elements=$visible} | ConvertTo-Json -Compress -Depth 4
    exit 0
}
$idCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
$element = $window.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $idCondition)
if (-not $element) { @{found=$false} | ConvertTo-Json -Compress; exit 0 }
$result = @{ found=$true; enabled=$element.Current.IsEnabled; offscreen=$element.Current.IsOffscreen;
    name=$element.Current.Name; controlType=$element.Current.ControlType.ProgrammaticName }
if ($Action -eq 'invoke') {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    $result.invoked = $true
} else {
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        $result.value = $pattern.Current.Value
        $result.readOnly = $pattern.Current.IsReadOnly
    }
}
$result | ConvertTo-Json -Compress -Depth 3
