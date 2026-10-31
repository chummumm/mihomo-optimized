param([Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int]$InterfaceIndex)
$ErrorActionPreference = 'Stop'
# This helper can remove only the one synthetic route on the smoke-owned TUN.
# Never use wildcard names, a default route, or an interface chosen by position.
$adapters = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.ifIndex -eq $InterfaceIndex -and $_.Name -ceq 'MihomoSmokeTun' })
if ($adapters.Count -ne 1) { throw 'smoke_tun_identity_mismatch' }
$identity = $adapters[0].InterfaceGuid
$routes = @(Get-NetRoute -AddressFamily IPv4 -InterfaceIndex $InterfaceIndex -DestinationPrefix '198.18.77.77/32' -PolicyStore ActiveStore)
if ($routes.Count -ne 1) { throw 'smoke_owned_route_not_unique' }
$current = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.ifIndex -eq $InterfaceIndex -and $_.Name -ceq 'MihomoSmokeTun' -and $_.InterfaceGuid -eq $identity })
if ($current.Count -ne 1) { throw 'smoke_tun_identity_changed' }
$routes[0] | Remove-NetRoute -Confirm:$false
@{ removed = $true; interfaceIndex = $InterfaceIndex; prefix = '198.18.77.77/32' } | ConvertTo-Json -Compress
