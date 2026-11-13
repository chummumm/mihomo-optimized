param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Snapshot', 'Credentials')]
    [string] $Action,
    [string] $OwnerRoot,
    [uint32] $CoreProcessId = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

if ($Action -eq 'Snapshot') {
    # Match the official service's SHGetKnownFolderPath(FOLDERID_ProgramData).
    $stateRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'clash-verge-service'
    $registered = Get-CimInstance -ClassName Win32_Service -Filter "Name='clash_verge_service'"
    $serviceView = $null
    if ($null -ne $registered) {
        $serviceView = @{
            name = $registered.Name
            state = $registered.State
            pid = [uint32] $registered.ProcessId
            path = $registered.PathName
            account = $registered.StartName
        }
    }
    $processView = $null
    if ($CoreProcessId -ne 0) {
        $observed = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$CoreProcessId"
        if ($null -ne $observed) {
            $processView = @{
                pid = [uint32] $observed.ProcessId
                parentPid = [uint32] $observed.ParentProcessId
                path = $observed.ExecutablePath
                commandLine = $observed.CommandLine
            }
        }
    }
    @{
        root = $stateRoot
        rootExists = Test-Path -LiteralPath $stateRoot
        service = $serviceView
        coreProcess = $processView
    } | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

# The caller supplies a fresh, isolated directory. Never change a profile or
# application directory to make credentials acceptable to the service.
if ([string]::IsNullOrEmpty($OwnerRoot) -or -not [IO.Path]::IsPathRooted($OwnerRoot)) {
    throw 'Credentials require an absolute fresh owner directory'
}
if (Test-Path -LiteralPath $OwnerRoot) {
    throw 'Owner directory already exists'
}
$null = New-Item -ItemType Directory -Path $OwnerRoot
$ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$descriptor = "O:${ownerSid}D:P(A;;FA;;;${ownerSid})(A;;FA;;;SY)(A;;FA;;;BA)"
$directorySecurity = [Security.AccessControl.DirectorySecurity]::new()
$directorySecurity.SetSecurityDescriptorSddlForm($descriptor)
Set-Acl -LiteralPath $OwnerRoot -AclObject $directorySecurity

$ownerToken = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$tokenPath = Join-Path $OwnerRoot '.clash-verge-service-owner-token'
[IO.File]::WriteAllBytes($tokenPath, $ownerToken)
$fileSecurity = [Security.AccessControl.FileSecurity]::new()
$fileSecurity.SetSecurityDescriptorSddlForm($descriptor)
Set-Acl -LiteralPath $tokenPath -AclObject $fileSecurity

# Captured only by the Python parent; neither this response nor the token file
# is included in the acceptance evidence.
@{
    identity = @{ Windows = @{ sid = $ownerSid } }
    app_data_dir = $OwnerRoot
    token = [Convert]::ToHexString($ownerToken).ToLowerInvariant()
} | ConvertTo-Json -Depth 4 -Compress
