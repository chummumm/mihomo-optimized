param(
    [Parameter(Mandatory=$true)][int]$ApplicationProcessId,
    [Parameter(Mandatory=$true)][string]$AutomationId,
    [ValidateSet('read', 'invoke', 'set', 'select', 'toggle', 'scroll')][string]$Action = 'read',
    [AllowEmptyString()][string]$Value = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$processCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ApplicationProcessId)
$windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children, $processCondition)
if ($windows.Count -eq 0) { @{found=$false} | ConvertTo-Json -Compress; exit 0 }
if ($AutomationId -eq '*') {
    $visible = @()
    foreach ($window in $windows) {
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
    }
    @{found=$true; elements=$visible} | ConvertTo-Json -Compress -Depth 4
    exit 0
}
$idCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
$element = $null
foreach ($window in $windows) {
    $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $idCondition)
    if ($element) { break }
}
if (-not $element) { @{found=$false} | ConvertTo-Json -Compress; exit 0 }
$result = @{ found=$true; enabled=$element.Current.IsEnabled; offscreen=$element.Current.IsOffscreen;
    name=$element.Current.Name; focused=$element.Current.HasKeyboardFocus;
    controlType=$element.Current.ControlType.ProgrammaticName }
switch ($Action) {
    'invoke' {
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
        $result.invoked = $true
    }
    'set' {
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $pattern.SetValue($Value)
        $result.changed = $true
    }
    'toggle' {
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $pattern.Toggle()
        $result.toggled = $true
    }
    'select' {
        if ($Value -eq '') {
            $pattern = $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $pattern.Select()
        } else {
            $expand = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) {
                $expand.Expand()
            }
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, $Value)
            $choice = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if (-not $choice) { throw 'The requested option is not present.' }
            $pattern = $choice.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $pattern.Select()
            if ($expand) { $expand.Collapse() }
        }
        $result.selected = $true
    }
    'scroll' {
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $percent = [double]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture)
        if ($percent -lt 0 -or $percent -gt 100) { throw 'Scroll percent is out of range.' }
        $pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $percent)
        $result.scrolled = $true
    }
    default {
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            $result.value = $pattern.Current.Value
            $result.readOnly = $pattern.Current.IsReadOnly
        }
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$pattern)) {
            $result.selection = @($pattern.Current.GetSelection() | ForEach-Object { $_.Current.Name })
        }
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
            $result.toggleState = $pattern.Current.ToggleState.ToString()
        }
    }
}
$result | ConvertTo-Json -Compress -Depth 4
