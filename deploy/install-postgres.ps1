#Requires -Version 5.1
<#
.SYNOPSIS
  Installs PostgreSQL 16 on the QuickBooks server for saved requests (saved-requests spec 5.4, task T-1016).

.DESCRIPTION
  Run once, from an ELEVATED PowerShell, on the QuickBooks server. It
    1. installs the pinned PostgreSQL 16 for Windows silently as a service (-InstallerPath or -InstallerUrl, and the
       installer's SHA-256 from the vendor's download page: the script refuses a file that does not match). With -SkipInstall
       it uses a PostgreSQL 16 that is already there;
    2. binds the service to 127.0.0.1 only (listen_addresses), so no remote access and no database TLS is needed;
    3. creates database qbautopost and the roles qbautopost_owner (runs migrations) and qbautopost_app (the API);
    4. writes the generated role passwords ONLY to machine environment variables:
         QBAUTOPOST__Database__ConnectionString  (app role; the API reads it)
         QBAUTOPOST_MIGRATION_CONNECTION         (owner role; apply-migrations.ps1 and backup-postgres.ps1 read it)
       and, if it is absent, generates QBAUTOPOST__Auth__Jwt__SigningKey. Nothing is written to a file or the console;
    5. runs deploy\apply-migrations.ps1 (migrations.sql, then grants.sql: the app role gets SELECT, INSERT, UPDATE on the
       mutable tables, SELECT and INSERT only on saved_request_versions, saved_request_events and user_audit, and DELETE
       only on user_roles);
    6. registers the daily Task Scheduler task QbAutopostPostgresBackup (backup-postgres.ps1, as SYSTEM, no password stored).
  The PostgreSQL superuser password is the one you pass in -SuperuserPassword. It is NOT stored anywhere: keep it in your
  password manager. (The installer itself is given a throwaway password that is replaced before the script ends.)
  Safe to run again: existing roles, database and variables are kept; add -RotatePasswords to issue new role passwords.
  Use -WhatIf for a dry run: it prints every step and changes nothing.

  Machine environment variables are read by processes started AFTER they are set: restart the API (and open a new
  PowerShell window) after this script.

.PARAMETER InstallerPath
  Path of the EDB PostgreSQL 16 Windows x64 installer (postgresql-16.x-N-windows-x64.exe), already downloaded.

.PARAMETER InstallerUrl
  https URL of that installer. Use InstallerPath on a server without internet access.

.PARAMETER InstallerSha256
  The installer's SHA-256 as published on the download page. Required unless -SkipInstall.

.PARAMETER SuperuserPassword
  A SecureString for the postgres superuser, e.g. (Read-Host -AsSecureString 'postgres password').

.PARAMETER BackupFolder
  QuickBooks:BackupFolder. The dumps go to <BackupFolder>\postgres. Required unless -SkipBackupTask.

.EXAMPLE
  .\deploy\install-postgres.ps1 -InstallerPath D:\setup\postgresql-16.4-1-windows-x64.exe -InstallerSha256 <hash> -SuperuserPassword (Read-Host -AsSecureString 'postgres password') -BackupFolder D:\qb-backups -WhatIf
  .\deploy\install-postgres.ps1 -SkipInstall -SuperuserPassword (Read-Host -AsSecureString 'postgres password') -BackupFolder D:\qb-backups -RotatePasswords
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallerPath = '',
    [string] $InstallerUrl = '',
    [string] $InstallerSha256 = '',
    [Parameter(Mandatory = $true)][System.Security.SecureString] $SuperuserPassword,
    [string] $BackupFolder = '',
    [string] $BackupTime = '02:00',
    [string] $BackupTaskName = 'QbAutopostPostgresBackup',
    [string] $InstallDir = '',
    [string] $DataDir = '',
    [string] $ServiceName = 'postgresql-x64-16',
    [int] $Port = 5432,
    [string] $PgBin = '',
    [switch] $SkipInstall,
    [switch] $SkipMigrations,
    [switch] $SkipBackupTask,
    [switch] $RotatePasswords
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is not set inside param() defaults when Windows PowerShell 5.1 runs a script with -File.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'pg-common.ps1')
if (-not $InstallDir) { $InstallDir = Join-Path $env:ProgramFiles 'PostgreSQL\16' }
if (-not $DataDir) { $DataDir = Join-Path $InstallDir 'data' }
if (-not $PgBin) { $PgBin = Join-Path $InstallDir 'bin' }

