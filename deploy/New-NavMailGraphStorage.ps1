<#
.SYNOPSIS
    Optional helper: creates an Azure Storage account + blob container for NavMailGraph object storage and grants
    the app's service principal 'Storage Blob Data Contributor' on that ONE container.

.DESCRIPTION
    For the Entra mode of ObjectStorageClient (UseAzureBlobEntra): the DLL reuses the app's client-credentials
    token (scope https://storage.azure.com/.default), so no account key or SAS has to be stored in NAV. The least
    privileged built-in role for Put Blob / Put Block / Put Block List / Get Blob / Delete Blob is
    'Storage Blob Data Contributor'; this script assigns it at CONTAINER scope:
      /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/<account>/blobServices/default/containers/<container>

    Steps (Az PowerShell: Az.Accounts, Az.Resources, Az.Storage; all steps are idempotent):
      1. Connect-AzAccount (unless -SkipConnect), Set-AzContext -Subscription (if -SubscriptionId).
      2. Get-AzResourceGroup / New-AzResourceGroup (only created when -Location is given).
      3. Get-AzStorageAccount / New-AzStorageAccount -Kind StorageV2 -MinimumTlsVersion TLS1_2
         -AllowBlobPublicAccess $false -EnableHttpsTrafficOnly $true (-AllowSharedKeyAccess $false with
         -DisableSharedKeyAccess). An existing account is reused; weak settings are reported, not changed.
      4. Get-AzRmStorageContainer / New-AzRmStorageContainer -PublicAccess None (management plane: the admin
         needs no data-plane role and no account key).
      5. Get-AzADServicePrincipal -ApplicationId <AppId> -> object id of the Enterprise application.
      6. Get-AzRoleAssignment / New-AzRoleAssignment -ObjectId <sp> -RoleDefinitionName
         'Storage Blob Data Contributor' -Scope <container scope>; read back after creation.

    With -PrintOnly nothing is changed and no module or sign-in is needed: the script prints the equivalent
    Azure CLI (az) commands for bash.

    The script never reads, prints or returns an account key or SAS token. Shared Key / SAS modes
    (UseAzureBlobSharedKey / SAS URL) are configured separately (see docs/object-storage.md); GetDownloadUrl
    needs one of them, the Entra mode alone cannot create download links.

    Required Azure role of the signed-in admin: Contributor (or Owner) on the resource group for steps 2-4, and
    a role with Microsoft.Authorization/roleAssignments/write (Owner, User Access Administrator or Role Based
    Access Control Administrator) for step 6. Role assignments can take up to 10 minutes to become effective.

.PARAMETER ResourceGroupName
    Resource group of the storage account.

.PARAMETER StorageAccountName
    Storage account name (3-24 characters, lower-case letters and digits, globally unique).

.PARAMETER ContainerName
    Blob container name (3-63 characters, lower-case letters, digits and single hyphens). Default: nav-mail.

.PARAMETER Location
    Azure region (e.g. westeurope). Required only if the resource group or the storage account does not exist yet.

.PARAMETER AppId
    Application (client) id of the NavMailGraph app registration (New-NavMailGraphApp.ps1 prints it as ClientId).

.PARAMETER ServicePrincipalObjectId
    Object id of the Enterprise application. Optional; resolved from -AppId. If both are given they must match.

.PARAMETER SubscriptionId
    Subscription to use. Default: the subscription of the current Az context.

.PARAMETER SkuName
    Storage account SKU for a NEW account. Default: Standard_LRS.

.PARAMETER DisableSharedKeyAccess
    Create a NEW account with AllowSharedKeyAccess = $false (Entra only; Shared Key mode and service SAS download
    links of the add-in then do not work). Ignored for an existing account.

.PARAMETER PrintOnly
    Change nothing; print the equivalent az CLI commands.

.PARAMETER SkipConnect
    Use the existing Az PowerShell session (Get-AzContext).

.EXAMPLE
    ./New-NavMailGraphStorage.ps1 -ResourceGroupName rg-nav -StorageAccountName contosonavmail -Location westeurope `
        -ContainerName nav-mail -AppId 11111111-2222-3333-4444-555555555555 -WhatIf

.EXAMPLE
    ./New-NavMailGraphStorage.ps1 -ResourceGroupName rg-nav -StorageAccountName contosonavmail -Location westeurope `
        -AppId 11111111-2222-3333-4444-555555555555 -PrintOnly

    Prints the az CLI commands instead of running anything.

.OUTPUTS
    PSCustomObject with StorageAccountName, ContainerName, BlobEndpoint, ServicePrincipalObjectId, RoleAssignmentScope,
    RoleAssigned ($true / $false = WhatIf).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9]{3,24}$')]
    [string] $StorageAccountName,

    [ValidatePattern('^(?=.{3,63}$)[a-z0-9]+(-[a-z0-9]+)*$')]
    [string] $ContainerName = 'nav-mail',

    [string] $Location,

    [guid] $AppId,

    [guid] $ServicePrincipalObjectId,

    [guid] $SubscriptionId,

    [ValidateNotNullOrEmpty()]
    [string] $SkuName = 'Standard_LRS',

    [switch] $DisableSharedKeyAccess,

    [switch] $PrintOnly,

    [switch] $SkipConnect
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RoleName = 'Storage Blob Data Contributor'

