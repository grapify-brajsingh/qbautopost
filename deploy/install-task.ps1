#Requires -Version 5.1
<#
.SYNOPSIS
  Registers (or removes) the Task Scheduler task that runs deploy/start-all.ps1 at logon (spec section 4, T-802).

.DESCRIPTION
  The task runs in the INTERACTIVE session of the auto-logon account, because the QuickBooks SDK needs a desktop
  session. It is never a Windows service, never "run whether the user is logged on or not", and stores no password.
  Run it once, as that account, from an elevated PowerShell (registering a logon trigger needs administrator rights).
  Use -WhatIf for a dry run: it prints the task it would register and changes nothing.

.EXAMPLE
  .\deploy\install-task.ps1 -WhatIf
  .\deploy\install-task.ps1
  .\deploy\install-task.ps1 -StartAllArguments '-DockerMode Desktop'
  .\deploy\install-task.ps1 -Unregister
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $TaskName = 'QbAutopost',
    [string] $UserId = ('{0}\{1}' -f $env:USERDOMAIN, $env:USERNAME),
    [string] $StartAllPath = '',
    [string] $StartAllArguments = '',
    [int] $DelaySeconds = 60,
    [switch] $Unregister
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is not set inside param() defaults when Windows PowerShell 5.1 runs a script with -File,
# so path defaults are resolved here.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $StartAllPath) { $StartAllPath = Join-Path $here 'start-all.ps1' }

if ($Unregister) {
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $existing) {
        Write-Host "No task named '$TaskName'."
        exit 0
    }
    if ($PSCmdlet.ShouldProcess($TaskName, 'Unregister scheduled task')) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Task '$TaskName' removed. A running API keeps running until it is stopped."
    }
    exit 0
}

$StartAllPath = [System.IO.Path]::GetFullPath($StartAllPath)
if (-not (Test-Path -LiteralPath $StartAllPath)) {
    Write-Error "start-all.ps1 not found at '$StartAllPath'."
    exit 1
}
if ($StartAllArguments -match '(?i)WhatIf') {
    Write-Error 'StartAllArguments must not contain -WhatIf: the task would never start anything.'
    exit 1
}

$arguments = ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File "{0}" {1}' -f $StartAllPath, $StartAllArguments).Trim()
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory (Split-Path -Parent $StartAllPath)

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserId
if ($DelaySeconds -gt 0) {
    # Give the desktop (and QuickBooks, if it is also started at logon) time to come up.
    $trigger.Delay = 'PT{0}S' -f $DelaySeconds
}

# Interactive: only while that user is logged on, in that user's desktop session. Limited: no elevation.
$principal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Limited

# No time limit (start-all.ps1 exits after starting the API, but a stop at the limit could end the API too);
# a second start while one is running is ignored.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

$description = 'QbAutopost: starts Hermes (Docker) and the QbAutopost API at logon in the interactive session. See docs/runbook.md section 2.4.'

Write-Host "Task      : $TaskName"
Write-Host "User      : $UserId (interactive logon, limited rights)"
Write-Host "Trigger   : at logon of $UserId, delay $DelaySeconds s"
Write-Host "Action    : powershell.exe $arguments"

if ($PSCmdlet.ShouldProcess($TaskName, "Register scheduled task at logon of $UserId")) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description $description -Force | Out-Null
    Write-Host "Task '$TaskName' registered. Test it now with: Start-ScheduledTask -TaskName '$TaskName'"
}
exit 0