$DatabaseName = 'qbautopost'
$OwnerRole = 'qbautopost_owner'
$AppRole = 'qbautopost_app'
$AppVariable = 'QBAUTOPOST__Database__ConnectionString'
$OwnerVariable = 'QBAUTOPOST_MIGRATION_CONNECTION'
$JwtVariable = 'QBAUTOPOST__Auth__Jwt__SigningKey'

function Say([string] $Message, [string] $Level = 'INF') { Write-LogLine -Tag 'install-postgres' -Message $Message -Level $Level }

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-PlainText([System.Security.SecureString] $Secure) {
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function ConvertTo-SqlLiteral([string] $Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function Wait-PostgresPort([int] $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $connect = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
            if ($connect.AsyncWaitHandle.WaitOne(2000)) {
                $client.EndConnect($connect)
                return $true
            }
        }
        catch { }
        finally { $client.Close() }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Get-RolePassword([string] $Variable, [string] $Role) {
    # Keeps the password already issued (so a re-run does not break a running API) unless -RotatePasswords.
    if (-not $RotatePasswords) {
        $existing = [Environment]::GetEnvironmentVariable($Variable, 'Machine')
        if ($existing) {
            $parsed = ConvertFrom-NpgsqlConnection -ConnectionString $existing
            if ($parsed.Username -eq $Role -and $parsed.Password) { return $parsed.Password }
        }
    }
    return New-RandomSecret -Length 32
}

function Set-MachineVariable([string] $Name, [string] $Value) {
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Machine')
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function Get-ConnectionString([string] $Role, [string] $Password) {
    return 'Host=127.0.0.1;Port={0};Database={1};Username={2};Password={3}' -f $Port, $DatabaseName, $Role, $Password
}

# ---- checks that change nothing ------------------------------------------------------------------------------

if (-not $WhatIfPreference -and -not (Test-Elevated)) {
    Say 'Run this from an elevated PowerShell (it installs a service and writes machine environment variables).' 'ERR'
    exit 1
}
if ($Port -lt 1 -or $Port -gt 65535) {
    Say "Port $Port is not a valid port." 'ERR'
    exit 1
}
if (-not $SkipBackupTask -and [string]::IsNullOrWhiteSpace($BackupFolder)) {
    Say 'Pass -BackupFolder (QuickBooks:BackupFolder), or -SkipBackupTask.' 'ERR'
    exit 1
}
if (-not $SkipInstall) {
    if (-not $InstallerPath -and -not $InstallerUrl) {
        Say 'Pass -InstallerPath or -InstallerUrl (or -SkipInstall if PostgreSQL 16 is already installed).' 'ERR'
        exit 1
    }
    if ($InstallerPath -and $InstallerUrl) {
        Say 'Pass -InstallerPath or -InstallerUrl, not both.' 'ERR'
        exit 1
    }
    if ($InstallerUrl -and -not $InstallerUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
        Say 'InstallerUrl must be https.' 'ERR'
        exit 1
    }
    if ($InstallerSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        Say 'Pass -InstallerSha256: the 64-hex SHA-256 from the vendor download page. The script will not run an unchecked installer.' 'ERR'
        exit 1
    }
    if ($InstallerPath -and -not (Test-Path -LiteralPath $InstallerPath)) {
        Say "Installer '$InstallerPath' not found." 'ERR'
        exit 1
    }
}
foreach ($file in 'migrations.sql', 'grants.sql', 'apply-migrations.ps1', 'backup-postgres.ps1') {
    if (-not (Test-Path -LiteralPath (Join-Path $here $file))) {
        Say "deploy\$file not found next to this script." 'ERR'
        exit 1
    }
}

$serviceExists = [bool] (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
Say ("Plan: database '{0}' on 127.0.0.1:{1}, service '{2}', install={3}, migrations={4}, backup task={5}." -f $DatabaseName, $Port, $ServiceName, (-not $SkipInstall -and -not $serviceExists), (-not $SkipMigrations), (-not $SkipBackupTask))

if (-not $PSCmdlet.ShouldProcess("PostgreSQL 16 on this server ($ServiceName)", 'Install/configure, create roles and database, set machine environment variables, apply migrations, register the backup task')) {
    Say 'Dry run: would (1) install PostgreSQL silently if the service is missing, (2) set listen_addresses = 127.0.0.1, (3) create database qbautopost and roles qbautopost_owner and qbautopost_app, (4) write machine variables QBAUTOPOST__Database__ConnectionString, QBAUTOPOST_MIGRATION_CONNECTION and (if absent) QBAUTOPOST__Auth__Jwt__SigningKey, (5) run apply-migrations.ps1, (6) register the daily task and run nothing else. Nothing was changed.'
    exit 0
}

try {
    $superuserPlain = ConvertTo-PlainText $SuperuserPassword
    if ($superuserPlain.Length -lt 12) { throw 'Use a superuser password of at least 12 characters.' }

    # ---- 1. install ------------------------------------------------------------------------------------------
    $bootstrapPassword = $null
    if (-not $serviceExists) {
        if ($SkipInstall) { throw "Service '$ServiceName' not found and -SkipInstall was given." }
        $installer = $InstallerPath
        $downloaded = $false
        if ($InstallerUrl) {
            $installer = Join-Path $env:TEMP 'qbautopost-postgresql-16-installer.exe'
            Say "Downloading the installer from $InstallerUrl."
            Invoke-WebRequest -Uri $InstallerUrl -OutFile $installer -UseBasicParsing
            $downloaded = $true
        }
        $actual = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
        if ($actual -ne $InstallerSha256.ToUpperInvariant()) {
            if ($downloaded) { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
            throw "Installer SHA-256 is $actual, expected $($InstallerSha256.ToUpperInvariant()). Not running it."
        }
        $bootstrapPassword = New-RandomSecret -Length 24
        Say 'Installing PostgreSQL 16 (silent, no pgAdmin, no Stack Builder). This takes a few minutes.'
        # The installer takes the superuser password on its command line, so it gets a throwaway one that is replaced below.
        # Start-Process does not quote array items, so paths with spaces are quoted here.
        $installArgs = @(
            '--mode', 'unattended', '--unattendedmodeui', 'none',
            '--superpassword', $bootstrapPassword,
            '--servicename', $ServiceName, '--serverport', [string] $Port,
            '--prefix', ('"' + $InstallDir + '"'), '--datadir', ('"' + $DataDir + '"'),
            '--enable-components', 'server,commandlinetools',
            '--disable-components', 'pgAdmin,stackbuilder'
        )
        $process = Start-Process -FilePath $installer -ArgumentList $installArgs -Wait -PassThru
        if ($downloaded) { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
        if ($process.ExitCode -ne 0) { throw "The PostgreSQL installer exited with code $($process.ExitCode)." }
    }
    else {
        Say "Service '$ServiceName' already exists; not installing."
    }

    $service = Get-Service -Name $ServiceName
    if ($service.Status -ne 'Running') {
        Start-Service -Name $ServiceName
    }
    if (-not (Wait-PostgresPort -TimeoutSeconds 120)) { throw "PostgreSQL did not accept connections on 127.0.0.1:$Port within 120 s." }

    $psql = Find-PgTool -Name 'psql' -PgBin $PgBin
    $version = (& $psql --version)
    if ($version -notmatch ' 16\.') { throw "Expected PostgreSQL 16, found: $version" }

    # ---- 2. superuser password, loopback only ----------------------------------------------------------------
    $superuser = @{ Host = '127.0.0.1'; Port = [string] $Port; Database = 'postgres'; Username = 'postgres'; Password = $superuserPlain }
    if ($bootstrapPassword) {
        $bootstrap = @{ Host = '127.0.0.1'; Port = [string] $Port; Database = 'postgres'; Username = 'postgres'; Password = $bootstrapPassword }
        Invoke-Pg -Exe $psql -Connection $bootstrap -Arguments @('-X', '-q', '-v', 'ON_ERROR_STOP=1', '-f', '-') -StdIn ('ALTER ROLE postgres PASSWORD {0};' -f (ConvertTo-SqlLiteral $superuserPlain)) | Out-Null
        Say 'Superuser password set to the one you supplied.'
    }

    $listen = (Invoke-Pg -Exe $psql -Connection $superuser -Arguments @('-X', '-q', '-t', '-A', '-c', 'SHOW listen_addresses'))[0]
    if ($listen -ne '127.0.0.1') {
        Say "listen_addresses was '$listen'; setting 127.0.0.1 and restarting the service."
        Invoke-Pg -Exe $psql -Connection $superuser -Arguments @('-X', '-q', '-v', 'ON_ERROR_STOP=1', '-c', "ALTER SYSTEM SET listen_addresses = '127.0.0.1'") | Out-Null
        Restart-Service -Name $ServiceName -Force
        if (-not (Wait-PostgresPort -TimeoutSeconds 120)) { throw 'PostgreSQL did not come back after the restart.' }
    }

    # ---- 3. roles and database -------------------------------------------------------------------------------
    $ownerPassword = Get-RolePassword -Variable $OwnerVariable -Role $OwnerRole
    $appPassword = Get-RolePassword -Variable $AppVariable -Role $AppRole
    $rolesSql = @"
DO `$`$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$OwnerRole') THEN CREATE ROLE $OwnerRole LOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$AppRole') THEN CREATE ROLE $AppRole LOGIN; END IF;
END `$`$;
ALTER ROLE $OwnerRole NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD $(ConvertTo-SqlLiteral $ownerPassword);
ALTER ROLE $AppRole NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD $(ConvertTo-SqlLiteral $appPassword);
"@
    Invoke-Pg -Exe $psql -Connection $superuser -Arguments @('-X', '-q', '-v', 'ON_ERROR_STOP=1', '-f', '-') -StdIn $rolesSql | Out-Null

    $exists = (Invoke-Pg -Exe $psql -Connection $superuser -Arguments @('-X', '-q', '-t', '-A', '-c', "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'"))
    if (-not ($exists -contains '1')) {
        Say "Creating database $DatabaseName owned by $OwnerRole."
        Invoke-Pg -Exe $psql -Connection $superuser -Arguments @('-X', '-q', '-v', 'ON_ERROR_STOP=1', '-c', "CREATE DATABASE $DatabaseName OWNER $OwnerRole") | Out-Null
    }
    $connectSql = "REVOKE ALL ON DATABASE $DatabaseName FROM PUBLIC; GRANT CONNECT ON DATABASE $DatabaseName TO $OwnerRole, $AppRole;"
    Invoke-Pg -Exe $psql -Connection $superuser -Arguments @('-X', '-q', '-v', 'ON_ERROR_STOP=1', '-c', $connectSql) | Out-Null

    # ---- 4. machine environment variables --------------------------------------------------------------------
    Set-MachineVariable -Name $AppVariable -Value (Get-ConnectionString -Role $AppRole -Password $appPassword)
    Set-MachineVariable -Name $OwnerVariable -Value (Get-ConnectionString -Role $OwnerRole -Password $ownerPassword)
    Say "Set machine variables $AppVariable and $OwnerVariable (values not shown)."
    if ([Environment]::GetEnvironmentVariable($JwtVariable, 'Machine')) {
        Say "$JwtVariable already set; kept."
    }
    else {
        $bytes = New-Object byte[] 48
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        Set-MachineVariable -Name $JwtVariable -Value ([Convert]::ToBase64String($bytes))
        Say "Generated $JwtVariable (value not shown)."
    }

    # ---- 5. migrations and grants ----------------------------------------------------------------------------
    if (-not $SkipMigrations) {
        & (Join-Path $here 'apply-migrations.ps1') -OwnerConnection (Get-ConnectionString -Role $OwnerRole -Password $ownerPassword) -PgBin $PgBin
        if ($LASTEXITCODE -ne 0) { throw 'apply-migrations.ps1 failed; read its output and C:\qb-autopost\logs\apply-migrations-*.log.' }
    }

    # ---- 6. daily backup task --------------------------------------------------------------------------------
    if (-not $SkipBackupTask) {
        $backupScript = Join-Path $here 'backup-postgres.ps1'
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -BackupFolder "{1}" -PgBin "{2}"' -f $backupScript, $BackupFolder, $PgBin
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $here
        $trigger = New-ScheduledTaskTrigger -Daily -At $BackupTime
        # SYSTEM: no password is stored, and it reads the machine-scoped variable. A backup folder on a network share
        # needs another account (re-register the task by hand, runbook section 10).
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
        Register-ScheduledTask -TaskName $BackupTaskName -Action $action -Trigger $trigger -Principal $principal -Settings $taskSettings -Force | Out-Null
        Say "Task '$BackupTaskName' registered: daily at $BackupTime, dumps to $BackupFolder\postgres, newest 30 kept."
    }

    Say 'Done. Open a NEW PowerShell window and restart the API so it sees the new variables.'
    exit 0
}
catch {
    Say $_.Exception.Message 'ERR'
    exit 1
}
