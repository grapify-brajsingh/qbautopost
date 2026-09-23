#Requires -Version 5.1
<#
.SYNOPSIS
  T-803 shadow week helper: compares a dry run (output\analysis.json) with what a person entered by hand.

.DESCRIPTION
  Read-only. Nothing is sent to QuickBooks, Hermes or the API.

  -Manual is a CSV the operator prepares from QuickBooks for the same statement period (for example a
  "Transaction Detail by Account" report exported to Excel and saved as CSV), with these columns:
      Date      yyyy-MM-dd or MM/dd/yyyy
      Amount    positive number, as on the statement (no currency sign needed)
      Payee     vendor / customer name as entered (blank when none)
      Account   the expense / income / transfer account of the line (what QbAutopost calls lineAccount)
      Source    optional: the bank or card account the transaction is in (what QbAutopost calls account)

  Each analysis line is matched to one manual row with the same date and amount (payee breaks ties).
  Result per line (written to -OutFile, default output\shadow-diff.csv next to analysis.json):
      agree               tool would post it exactly as entered by hand
      account-differs     same transaction, different Account (or Source)
      payee-differs       same transaction, different Payee
      not-in-manual       tool would post it, nobody entered it
      skipped-but-entered tool skips it (e.g. card payment), a person entered it
      held                tool holds it (teach a rule, runbook section 6); a person entered it
      held-not-entered    tool holds it, nobody entered it
      manual-only         entered by hand, not in the dry run at all
  Names compare ignoring case and surrounding spaces. Amounts compare exactly (decimal).

  Exit codes: 0 no disagreements (held lines are listed but are not disagreements); 3 disagreements found;
  1 bad input. Plan section 6: the shadow week ends after two consecutive runs with no disagreements.

.EXAMPLE
  .\scripts\shadow-diff.ps1 -Analysis C:\qb-jobs\2026-09-acme\output\analysis.json -Manual C:\shadow\2026-09-acme-manual.csv
#>
param(
    [Parameter(Mandatory = $true)] [string] $Analysis,
    [Parameter(Mandatory = $true)] [string] $Manual,
    [string] $OutFile = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$inv = [System.Globalization.CultureInfo]::InvariantCulture

function ConvertTo-Day {
    param([string] $Text, [string] $Where)
    $formats = [string[]] @('yyyy-MM-dd', 'MM/dd/yyyy', 'M/d/yyyy')
    $parsed = [datetime]::MinValue
    if ([datetime]::TryParseExact($Text.Trim(), $formats, $inv, [System.Globalization.DateTimeStyles]::None, [ref] $parsed)) {
        return $parsed.ToString('yyyy-MM-dd', $inv)
    }
    throw "$Where : date '$Text' is not yyyy-MM-dd or MM/dd/yyyy."
}

function ConvertTo-Money {
    param([string] $Text, [string] $Where)
    $clean = $Text.Trim().Replace('$', '').Replace(',', '')
    $value = [decimal]::Zero
    if ([decimal]::TryParse($clean, [System.Globalization.NumberStyles]::Number, $inv, [ref] $value)) {
        return [Math]::Abs($value).ToString('0.00', $inv)
    }
    throw "$Where : amount '$Text' is not a number."
}

function Get-Name {
    param($Value)
    if ($null -eq $Value) { return '' }
    return ([string] $Value).Trim()
}

function Test-SameName {
    param([string] $A, [string] $B)
    return [string]::Equals($A, $B, [StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-Path -LiteralPath $Analysis)) { Write-Error "analysis.json not found: $Analysis"; exit 1 }
if (-not (Test-Path -LiteralPath $Manual)) { Write-Error "manual CSV not found: $Manual"; exit 1 }
if (-not $OutFile) { $OutFile = Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($Analysis))) 'shadow-diff.csv' }

