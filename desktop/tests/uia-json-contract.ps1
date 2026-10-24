# Exercise the exact Windows PowerShell 5.1 encoder used by uia-query.ps1.
# Output contains only fixed fixtures; never read a window or user data here.
$ErrorActionPreference = 'Stop'
$script:stageClock = [System.Diagnostics.Stopwatch]::StartNew()
$script:stage = 'initialize'
function Set-ContractStage {
    param([ValidateSet('initialize', 'resolve_encoder', 'compile', 'compiled', 'reject_object', 'reject_nonfinite',
        'reject_key', 'reject_cycle', 'reject_output_size', 'create_text', 'wrap_string',
        'create_dictionary', 'create_tree', 'serialize', 'write_result', 'flush_result',
        'exit_process')][string]$Stage)
    $script:stage = $Stage
    # Keep a killed process diagnosable without using its encoder or PowerShell
    # object formatting. These markers contain fixed stages and elapsed time only.
    $milliseconds = $script:stageClock.ElapsedMilliseconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    [Console]::Error.WriteLine('MIHOMO_UIA_STAGE:' + $Stage + ':' + $milliseconds)
    [Console]::Error.Flush()
}
Set-ContractStage 'initialize'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
try {
    Set-ContractStage 'resolve_encoder'
    $encoderPath = Join-Path $PSScriptRoot 'UiaResultJson.cs'
    Set-ContractStage 'compile'
    Add-Type -Path $encoderPath -ReferencedAssemblies @(
        'System.dll', 'System.Core.dll', [System.Management.Automation.PSObject].Assembly.Location)
    Set-ContractStage 'compiled'
    function Assert-Rejected {
        param([object]$Value)
        try { $null = [UiaResultJson]::Encode($Value) }
        catch {
            $exception = $_.Exception
            while ($exception.InnerException) { $exception = $exception.InnerException }
            if ($exception -is [System.InvalidOperationException]) { return }
            throw
        }
        throw 'The encoder accepted an unsupported fixture.'
    }

    Set-ContractStage 'reject_object'
    Assert-Rejected ([System.Version]::new(1, 2, 3))
    Set-ContractStage 'reject_nonfinite'
    Assert-Rejected ([double]::NaN)
    Assert-Rejected ([double]::PositiveInfinity)
    Assert-Rejected ([float]::NegativeInfinity)
    Set-ContractStage 'reject_key'
    Assert-Rejected @{1 = 'nonstring-key'}
    Set-ContractStage 'reject_cycle'
    $cycle = [System.Collections.ArrayList]::new()
    [void]$cycle.Add($cycle)
    Assert-Rejected $cycle
    Set-ContractStage 'reject_output_size'
    Assert-Rejected ([string]::new([char]'x', 16 * 1024 * 1024))

    # The decimal separator must not follow the helper's current culture.
    Set-ContractStage 'create_text'
    [System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo('fr-FR')
    $text = [string]::new([char[]]@(0x4e2d, 0x6587, 0x22, 0x5c, 0, 9, 10, 13, 0xd83d, 0xde80, 0xd800, 0x78, 0xdc00))
    Set-ContractStage 'wrap_string'
    [psobject]$wrapped = 'wrapped'
    $wrapped.PSObject.Properties.Add([System.Management.Automation.PSNoteProperty]::new('ignored', [System.Version]::new(1, 2)))
    Set-ContractStage 'create_dictionary'
    $nativeDictionary = [System.Collections.Generic.Dictionary[string, object]]::new()
    $nativeDictionary.Add('offsets', [int[]]@(0, 942))
    $nativeDictionary.Add('visible', $true)
    Set-ContractStage 'create_tree'
    $elements = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 4096; $index++) {
        $elements.Add(@{id = 'row-' + $index; name = $text; value = 'value-' + $index})
    }
    $fixture = @{found = $true; text = $text; wrapped = $wrapped; empty = @(); missing = $null;
        flags = @($false, $true); numbers = @([System.Int64]::MinValue, [System.UInt64]::MaxValue, 1.25, -0.5);
        native = $nativeDictionary; elements = $elements; rejected = 7}
    Set-ContractStage 'serialize'
    $json = [UiaResultJson]::Encode($fixture)
    Set-ContractStage 'write_result'
    [Console]::Out.WriteLine($json)
    Set-ContractStage 'flush_result'
    [Console]::Out.Flush()
    Set-ContractStage 'exit_process'
    [Environment]::Exit(0)
} catch {
    $failureRecord = $_
    $exception = $_.Exception
    while ($exception.InnerException) { $exception = $exception.InnerException }
    $type = $exception.GetType().Name
    $hresult = '0x{0:X8}' -f ([long]$exception.HResult -band 0xFFFFFFFFL)
    $line = [int]$_.InvocationInfo.ScriptLineNumber
    # This self-test reads no UI or user data. Preserve bounded compiler/binder
    # details here only; the real UIA client never emits exception messages.
    $detail = ($failureRecord.ToString() + ' | ' + $exception.Message).Replace($PSScriptRoot, '<tests>')
    if ($detail.Length -gt 2048) { $detail = $detail.Substring(0, 2048) }
    $errorId = [string]$failureRecord.FullyQualifiedErrorId
    if ($errorId.Length -gt 256) { $errorId = $errorId.Substring(0, 256) }
    [Console]::Error.WriteLine('MIHOMO_UIA_JSON_ERROR:' + $detail)
    [Console]::Error.WriteLine('MIHOMO_UIA_JSON_ERROR_ID:' + $errorId)
    [Console]::Error.Flush()
    # The JSON result contains only fixed fixture stages, exception types,
    # numeric codes and source lines. Do not use the failing encoder for errors.
    [Console]::Out.WriteLine('{"found":false,"error":"uia_json_contract_failed","stage":"' + $stage +
        '","exception":"' + $type + '","hresult":"' + $hresult + '","line":' + $line + '}')
    [Console]::Out.Flush()
    [Environment]::Exit(1)
}
