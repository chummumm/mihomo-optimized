param([Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$InterfaceIndex)
$ErrorActionPreference = 'Stop'
# This fixture may remove only its explicitly configured test address. Never
# select an adapter by position or remove addresses from an underlying NIC.
$adapters = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.ifIndex -eq $InterfaceIndex -and $_.Name -ceq 'MihomoSmokeTun' })
if ($adapters.Count -ne 1) { throw 'smoke_tun_identity_mismatch' }
$identity = $adapters[0].InterfaceGuid
$addresses = @(Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $InterfaceIndex -IPAddress '198.18.0.1' -PolicyStore ActiveStore)
if ($addresses.Count -ne 1 -or $addresses[0].PrefixLength -ne 30) { throw 'smoke_owned_address_not_unique' }
$current = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.ifIndex -eq $InterfaceIndex -and $_.Name -ceq 'MihomoSmokeTun' -and $_.InterfaceGuid -eq $identity })
if ($current.Count -ne 1) { throw 'smoke_tun_identity_changed' }
$addresses[0] | Remove-NetIPAddress -Confirm:$false
@{ removed = $true; interfaceIndex = $InterfaceIndex; address = '198.18.0.1' } | ConvertTo-Json -Compress