try {
    $doc = Get-Content -LiteralPath $Analysis -Raw -Encoding UTF8 | ConvertFrom-Json
    $manualRows = @(Import-Csv -LiteralPath $Manual -Encoding UTF8)
    foreach ($column in 'Date', 'Amount', 'Payee', 'Account') {
        if ($manualRows.Count -gt 0 -and -not ($manualRows[0].PSObject.Properties.Name -contains $column)) {
            throw "manual CSV has no '$column' column (needs Date, Amount, Payee, Account; Source optional)."
        }
    }

    $manualItems = New-Object System.Collections.ArrayList
    $rowNo = 1
    foreach ($row in $manualRows) {
        $rowNo++
        $source = ''
        if ($row.PSObject.Properties.Name -contains 'Source') { $source = Get-Name $row.Source }
        [void] $manualItems.Add([pscustomobject] @{
            Row     = $rowNo
            Date    = ConvertTo-Day -Text $row.Date -Where "manual row $rowNo"
            Amount  = ConvertTo-Money -Text $row.Amount -Where "manual row $rowNo"
            Payee   = Get-Name $row.Payee
            Account = Get-Name $row.Account
            Source  = $source
            Used    = $false
        })
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}

$results = New-Object System.Collections.ArrayList
foreach ($line in @($doc.lines)) {
    # PowerShell 7 turns ISO date strings in JSON into DateTime; 5.1 keeps the string.
    $dateText = if ($line.date -is [datetime]) { $line.date.ToString('yyyy-MM-dd', $inv) } else { [string] $line.date }
    $day = ConvertTo-Day -Text $dateText -Where "analysis line $($line.requestId)"
    $amount = ([decimal] $line.amount).ToString('0.00', $inv)
    $toolPayee = Get-Name $line.payee
    $toolAccount = Get-Name $line.lineAccount
    $toolSource = Get-Name $line.account

    $candidates = @($manualItems | Where-Object { -not $_.Used -and $_.Date -eq $day -and $_.Amount -eq $amount })
    $match = $null
    if ($candidates.Count -gt 0) {
        $match = @($candidates | Where-Object { Test-SameName $_.Payee $toolPayee }) + $candidates | Select-Object -First 1
        $match.Used = $true
    }

    $details = New-Object System.Collections.ArrayList
    switch ([string] $line.decision) {
        'post' {
            if ($null -eq $match) { $status = 'not-in-manual' }
            else {
                $status = 'agree'
                if (-not (Test-SameName $match.Payee $toolPayee)) { $status = 'payee-differs'; [void] $details.Add("payee tool='$toolPayee' manual='$($match.Payee)'") }
                if (-not (Test-SameName $match.Account $toolAccount)) { $status = 'account-differs'; [void] $details.Add("account tool='$toolAccount' manual='$($match.Account)'") }
                if ($match.Source -and -not (Test-SameName $match.Source $toolSource)) { $status = 'account-differs'; [void] $details.Add("source tool='$toolSource' manual='$($match.Source)'") }
            }
        }
        'skip' { if ($null -eq $match) { $status = 'agree' } else { $status = 'skipped-but-entered' } }
        default { if ($null -eq $match) { $status = 'held-not-entered' } else { $status = 'held' } }
    }
    if ([string] $line.decision -ne 'post' -and $line.reason) { [void] $details.Add("reason=$($line.reason)") }

    [void] $results.Add([pscustomobject] @{
        Status       = $status
        Date         = $day
        Amount       = $amount
        Kind         = [string] $line.kind
        Description  = [string] $line.description
        ToolSource   = $toolSource
        ToolPayee    = $toolPayee
        ToolAccount  = $toolAccount
        ManualRow    = $(if ($match) { $match.Row } else { '' })
        ManualPayee  = $(if ($match) { $match.Payee } else { '' })
        ManualAccount = $(if ($match) { $match.Account } else { '' })
        RequestId    = [string] $line.requestId
        Detail       = ($details -join '; ')
    })
}

foreach ($item in @($manualItems | Where-Object { -not $_.Used })) {
    [void] $results.Add([pscustomobject] @{
        Status = 'manual-only'; Date = $item.Date; Amount = $item.Amount; Kind = ''; Description = ''
        ToolSource = ''; ToolPayee = ''; ToolAccount = ''; ManualRow = $item.Row; ManualPayee = $item.Payee
        ManualAccount = $item.Account; RequestId = ''; Detail = 'not in the dry run (other file, other period, or not on the statement?)'
    })
}

$results | Export-Csv -LiteralPath $OutFile -NoTypeInformation -Encoding UTF8

$disagreeing = @('account-differs', 'payee-differs', 'not-in-manual', 'skipped-but-entered', 'manual-only')
$counts = $results | Group-Object Status | Sort-Object Name
Write-Host "Job $($doc.jobId): $($results.Count) rows -> $OutFile"
foreach ($group in $counts) { Write-Host ('  {0,-20} {1}' -f $group.Name, $group.Count) }
$bad = @($results | Where-Object { $disagreeing -contains $_.Status }).Count
if ($bad -gt 0) {
    Write-Host "$bad disagreement(s). Teach rules for real mapping differences (POST /api/v1/rules/alias, POST /api/v1/rules/account) and log them in docs/tracker.md (T-803)."
    exit 3
}
Write-Host 'No disagreements.'
exit 0
