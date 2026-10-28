#Requires -Version 5.1
<#
.SYNOPSIS
  Installs (or rolls back) the dashboard's static files that the API serves from Dashboard:Path (T-1117).

.DESCRIPTION
  Copies the built dashboard (-Source, the dist folder) to 'dashboard.new' beside the target, checks it has an
  index.html, then renames: the current folder becomes 'dashboard.old' and 'dashboard.new' becomes 'dashboard'.
  The API reads the folder on every request, so an install into a folder that already exists is picked up at once.
  If the folder did not exist when the API started, restart the API (the startup check ran once): stop it and run
  start-all.ps1 or the scheduled task.
  -Rollback swaps 'dashboard.old' back in. Use -WhatIf for a dry run. It changes nothing but the dashboard folders:
  no service, firewall rule, certificate or setting.

.EXAMPLE
  .\deploy\install-dashboard.ps1 -Source D:\release\dashboard\dist -WhatIf
  .\deploy\install-dashboard.ps1 -Source D:\release\dashboard\dist
  .\deploy\install-dashboard.ps1 -Rollback
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Source = '',
    [string] $Target = 'C:\qb-autopost\dashboard',
    [switch] $Rollback
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Target = [System.IO.Path]::GetFullPath($Target.TrimEnd([char]92))
$leaf = Split-Path -Leaf $Target
$parent = Split-Path -Parent $Target
$newLeaf = $leaf + '.new'
$oldLeaf = $leaf + '.old'
$failedLeaf = $leaf + '.failed'
$newDir = Join-Path $parent $newLeaf
$oldDir = Join-Path $parent $oldLeaf
$failedDir = Join-Path $parent $failedLeaf

if ($Rollback) {
    if (-not (Test-Path -LiteralPath $oldDir)) {
        Write-Error "No previous version at '$oldDir' to roll back to."
        exit 1
    }
    if ($PSCmdlet.ShouldProcess($Target, "Roll back: '$oldDir' becomes '$Target'")) {
        if (Test-Path -LiteralPath $failedDir) { Remove-Item -LiteralPath $failedDir -Recurse -Force }
        if (Test-Path -LiteralPath $Target) { Rename-Item -LiteralPath $Target -NewName $failedLeaf }
        Rename-Item -LiteralPath $oldDir -NewName $leaf
        Write-Host "Rolled back. The version that was live is kept as '$failedDir'."
    }
    else {
        Write-Host "Dry run: would rename '$Target' to '$failedLeaf' and '$oldDir' to '$Target'. Nothing was changed."
    }
    exit 0
}

if (-not $Source) {
    Write-Error 'Give -Source <dist folder> (or -Rollback).'
    exit 1
}
$Source = [System.IO.Path]::GetFullPath($Source)
if (-not (Test-Path -LiteralPath (Join-Path $Source 'index.html'))) {
    Write-Error "'$Source' has no index.html: that is not a built dashboard (point -Source at the dist folder)."
    exit 1
}

Write-Host "Source : $Source"
Write-Host "Target : $Target (previous version kept as $oldLeaf)"

if ($PSCmdlet.ShouldProcess($Target, "Install dashboard from '$Source' through '$newDir'")) {
    if (Test-Path -LiteralPath $newDir) { Remove-Item -LiteralPath $newDir -Recurse -Force }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $newDir -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $newDir 'index.html'))) {
        Remove-Item -LiteralPath $newDir -Recurse -Force
        Write-Error 'The copy has no index.html; nothing was installed.'
        exit 1
    }

    $hadCurrent = Test-Path -LiteralPath $Target
    if ($hadCurrent) {
        if (Test-Path -LiteralPath $oldDir) { Remove-Item -LiteralPath $oldDir -Recurse -Force }
        Rename-Item -LiteralPath $Target -NewName $oldLeaf
    }
    try {
        Rename-Item -LiteralPath $newDir -NewName $leaf
    }
    catch {
        if ($hadCurrent) { Rename-Item -LiteralPath $oldDir -NewName $leaf }
        throw
    }
    Write-Host "Installed '$Target'."
    if (-not $hadCurrent) {
        Write-Host 'The folder did not exist before: if the API was already running, restart it (stop it, then start-all.ps1 or the scheduled task).'
    }
    Write-Host 'Done.'
}
else {
    Write-Host "Dry run: would copy '$Source' to '$newDir', rename '$Target' to '$oldLeaf', then '$newDir' to '$Target'. Nothing was changed."
}
exit 0
