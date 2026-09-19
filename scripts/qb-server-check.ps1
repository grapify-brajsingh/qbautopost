#Requires -Version 5.1
<#
.SYNOPSIS
  T-609 helper: runs the manual QuickBooks check against a running QbAutopost API, one step at a time.

.DESCRIPTION
  Steps, in order: health -> sync -> dryrun -> post -> undo.
  Run it on the QuickBooks server, in the interactive session of the account QuickBooks runs under,
  with Company:FilePath pointing at a COPY of the company file.
  The API key is read from the QBAUTOPOST__Api__ApiKey environment variable (or -ApiKey) and never printed.

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
        $view = Invoke-Api GET "/jobs/$([uri]::EscapeDataString($Id))" $null
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
        # No API key needed; a 503 still carries the JSON body with the reason.
        try {
            Show (Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + '/health/quickbooks'))
        }
        catch {
            Write-Warning "QuickBooks is not healthy: $($_.ErrorDetails.Message)"
            exit 1
        }
        Write-Host 'Record the API process bitness in the tracker (Task Manager > Details > Platform column).'
    }
    'sync' {
        Show (Invoke-Api POST '/qb/sync-lists' $null)
    }
    'dryrun' {
        if (-not $Folder) { throw 'Pass -Folder <absolute job folder>.' }
        $created = Invoke-Api POST '/jobs' @{ folder = $Folder; dryRun = $true; force = [bool] $Force }
        $view = Wait-Job $created.jobId
        Show $view
        Write-Host "Review $Folder\output\analysis.json before posting."
    }
    'post' {
        Require-Copy
        if (-not $JobId) { throw 'Pass -JobId <job id in ready state>.' }
        [void] (Invoke-Api POST "/jobs/$([uri]::EscapeDataString($JobId))/post" $null)
        $view = Wait-Job $JobId
        Show $view
        Write-Host "Batch: $($view.batchId). Check the transactions in QuickBooks, then undo with -Step undo -BatchId '$($view.batchId)'."
    }
    'undo' {
        Require-Copy
        if (-not $BatchId) { throw "Pass -BatchId '<jobId>#<attempt>'." }
        Show (Invoke-Api POST "/batches/$([uri]::EscapeDataString($BatchId))/undo" $null)
    }
}
