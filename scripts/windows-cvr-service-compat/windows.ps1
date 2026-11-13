param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Snapshot', 'Credentials', 'NormalizeCoreOwner')]
    [string] $Action,
    [string] $OwnerRoot,
    [uint32] $CoreProcessId = 0,
    [string] $CorePath,
    [string] $CoreSHA256
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

if ($Action -eq 'NormalizeCoreOwner') {
    # The official installer creates Windows files with create_new and the
    # caller's default owner. GitHub's elevated runner can assign its user SID,
    # which the production service refuses even inside its protected core dir.
    # Normalize only this fresh, hash-attested fixture file; never grant access.
    $stateRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'clash-verge-service'
    $coreDirectory = Join-Path $stateRoot 'cores'
    if ([string]::IsNullOrEmpty($CorePath) -or -not [IO.Path]::IsPathFullyQualified($CorePath) -or
        $CorePath -match '(^|[\\/])\.\.?([\\/]|$)' -or $CoreSHA256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Owner normalization requires an absolute fixture path and its SHA-256'
    }
    $coreName = [IO.Path]::GetFileName($CorePath)
    $expectedPath = Join-Path $coreDirectory $coreName
    if ($coreName -cnotmatch '^mihomo-cvr-compat-[0-9a-f]{16}\.exe$' -or
        -not [string]::Equals([IO.Path]::GetFullPath($CorePath), $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Owner normalization is restricted to the unique approved test core'
    }

    # Reject reparse points at the leaf and every ancestor, including ProgramData.
    $componentPath = $expectedPath
    while (-not [string]::IsNullOrEmpty($componentPath)) {
        $component = Get-Item -LiteralPath $componentPath -Force
        if (($component.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Core ownership path contains a reparse point: $componentPath"
        }
        if (($componentPath -eq $expectedPath) -eq $component.PSIsContainer) {
            throw "Core ownership path has the wrong file type: $componentPath"
        }
        $componentPath = [IO.Path]::GetDirectoryName($componentPath)
    }

    $trustedOwners = @(
        'S-1-5-18', 'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
    )
    $accessSection = [Security.AccessControl.AccessControlSections]::Access
    function Assert-TrustedAccessRules($Security, [string] $Label) {
        $rules = @($Security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
        $seen = @{}
        foreach ($rule in $rules) {
            $principal = $rule.IdentityReference.Value
            if ($principal -notin @('S-1-5-18', 'S-1-5-32-544') -or $seen.ContainsKey($principal) -or
                $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
                $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl) {
                throw "$Label has an unexpected access rule"
            }
            $seen[$principal] = $true
        }
        if ($seen.Count -ne 2) {
            throw "$Label must retain full access for SYSTEM and Administrators only"
        }
    }

    $parents = @()
    foreach ($parentPath in @($stateRoot, $coreDirectory)) {
        $parentSecurity = Get-Acl -LiteralPath $parentPath
        $parentOwner = $parentSecurity.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($parentOwner -notin $trustedOwners -or -not $parentSecurity.AreAccessRulesProtected) {
            throw "Official core parent is not protected by a trusted owner: $parentPath"
        }
        Assert-TrustedAccessRules $parentSecurity 'Official core parent'
        $parents += @{ path = $parentPath; ownerSid = $parentOwner
                       accessSddl = $parentSecurity.GetSecurityDescriptorSddlForm($accessSection) }
    }
    $beforeHash = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($beforeHash -cne $CoreSHA256) {
        throw 'Approved core hash changed before owner normalization'
    }
    $fileSecurity = Get-Acl -LiteralPath $expectedPath
    Assert-TrustedAccessRules $fileSecurity 'Approved test core'
    $beforeOwner = $fileSecurity.GetOwner([Security.Principal.SecurityIdentifier]).Value
    $beforeAccess = $fileSecurity.GetSecurityDescriptorSddlForm($accessSection)
    $installerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $changed = $false
    $reason = 'official installer already assigned a service-trusted owner'
    if ($beforeOwner -notin $trustedOwners) {
        if ($beforeOwner -ne $installerSid) {
            throw "Approved core has unexpected owner $beforeOwner; installer user is $installerSid"
        }
        $fileSecurity.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        Set-Acl -LiteralPath $expectedPath -AclObject $fileSecurity
        $changed = $true
        $reason = 'fresh installer-created core had the runner user as default owner; assigned Builtin Administrators without changing access rules'
    }
    $afterSecurity = Get-Acl -LiteralPath $expectedPath
    $afterOwner = $afterSecurity.GetOwner([Security.Principal.SecurityIdentifier]).Value
    $afterAccess = $afterSecurity.GetSecurityDescriptorSddlForm($accessSection)
    $afterHash = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedOwner = if ($changed) { 'S-1-5-32-544' } else { $beforeOwner }
    if ($afterOwner -ne $expectedOwner -or $afterAccess -cne $beforeAccess -or $afterHash -cne $beforeHash) {
        throw 'Core owner normalization did not preserve the expected owner, access DACL and bytes'
    }
    @{
        path = $expectedPath; installerSid = $installerSid; ownerNormalizedForRunner = $changed
        reason = $reason; parents = $parents
        before = @{ ownerSid = $beforeOwner; accessSddl = $beforeAccess; sha256 = $beforeHash }
        after = @{ ownerSid = $afterOwner; accessSddl = $afterAccess; sha256 = $afterHash }
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