function Get-Prop {
    # Safe property read under StrictMode (Az object models differ slightly between module versions).
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

# ---------------------------------------------------------------- -PrintOnly: az CLI equivalent
if ($PrintOnly) {
    $loc = if ($Location) { $Location } else { '<location, e.g. westeurope>' }
    $app = if ($AppId) { $AppId.ToString() } else { '<application (client) id>' }
    $lines = @('# Azure CLI equivalent (bash). Sign in first: az login', '')
    if ($SubscriptionId) { $lines += "az account set --subscription $SubscriptionId" }
    $lines += "az group create --name $ResourceGroupName --location $loc"
    $create = "az storage account create --name $StorageAccountName --resource-group $ResourceGroupName --location $loc --sku $SkuName --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false --https-only true"
    if ($DisableSharedKeyAccess) { $create += ' --allow-shared-key-access false' }
    $lines += $create
    $lines += "az storage container-rm create --storage-account $StorageAccountName --resource-group $ResourceGroupName --name $ContainerName --public-access off"
    if ($ServicePrincipalObjectId) {
        $lines += "SP_ID=$ServicePrincipalObjectId"
    }
    else {
        $lines += "SP_ID=`$(az ad sp show --id $app --query id -o tsv)"
    }
    $lines += "ACCOUNT_ID=`$(az storage account show --name $StorageAccountName --resource-group $ResourceGroupName --query id -o tsv)"
    $lines += "az role assignment create --role `"$RoleName`" --assignee-object-id `"`$SP_ID`" --assignee-principal-type ServicePrincipal --scope `"`$ACCOUNT_ID/blobServices/default/containers/$ContainerName`""
    $lines += ''
    $lines += '# Role assignments can take up to 10 minutes to become effective.'
    $lines | ForEach-Object { Write-Output $_ }
    return
}

# ---------------------------------------------------------------- prerequisites
if (-not $AppId -and -not $ServicePrincipalObjectId) {
    throw '-AppId (or -ServicePrincipalObjectId) is required: the role is assigned to the service principal of the NavMailGraph app.'
}
foreach ($m in 'Az.Accounts', 'Az.Resources', 'Az.Storage') {
    if (-not (Get-Module -ListAvailable -Name $m)) {
        throw "Module '$m' not found. Install it with: Install-Module Az.Accounts, Az.Resources, Az.Storage -Scope CurrentUser (or Install-Module Az), or use -PrintOnly for the az CLI commands."
    }
    Import-Module $m -ErrorAction Stop
}

# NOTE: -ErrorAction Stop is passed explicitly on every Az call (module functions do not inherit this script's
# $ErrorActionPreference).
if (-not $SkipConnect) {
    $connect = @{ ErrorAction = 'Stop' }
    if ($SubscriptionId) { $connect.Subscription = $SubscriptionId.ToString() }
    Write-Host 'Connecting to Azure ...'
    [void](Connect-AzAccount @connect)
}
elseif ($SubscriptionId) {
    [void](Set-AzContext -Subscription $SubscriptionId.ToString() -ErrorAction Stop)
}
$ctx = Get-AzContext -ErrorAction Stop
if (-not $ctx -or -not (Get-Prop $ctx 'Subscription')) {
    throw 'No Azure context. Run Connect-AzAccount (or run without -SkipConnect).'
}
Write-Host ("Subscription {0} ({1}), account {2}" -f (Get-Prop $ctx.Subscription 'Id'), (Get-Prop $ctx.Subscription 'Name'), (Get-Prop $ctx.Account 'Id'))

# ---------------------------------------------------------------- 1. service principal
if ($AppId) {
    $sp = @(Get-AzADServicePrincipal -ApplicationId $AppId -ErrorAction Stop) | Select-Object -First 1
    if (-not $sp) { throw "No service principal (Enterprise application) with AppId $AppId found. Run New-NavMailGraphApp.ps1 first." }
    if ($ServicePrincipalObjectId -and [guid](Get-Prop $sp 'Id') -ne $ServicePrincipalObjectId) {
        throw ("-ServicePrincipalObjectId {0} does not match the Enterprise application object id {1} of AppId {2}. Do not use the object id from the App registrations page." -f $ServicePrincipalObjectId, (Get-Prop $sp 'Id'), $AppId)
    }
    $spId = [string](Get-Prop $sp 'Id')
    Write-Host ("Service principal '{0}' object id {1}" -f (Get-Prop $sp 'DisplayName'), $spId)
}
else {
    $spId = $ServicePrincipalObjectId.ToString()
}

# ---------------------------------------------------------------- 2. resource group
$rg = Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue
if ($rg) {
    Write-Host ("Resource group '{0}' exists ({1})" -f $ResourceGroupName, (Get-Prop $rg 'Location'))
}
elseif (-not $Location) {
    throw "Resource group '$ResourceGroupName' not found. Pass -Location to create it."
}
elseif ($PSCmdlet.ShouldProcess($ResourceGroupName, "New-AzResourceGroup -Location $Location")) {
    [void](New-AzResourceGroup -Name $ResourceGroupName -Location $Location -ErrorAction Stop)
    Write-Host ("Created resource group '{0}'" -f $ResourceGroupName) -ForegroundColor Green
}

# ---------------------------------------------------------------- 3. storage account
$account = $null
if ($rg) { $account = Get-AzStorageAccount -ResourceGroupName $ResourceGroupName -Name $StorageAccountName -ErrorAction SilentlyContinue }
if ($account) {
    Write-Host ("Storage account '{0}' exists ({1}, {2})" -f $StorageAccountName, (Get-Prop $account 'Kind'), (Get-Prop $account 'PrimaryLocation'))
    if ((Get-Prop $account 'AllowBlobPublicAccess') -eq $true) {
        Write-Warning "Storage account '$StorageAccountName' allows anonymous blob access (AllowBlobPublicAccess = True). Consider: Set-AzStorageAccount -ResourceGroupName $ResourceGroupName -Name $StorageAccountName -AllowBlobPublicAccess `$false"
    }
    $tls = [string](Get-Prop $account 'MinimumTlsVersion')
    if ($tls -and $tls -notin @('TLS1_2', 'TLS1_3')) {
        Write-Warning "Storage account '$StorageAccountName' accepts $tls; TLS1_2 is recommended."
    }
}
else {
    if (-not $Location) { throw "Storage account '$StorageAccountName' not found in resource group '$ResourceGroupName'. Pass -Location to create it." }
    $newAccount = @{
        ResourceGroupName      = $ResourceGroupName
        Name                   = $StorageAccountName
        Location               = $Location
        SkuName                = $SkuName
        Kind                   = 'StorageV2'
        MinimumTlsVersion      = 'TLS1_2'
        AllowBlobPublicAccess  = $false
        EnableHttpsTrafficOnly = $true
        ErrorAction            = 'Stop'
    }
    if ($DisableSharedKeyAccess) { $newAccount.AllowSharedKeyAccess = $false }
    if ($PSCmdlet.ShouldProcess($StorageAccountName, "New-AzStorageAccount -SkuName $SkuName -Kind StorageV2 -MinimumTlsVersion TLS1_2 -AllowBlobPublicAccess `$false")) {
        [void](New-AzStorageAccount @newAccount)
        $account = Get-AzStorageAccount -ResourceGroupName $ResourceGroupName -Name $StorageAccountName -ErrorAction Stop
        Write-Host ("Created storage account '{0}'" -f $StorageAccountName) -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- 4. container
$containerExists = $false
if ($account) {
    try {
        $c = Get-AzRmStorageContainer -ResourceGroupName $ResourceGroupName -StorageAccountName $StorageAccountName -Name $ContainerName -ErrorAction Stop
        $containerExists = [bool]$c
    }
    catch {
        $containerExists = $false   # not found (any other problem surfaces again in New-AzRmStorageContainer)
    }
}
if ($containerExists) {
    Write-Host ("Container '{0}' exists" -f $ContainerName)
}
elseif ($PSCmdlet.ShouldProcess("$StorageAccountName/$ContainerName", 'New-AzRmStorageContainer -PublicAccess None')) {
    [void](New-AzRmStorageContainer -ResourceGroupName $ResourceGroupName -StorageAccountName $StorageAccountName -Name $ContainerName -PublicAccess None -ErrorAction Stop)
    [void](Get-AzRmStorageContainer -ResourceGroupName $ResourceGroupName -StorageAccountName $StorageAccountName -Name $ContainerName -ErrorAction Stop)
    Write-Host ("Created container '{0}'" -f $ContainerName) -ForegroundColor Green
}

# ---------------------------------------------------------------- 5. role assignment on the container
$subId = [string](Get-Prop $ctx.Subscription 'Id')
$accountId = if ($account) { [string](Get-Prop $account 'Id') } else { "/subscriptions/$subId/resourceGroups/$ResourceGroupName/providers/Microsoft.Storage/storageAccounts/$StorageAccountName" }
$scope = "$accountId/blobServices/default/containers/$ContainerName"
$assigned = $false

function Get-ExactAssignment {
    @(Get-AzRoleAssignment -ObjectId $spId -Scope $scope -RoleDefinitionName $RoleName -ErrorAction Stop |
            Where-Object { [string](Get-Prop $_ 'Scope') -eq $scope }) | Select-Object -First 1
}

if ($account) {
    # Effective assignments include those inherited from the account, resource group or subscription.
    $effective = @(Get-AzRoleAssignment -ObjectId $spId -Scope $scope -ErrorAction Stop)
    foreach ($e in $effective) {
        $eScope = [string](Get-Prop $e 'Scope')
        if ($eScope -ne $scope) {
            Write-Warning ("The service principal also has '{0}' at the broader scope {1}. Azure roles are additive; remove it if it is broader than intended." -f (Get-Prop $e 'RoleDefinitionName'), $eScope)
        }
    }
    if (Get-ExactAssignment) {
        Write-Host ("'{0}' is already assigned on container '{1}'" -f $RoleName, $ContainerName)
        $assigned = $true
    }
}
if (-not $assigned -and $PSCmdlet.ShouldProcess($scope, "New-AzRoleAssignment -ObjectId $spId -RoleDefinitionName '$RoleName'")) {
    [void](New-AzRoleAssignment -ObjectId $spId -RoleDefinitionName $RoleName -Scope $scope -ErrorAction Stop)
    if (-not (Get-ExactAssignment)) { throw "New-AzRoleAssignment returned without error, but Get-AzRoleAssignment does not list '$RoleName' on $scope." }
    Write-Host ("Assigned '{0}' on container '{1}'" -f $RoleName, $ContainerName) -ForegroundColor Green
    $assigned = $true
}

$blobEndpoint = $null
if ($account) {
    $endpoints = Get-Prop $account 'PrimaryEndpoints'
    $blobEndpoint = [string](Get-Prop $endpoints 'Blob')
}
if (-not $blobEndpoint) { $blobEndpoint = "https://$StorageAccountName.blob.core.windows.net/" }

Write-Host ''
Write-Host 'Note: role assignments can take up to 10 minutes to become effective (the add-in gets HTTP 403 until then).' -ForegroundColor Cyan
Write-Host ("Then in NAV (page 'Object Storage Profiles'): Azure Blob with Entra ID authentication (same app registration as the Graph connection), account '{0}', container '{1}'; use 'Test Connection'." -f $StorageAccountName, $ContainerName)

[pscustomobject]@{
    StorageAccountName       = $StorageAccountName
    ContainerName            = $ContainerName
    BlobEndpoint             = $blobEndpoint
    ServicePrincipalObjectId = $spId
    RoleAssignmentScope      = $scope
    RoleAssigned             = $assigned
}
