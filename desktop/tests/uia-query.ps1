param(
    [Parameter(Mandatory=$true)][int]$ApplicationProcessId,
    [Parameter(Mandatory=$true)][string]$AutomationId,
    [ValidateSet('read', 'invoke', 'set', 'select', 'toggle', 'scroll', 'scroll-horizontal', 'text-start', 'text-end', 'text-viewport', 'context', 'escape')][string]$Action = 'read',
    [AllowEmptyString()][string]$Value = ''
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$script:operationStage = 'initialize'
$script:textProbeActive = $false
trap {
    # Keep CI diagnostics useful without printing control values, YAML or URLs.
    $exception = $_.Exception
    while ($exception.InnerException) { $exception = $exception.InnerException }
    if ($script:textProbeActive) { $script:operationStage = [NativeTextViewportProbe]::Stage }
    @{found=$false; error='uia_operation_failed'; stage=$script:operationStage;
        exception=$exception.GetType().Name;
        hresult=('0x{0:X8}' -f ([long]$exception.HResult -band 0xFFFFFFFFL))} | ConvertTo-Json -Compress
    exit 1
}
if ($Action -eq 'text-viewport') {
    # This dedicated probe uses native UIA3 COM from one MTA apartment. Keep
    # all ordinary reads and actor actions on their existing, proven path.
    $script:operationStage = 'read_text_uia3_compile'
    if ($AutomationId -ne 'ConfigEditor') { throw 'The text viewport probe requires the config editor.' }
    Add-Type -Path (Join-Path $PSScriptRoot 'UiaTextViewportProbe.cs')
    $script:textProbeActive = $true
    $result = [NativeTextViewportProbe]::Read($ApplicationProcessId, $AutomationId)
    $script:textProbeActive = $false
    $result | ConvertTo-Json -Compress -Depth 6
    exit 0
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$processCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ApplicationProcessId)
if ($AutomationId -eq '*') { $script:operationStage = 'visible_list_windows' }
$windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children, $processCondition)
if ($windows.Count -eq 0) { @{found=$false} | ConvertTo-Json -Compress; exit 0 }
if ($AutomationId -eq '*') {
    # Fetch the complete raw subtree with its read-only properties in one
    # UIA cache request per window. Walking live peers and reading Current
    # separately lets a refreshed WPF child disappear between those calls.
    # None makes accidental follow-up remote reads impossible; actors below
    # still use their original live elements and execute only once.
    $cache = [System.Windows.Automation.CacheRequest]::new()
    $cache.TreeScope = [System.Windows.Automation.TreeScope]::Element
    $cache.TreeFilter = [System.Windows.Automation.Automation]::RawViewCondition
    $cache.AutomationElementMode = [System.Windows.Automation.AutomationElementMode]::None
    $cache.Add([System.Windows.Automation.AutomationElement]::AutomationIdProperty)
    $cache.Add([System.Windows.Automation.AutomationElement]::NameProperty)
    $cache.Add([System.Windows.Automation.AutomationElement]::IsOffscreenProperty)
    $cache.Add([System.Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty)
    $cache.Add([System.Windows.Automation.ValuePattern]::ValueProperty)
    $visible = [System.Collections.Generic.List[object]]::new()
    foreach ($window in $windows) {
        $script:operationStage = 'visible_snapshot'
        $activeCache = $cache.Activate()
        try {
            $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Subtree,
                [System.Windows.Automation.Condition]::TrueCondition)
        } finally { $activeCache.Dispose() }
        $script:operationStage = 'visible_materialize'
        foreach ($item in $elements) {
            $properties = $item.Cached
            if ($properties.IsOffscreen) { continue }
            $row = @{id=$properties.AutomationId; name=$properties.Name}
            $hasValue = $item.GetCachedPropertyValue(
                [System.Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty)
            if ($hasValue -isnot [bool]) { throw 'The visible element has no cached value-pattern availability.' }
            if ($hasValue) {
                $cachedValue = $item.GetCachedPropertyValue([System.Windows.Automation.ValuePattern]::ValueProperty, $true)
                if ($cachedValue -isnot [string]) { throw 'The visible value control has no cached string value.' }
                $row.value = $cachedValue
            }
            $visible.Add($row)
        }
    }
    @{found=$true; elements=$visible.ToArray()} | ConvertTo-Json -Compress -Depth 4
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
$script:operationStage = $Action
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
            $script:operationStage = 'select_expand'
            $expand = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) {
                $expand.Expand()
            }
            $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, $Value)
            $selectionCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)
            $condition = [System.Windows.Automation.AndCondition]::new($nameCondition, $selectionCondition)
            # Expanding the popup schedules item containers for layout. Wait for
            # the selectable item, rather than accidentally matching its text child.
            $script:operationStage = 'select_find_option'
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            do {
                $choice = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($choice) { break }
                Start-Sleep -Milliseconds 50
            } while ([DateTime]::UtcNow -lt $deadline)
            if (-not $choice) { throw [System.InvalidOperationException]::new('The requested option is not present.') }
            $script:operationStage = 'select_option'
            $pattern = $choice.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $pattern.Select()
            # Language/theme changes can regenerate item peers. Obtain the
            # current combo peer before closing the popup; do not reuse the item.
            $script:operationStage = 'select_collapse'
            foreach ($window in $windows) {
                $current = $window.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $idCondition)
                if (-not $current) { continue }
                $currentExpand = $null
                if ($current.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$currentExpand)) {
                    $currentExpand.Collapse()
                }
                break
            }
        }
        $result.selected = $true
    }
    'context' {
        $script:operationStage = 'context_focus'
        $element.SetFocus()
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeMenuInput {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
}
'@
        $ancestor = $element
        while ($ancestor -and -not $ancestor.Current.NativeWindowHandle) { $ancestor = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($ancestor) }
        if (-not $ancestor -or $ancestor.Current.ProcessId -ne $ApplicationProcessId) { throw 'The focused element has no owned window.' }
        $handle = [IntPtr]$ancestor.Current.NativeWindowHandle
        # WPF PopupControlService opens a keyboard context menu on Key.Apps
        # KeyUp. Posting WM_CONTEXTMENU directly does not enter that input path.
        # Route the actual menu key to the focused application's HWND, with its
        # scan code, extended-key flag, repeat count and key-up transition bits.
        $script:operationStage = 'context_input'
        $scan = [NativeMenuInput]::MapVirtualKey(0x5D, 0)
        if (-not $scan) { throw 'The context menu key is not mapped.' }
        $down = (([long]$scan -band 0xFF) -shl 16) -bor 0x01000001L
        $up = $down -bor 0xC0000000L
        if (-not [NativeMenuInput]::PostMessage($handle, 0x0100, [IntPtr]0x5D, [IntPtr]$down) -or
            -not [NativeMenuInput]::PostMessage($handle, 0x0101, [IntPtr]0x5D, [IntPtr]$up)) { throw 'Context menu input failed.' }
        $script:operationStage = 'context_open'
        $menuCondition = [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Menu),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsOffscreenProperty, $false))
        $menu = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(3)
        do {
            # ContextMenu has a separate popup HWND, created after input. Do
            # not reuse the top-level window snapshot from before the keypress.
            $menuWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children, $processCondition)
            foreach ($window in $menuWindows) {
                $menu = $window.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $menuCondition)
                if ($menu) { break }
            }
            if ($menu) { break }
            Start-Sleep -Milliseconds 50
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $menu) { throw 'The requested context menu did not open.' }
        $result.opened = $true
    }
    'escape' {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeEscapeInput {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
        $ancestor = $element
        while ($ancestor -and -not $ancestor.Current.NativeWindowHandle) { $ancestor = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($ancestor) }
        if (-not $ancestor -or $ancestor.Current.ProcessId -ne $ApplicationProcessId) { throw 'The focused element has no owned window.' }
        $handle = [IntPtr]$ancestor.Current.NativeWindowHandle
        [void][NativeEscapeInput]::PostMessage($handle, 0x0100, [IntPtr]0x1B, [IntPtr]0)
        [void][NativeEscapeInput]::PostMessage($handle, 0x0101, [IntPtr]0x1B, [IntPtr]0xC0000001L)
        $result.sent = $true
    }
    'scroll' {
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $percent = [double]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture)
        if ($percent -lt 0 -or $percent -gt 100) { throw 'Scroll percent is out of range.' }
        $pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $percent)
        $result.scrolled = $true
    }
    'scroll-horizontal' {
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $percent = [double]::Parse($Value, [System.Globalization.CultureInfo]::InvariantCulture)
        if ($percent -lt 0 -or $percent -gt 100) { throw 'Scroll percent is out of range.' }
        $pattern.SetScrollPercent($percent, [System.Windows.Automation.ScrollPattern]::NoScroll)
        $result.scrolled = $true
    }
    { $_ -eq 'text-start' -or $_ -eq 'text-end' } {
        $element.SetFocus()
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $document = $pattern.DocumentRange
        $range = $document.Clone()
        $start = [System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start
        $end = [System.Windows.Automation.Text.TextPatternRangeEndpoint]::End
        if ($Action -eq 'text-start') { $range.MoveEndpointByRange($end, $document, $start) }
        else { $range.MoveEndpointByRange($start, $document, $end) }
        # This enters the native caret/BringIntoView path independently of the
        # ScrollPattern. A caret need not expose all trailing document padding.
        $range.Select()
        $range.ScrollIntoView(($Action -eq 'text-start'))
        $result.selected = $true
    }
    default {
        $pattern = $null
        $script:operationStage = 'read_value'
        if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            $result.value = $pattern.Current.Value
            $result.readOnly = $pattern.Current.IsReadOnly
        }
        $pattern = $null
        $script:operationStage = 'read_selection'
        if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern, [ref]$pattern)) {
            $result.selection = @($pattern.Current.GetSelection() | ForEach-Object { $_.Current.Name })
        }
        $pattern = $null
        $script:operationStage = 'read_toggle'
        if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
            $result.toggleState = $pattern.Current.ToggleState.ToString()
        }
        $pattern = $null
        $script:operationStage = 'read_scroll'
        if ($element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) {
            $result.scroll = @{ horizontalScrollable=$pattern.Current.HorizontallyScrollable;
                horizontalPercent=$pattern.Current.HorizontalScrollPercent;
                horizontalViewSize=$pattern.Current.HorizontalViewSize;
                verticalScrollable=$pattern.Current.VerticallyScrollable;
                verticalPercent=$pattern.Current.VerticalScrollPercent }
        }
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataGrid) {
            $bounds = $element.Current.BoundingRectangle
            $result.bounds = @{ x=$bounds.X; y=$bounds.Y; width=$bounds.Width; height=$bounds.Height }
            $pattern = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern, [ref]$pattern)) {
                $result.rowCount = $pattern.Current.RowCount
            }
            $headerCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::HeaderItem)
            $headers = @()
            foreach ($header in $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $headerCondition)) {
                $rect = $header.Current.BoundingRectangle
                if ($rect.IsEmpty) { continue }
                $headers += @{ name=$header.Current.Name; offscreen=$header.Current.IsOffscreen;
                    x=$rect.X; y=$rect.Y; width=$rect.Width; height=$rect.Height }
            }
            $result.headers = $headers
        }
    }
}
$result | ConvertTo-Json -Compress -Depth 4
