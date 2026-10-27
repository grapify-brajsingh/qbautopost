<#
.SYNOPSIS
    Issues an API caller key for QbAutopost (api-v1 FR-A-13, task T-910).

.DESCRIPTION
    Generates a random key, stores only its salted SHA-256 hash in clients.json, and prints the key ONCE.
    The key itself is never written to the file or to a log: if it is lost, issue a new one with -Rotate
    rather than trying to recover it.

    The hash is SHA-256 over salt || key, base64 - the same computation as ApiClientStore.Hash in the app.
    That is the one place this script duplicates product code; change both together.

.PARAMETER Id
    The caller's id, e.g. acme-erp. Letters, digits, dot, dash and underscore.

.PARAMETER Name
    A human label for the caller, e.g. "Acme ERP".

.PARAMETER Scopes
    Scopes to grant, from: health:read, qb:read, qb:post, qb:post:ai, qb:debug, jobs:read, jobs:write,
    rules:write, admin. Grant the fewest that let the caller do its job.

.PARAMETER AllowedCidrs
    Optional networks the caller may connect from, e.g. 10.0.0.0/24. Empty means anywhere.

.PARAMETER ClientsFile
    Path to clients.json. Defaults to the file beside the repository root, as the API resolves it.

.PARAMETER Rotate
    Issue a new key for an existing caller, keeping the old one working for -RotateHours (default 24)
    so the caller can switch over without downtime.

.EXAMPLE
    .\new-api-client.ps1 -Id acme-erp -Name "Acme ERP" -Scopes qb:read,qb:post

.EXAMPLE
    .\new-api-client.ps1 -Id acme-erp -Rotate -RotateHours 48
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Id,
    [string]$Name,
    [string[]]$Scopes = @('qb:read'),
    [string[]]$AllowedCidrs = @(),
    [string]$ClientsFile,
    [switch]$Rotate,
    [int]$RotateHours = 24
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validScopes = @('health:read', 'qb:read', 'qb:post', 'qb:post:ai', 'qb:debug', 'jobs:read', 'jobs:write', 'rules:write', 'admin')
foreach ($scope in $Scopes) {
    if ($validScopes -notcontains $scope) {
        throw "Unknown scope '$scope'. Valid scopes: $($validScopes -join ', ')"
    }
}

if ($Id -notmatch '^[A-Za-z0-9._-]{1,64}$') {
    throw "Id '$Id' must be 1 to 64 characters of letters, digits, dot, dash or underscore."
}

if (-not $ClientsFile) {
    $ClientsFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'clients.json'
}

function New-RandomBytes {
    param([int]$Count)
    $buffer = New-Object 'System.Byte[]' $Count
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return $buffer
}

function Get-KeyHash {
    param([string]$Key, [string]$SaltBase64)
    $salt = [Convert]::FromBase64String($SaltBase64)
    $keyBytes = [System.Text.Encoding]::UTF8.GetBytes($Key)
    $buffer = New-Object 'System.Byte[]' ($salt.Length + $keyBytes.Length)
    [Array]::Copy($salt, 0, $buffer, 0, $salt.Length)
    [Array]::Copy($keyBytes, 0, $buffer, $salt.Length, $keyBytes.Length)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToBase64String($sha.ComputeHash($buffer)) } finally { $sha.Dispose() }
}

$list = [ordered]@{ clients = @() }
if (Test-Path $ClientsFile) {
    $existing = Get-Content -Path $ClientsFile -Raw | ConvertFrom-Json
    if ($existing -and ($existing.PSObject.Properties.Name -contains 'clients') -and $existing.clients) {
        $list.clients = @($existing.clients)
    }
}

$current = $list.clients | Where-Object { $_.id -eq $Id } | Select-Object -First 1
if ($Rotate -and -not $current) { throw "No client '$Id' to rotate. Run without -Rotate to create one." }
if (-not $Rotate -and $current) { throw "Client '$Id' already exists. Use -Rotate to issue a new key for it." }

$key = [Convert]::ToBase64String((New-RandomBytes -Count 32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$salt = [Convert]::ToBase64String((New-RandomBytes -Count 16))
$hash = Get-KeyHash -Key $key -SaltBase64 $salt
$nowUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

if ($Rotate) {
    $until = (Get-Date).ToUniversalTime().AddHours($RotateHours).ToString('yyyy-MM-ddTHH:mm:ssZ')
    $action = "rotate the key for client '$Id' (old key valid until $until)"
} else {
    $action = "create client '$Id' with scopes $($Scopes -join ', ')"
}

if (-not $PSCmdlet.ShouldProcess($ClientsFile, $action)) { return }

if ($Rotate) {
    $current.previousKeyHash = $current.keyHash
    $current.previousKeySalt = $current.keySalt
    $current.previousExpiresUtc = $until
    $current.keyHash = $hash
    $current.keySalt = $salt
} else {
    $label = $Id
    if ($Name) { $label = $Name }
    $client = New-Object PSObject -Property ([ordered]@{
        id                 = $Id
        name               = $label
        keyHash            = $hash
        keySalt            = $salt
        scopes             = @($Scopes)
        enabled            = $true
        createdUtc         = $nowUtc
        expiresUtc         = $null
        allowedCidrs       = @($AllowedCidrs)
    })
    $list.clients = @($list.clients) + $client
}

$json = (New-Object PSObject -Property $list) | ConvertTo-Json -Depth 6
Set-Content -Path $ClientsFile -Value $json -Encoding utf8

Write-Host ""
Write-Host "Client : $Id"
Write-Host "Scopes : $($Scopes -join ', ')"
Write-Host "File   : $ClientsFile"
Write-Host ""
Write-Host "API key (shown once, copy it now):"
Write-Host "  $key"
Write-Host ""
Write-Host "Send it as the X-Api-Key header. It is not stored anywhere and cannot be recovered;"
Write-Host "if it is lost, run this script again with -Rotate."
