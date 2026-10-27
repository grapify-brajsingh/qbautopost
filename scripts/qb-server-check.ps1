#Requires -Version 5.1
<#
.SYNOPSIS
  T-609 helper: runs the manual QuickBooks check against a running QbAutopost API, one step at a time.

.DESCRIPTION
  Steps, in order: health -> sync -> dryrun -> post -> undo.
  Run it on the QuickBooks server, in the interactive session of the account QuickBooks runs under,
  with Company:FilePath pointing at a COPY of the company file.
  Every call goes to the versioned routes (/api/v1/...) and carries a key.
  The API key is read from the QBAUTOPOST__Api__ApiKey environment variable (or -ApiKey) and never printed.
  With per-caller keys (clients.json, scripts\new-api-client.ps1) the key needs the scopes
  health:read, qb:read, jobs:read, jobs:write and qb:post; the shared Api:ApiKey carries all of them
  while Api:AllowLegacyKey is true.

.EXAMPLE
  .\scripts\qb-server-check.ps1 -Step health
  .\scripts\qb-server-check.ps1 -Step dryrun -Folder C:\qb-jobs\2026-08-tropicana
  .\scripts\qb-server-check.ps1 -Step post -JobId 2026-08-tropicana -ConfirmCopy
  .\scripts\qb-server-check.ps1 -Step undo -BatchId '2026-08-tropicana#1' -ConfirmCopy
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('health', 'sync', 'dryrun', 'post', 'undo')]
    [string] $Step,
    [string] $BaseUrl = 'http://127.0.0.1:5080',
    [string] $ApiKey = $env:QBAUTOPOST__Api__ApiKey,
    [string] $Folder,
    [string] $JobId,
    [string] $BatchId,
    [switch] $ConfirmCopy,
    [switch] $Force,
    [int] $WaitSeconds = 900
)

$ErrorActionPreference = 'Stop'

function Show($value) {
    $value | ConvertTo-Json -Depth 8
}

function Invoke-Api([string] $Method, [string] $Path, $Body) {
    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        throw 'Set QBAUTOPOST__Api__ApiKey (or pass -ApiKey).'
    }

    $request = @{
        Method  = $Method
        Uri     = $BaseUrl.TrimEnd('/') + $Path
        Headers = @{ 'X-Api-Key' = $ApiKey }
    }
    if ($null -ne $Body) {
        $request.Body = ($Body | ConvertTo-Json -Depth 4)
        $request.ContentType = 'application/json'
    }

    Invoke-RestMethod @request
}

function Wait-Job([string] $Id) {
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ($true) {
        $view = Invoke-Api GET "/api/v1/jobs/$([uri]::EscapeDataString($Id))" $null
        if (@('queued', 'analysing', 'posting') -notcontains $view.status) {
            return $view
        }
        if ((Get-Date) -gt $deadline) {
            throw "Job $Id is still $($view.status) after $WaitSeconds s."
        }
        Start-Sleep -Seconds 2
    }
}

function Require-Copy {
    if (-not $ConfirmCopy) {
        throw 'This step changes the company file. Confirm Company:FilePath is a COPY, then add -ConfirmCopy.'
    }
}

switch ($Step) {
    'health' {
        # T-910/T-914: the QuickBooks health route opens a QuickBooks session, so it needs a key with the health:read scope.
        # Only /api/v1/health and /api/v1/health/ready answer without one. A 503 still carries the JSON body.
        try {
            Show (Invoke-Api GET '/api/v1/health/quickbooks' $null)
        }
        catch {
            Write-Warning "QuickBooks is not healthy: $($_.ErrorDetails.Message)"
            exit 1
        }
        Write-Host 'Record the API process bitness in the tracker (Task Manager > Details > Platform column).'
    }
    'sync' {
        Show (Invoke-Api POST '/api/v1/quickbooks/lists/sync' $null)
    }
    'dryrun' {
        if (-not $Folder) { throw 'Pass -Folder <absolute job folder>.' }
        $created = Invoke-Api POST '/api/v1/jobs' @{ folder = $Folder; dryRun = $true; force = [bool] $Force }
        $view = Wait-Job $created.jobId
        Show $view
        Write-Host "Review $Folder\output\analysis.json before posting."
    }
    'post' {
        Require-Copy
        if (-not $JobId) { throw 'Pass -JobId <job id in ready state>.' }
        [void] (Invoke-Api POST "/api/v1/jobs/$([uri]::EscapeDataString($JobId))/post" $null)
        $view = Wait-Job $JobId
        Show $view
        Write-Host "Batch: $($view.batchId). Check the transactions in QuickBooks, then undo with -Step undo -BatchId '$($view.batchId)'."
    }
    'undo' {
        Require-Copy
        if (-not $BatchId) { throw "Pass -BatchId '<jobId>#<attempt>'." }
        Show (Invoke-Api POST "/api/v1/batches/$([uri]::EscapeDataString($BatchId))/undo" $null)
    }
}
