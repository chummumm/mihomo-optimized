# Exercise the exact Windows PowerShell 5.1 encoder used by uia-query.ps1.
# Output contains only fixed fixtures; never read a window or user data here.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
try {
    Add-Type -Path (Join-Path $PSScriptRoot 'UiaResultJson.cs') -ReferencedAssemblies @(
        'System.dll', 'System.Core.dll', [System.Management.Automation.PSObject].Assembly.Location)
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

    Assert-Rejected ([System.Version]::new(1, 2, 3))
    Assert-Rejected ([double]::NaN)
    Assert-Rejected ([double]::PositiveInfinity)
    Assert-Rejected ([float]::NegativeInfinity)
    Assert-Rejected @{1 = 'nonstring-key'}
    $cycle = [System.Collections.ArrayList]::new()
    [void]$cycle.Add($cycle)
    Assert-Rejected $cycle
    Assert-Rejected ([string]::new([char]'x', 16 * 1024 * 1024))

    # The decimal separator must not follow the helper's current culture.
    [System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo('fr-FR')
    $text = [string]::new([char[]]@(0x4e2d, 0x6587, 0x22, 0x5c, 0, 9, 10, 13, 0xd83d, 0xde80, 0xd800, 0x78, 0xdc00))
    [psobject]$wrapped = 'wrapped'
    $wrapped.PSObject.Properties.Add([System.Management.Automation.PSNoteProperty]::new('ignored', [System.Version]::new(1, 2)))
    $nativeDictionary = [System.Collections.Generic.Dictionary[string, object]]::new()
    $nativeDictionary.Add('offsets', [int[]]@(0, 942))
    $nativeDictionary.Add('visible', $true)
    $elements = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 4096; $index++) {
        $elements.Add(@{id = 'row-' + $index; name = $text; value = 'value-' + $index})
    }
    $fixture = @{found = $true; text = $text; wrapped = $wrapped; empty = @(); missing = $null;
        flags = @($false, $true); numbers = @([long]::MinValue, [ulong]::MaxValue, 1.25, -0.5);
        native = $nativeDictionary; elements = $elements; rejected = 7}
    [Console]::Out.WriteLine([UiaResultJson]::Encode($fixture))
    [Console]::Out.Flush()
    [Environment]::Exit(0)
} catch {
    [Console]::Out.WriteLine('{"found":false,"error":"uia_json_contract_failed"}')
    [Console]::Out.Flush()
    [Environment]::Exit(1)
}
