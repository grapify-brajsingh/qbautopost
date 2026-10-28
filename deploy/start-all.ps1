#Requires -Version 5.1
<#
.SYNOPSIS
  Starts Hermes (Docker) and then the QbAutopost API in this interactive session (spec section 4, T-802).

.DESCRIPTION
  Order:
    1. Docker up: WSL 2 + Docker Engine (-DockerMode Wsl, Windows Server) or Docker Desktop (-DockerMode Desktop).
    2. docker compose up -d in deploy/hermes (the file publishes 127.0.0.1:8642 only).
    3. Wait until Hermes accepts connections on 127.0.0.1:8642.
    4. Start or verify the PostgreSQL service and wait for its port (saved requests, T-1016). The API must start AFTER it:
       the bootstrap admin runs once at start and is skipped, with a warning, when the database is not Ready. A database
       that stays down is a warning, not a failure: the validate-and-post routes do not need it. If the admin was not
       created because the database was down, restart the app once.
    5. Start the API (unless it already runs), then wait for GET /api/v1/health/ready to answer 200.
    6. Read the same key-free answer's `database` check and warn when the schema is not current (run
       deploy\apply-migrations.ps1) or the database is not reachable.
  This script holds no secret of any kind, so it can only call routes that need no key: since T-910 those are
  /api/v1/health and /api/v1/health/ready only. /api/v1/health/hermes needs the health:read scope, so Hermes is
  proved here by its own port (step 3) rather than through the API. Use
  scripts\qb-server-check.ps1 -Step health, which does carry a key, for the dependency checks.
  If Hermes does not come up in time, the API is still started (undo and QuickBooks health do not need Hermes;
  a new job fails at T1 and posts nothing) and the script exits 2. SPEC-GAP T-802, tracker Q-43.
  Use -WhatIf for a dry run: it prints every step and changes nothing.
  Registered at logon by deploy/install-task.ps1; never run it as a service.

  Exit codes: 0 all up; 1 Docker, compose or the API could not be started; 2 the API runs but Hermes is not healthy.

.EXAMPLE
  .\deploy\start-all.ps1 -WhatIf
  .\deploy\start-all.ps1 -DockerMode Wsl -WslDistro Ubuntu
  .\deploy\start-all.ps1 -DockerMode Desktop -AppExe D:\qb-autopost\app\QbAutopost.Api.exe
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Wsl', 'Desktop')]
    [string] $DockerMode = 'Wsl',
    [string] $WslDistro = 'Ubuntu',
    [string] $ComposeDir = '',
    [string] $AppExe = 'C:\qb-autopost\app\QbAutopost.Api.exe',
    [string] $ApiBaseUrl = 'http://127.0.0.1:5080',
    [int] $HermesPort = 8642,
    [string] $PostgresService = 'postgresql-x64-16',
    [int] $PostgresPort = 5432,
    [int] $PostgresWaitSeconds = 60,
    [switch] $SkipPostgres,
    [string] $DockerDesktopExe = (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'),
    [int] $DockerWaitSeconds = 180,
    [int] $HermesWaitSeconds = 300,
    [int] $ApiWaitSeconds = 180,
    [string] $LogDir = 'C:\qb-autopost\logs'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot is not set inside param() defaults when Windows PowerShell 5.1 runs a script with -File,
# so path defaults are resolved here.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ComposeDir) { $ComposeDir = Join-Path $here 'hermes' }

$script:LogFile = $null
if (-not $WhatIfPreference) {
    try {
        if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
        $script:LogFile = Join-Path $LogDir ('start-all-' + (Get-Date -Format 'yyyyMMdd') + '.log')
    }
    catch {
        $script:LogFile = $null
    }
}

function Write-Step {
    param([string] $Message, [string] $Level = 'INF')
    $line = '{0} [{1}] [start-all] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    if ($script:LogFile) {
        try { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding ASCII } catch { }
    }
}

function Test-Docker {
    if ($DockerMode -eq 'Wsl') {
        & wsl.exe -d $WslDistro -- docker info *> $null
    }
    else {
        & docker info *> $null
    }
    return ($LASTEXITCODE -eq 0)
}

function Start-Docker {
    param([int] $TimeoutSeconds)
    if (Test-Docker) {
        Write-Step "Docker is running ($DockerMode)."
        return $true
    }

    if ($DockerMode -eq 'Wsl') {
        # Booting the distribution starts dockerd when systemd is enabled (README-hermes.md section 1);
        # otherwise start it explicitly as root inside the distribution.
        Write-Step "Starting Docker Engine in WSL distribution '$WslDistro'."
        & wsl.exe -d $WslDistro -u root -- sh -c 'systemctl start docker 2>/dev/null || service docker start' *> $null
    }
    else {
        if (-not (Test-Path -LiteralPath $DockerDesktopExe)) {
            Write-Step "Docker Desktop not found at '$DockerDesktopExe'." 'ERR'
            return $false
        }
        Write-Step 'Starting Docker Desktop.'
        Start-Process -FilePath $DockerDesktopExe | Out-Null
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Docker) {
            Write-Step 'Docker is running.'
            return $true
        }
        Start-Sleep -Seconds 5
    }
    Write-Step "Docker did not answer within $TimeoutSeconds s." 'ERR'
    return $false
}

function Start-Hermes {
    param([string] $Directory)
    $composeFile = Join-Path $Directory 'docker-compose.yml'
    Write-Step "docker compose up -d ($composeFile)."
    if ($DockerMode -eq 'Wsl') {
        # wsl --cd accepts a Windows path; compose then reads .env and provider.env next to the file.
        & wsl.exe -d $WslDistro --cd $Directory -- docker compose up -d
    }
    else {
        & docker compose -f $composeFile up -d
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Step "docker compose up failed (exit $LASTEXITCODE). Check deploy/hermes/.env (README-hermes.md)." 'ERR'
        return $false
    }
    return $true
}

function Test-TcpPort {
    param([int] $Port)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(2000)) { return $false }
        $client.EndConnect($connect)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Wait-HermesPort {
    param([int] $Port, [int] $TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-TcpPort -Port $Port) {
            Write-Step "Hermes accepts connections on 127.0.0.1:$Port."
            return $true
        }
        Start-Sleep -Seconds 5
    }
    Write-Step "Hermes did not open 127.0.0.1:$Port within $TimeoutSeconds s." 'WRN'
    return $false
}

function Start-Postgres {
    # Saved requests live in PostgreSQL on this server (spec 5.4). The service is normally automatic and already running;
    # this starts it if it is stopped and waits for the port. Never fatal: without it only the saved-requests routes are 503.
    param([string] $Name, [int] $Port, [int] $TimeoutSeconds)
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Step "PostgreSQL service '$Name' not found (deploy\install-postgres.ps1). Saved requests and dashboard sign-in stay unavailable." 'WRN'
        return $false
    }
    if ($service.Status -ne 'Running') {
        Write-Step "Starting the PostgreSQL service '$Name'."
        try { Start-Service -Name $Name }
        catch { Write-Step "Could not start '$Name': $($_.Exception.Message). Start it from an administrator session." 'WRN' }
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-TcpPort -Port $Port) {
            Write-Step "PostgreSQL accepts connections on 127.0.0.1:$Port."
            return $true
        }
        Start-Sleep -Seconds 3
    }
    Write-Step "PostgreSQL did not open 127.0.0.1:$Port within $TimeoutSeconds s; the API starts without the database feature." 'WRN'
    return $false
}

function Test-DatabaseCheck {
    # The key-free readiness body carries a `database` check with a reason (schema-out-of-date, unreachable, ...).
    param([string] $BaseUrl)
    $url = $BaseUrl.TrimEnd('/') + '/api/v1/health/ready'
    try {
        $body = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json
        $check = @($body.checks | Where-Object { $_.name -eq 'database' })
        if ($check.Count -eq 0) {
            Write-Step 'The API reports no database check: Database:ConnectionString is not set.' 'WRN'
        }
        elseif (-not $check[0].ok) {
            if ($check[0].reason -eq 'schema-out-of-date') {
                Write-Step 'The database schema is NOT current: stop the API, run deploy\apply-migrations.ps1, then start it again. Saved-request routes answer 503 until then.' 'WRN'
            }
            else {
                Write-Step "The database is not ready ($($check[0].reason)). If it was down at start, restart the app once so the bootstrap admin is created." 'WRN'
            }
        }
        else {
            Write-Step 'Database ready and schema current.'
        }
    }
    catch {
        Write-Step "Could not read the database check: $($_.Exception.Message)" 'WRN'
    }
}

function Get-ApiProcess {
    param([string] $Exe)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($Exe)
    return @(Get-Process -Name $name -ErrorAction SilentlyContinue)
}

function Start-Api {
    param([string] $Exe)
    $running = Get-ApiProcess -Exe $Exe
    if ($running.Count -gt 0) {
        # One instance only: two hosts would share the ledger and the job index.
        Write-Step ('The API already runs (pid ' + (($running | ForEach-Object { $_.Id }) -join ', ') + '); not starting another.')
        return $true
    }
    if (-not (Test-Path -LiteralPath $Exe)) {
        Write-Step "API not found at '$Exe' (runbook section 2.3 publishes it)." 'ERR'
        return $false
    }
    Write-Step "Starting the API: $Exe"
    # The working directory is the program folder so appsettings.json next to the program is used.
    Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe) -WindowStyle Minimized | Out-Null
    return $true
}

function Wait-ApiReady {
    param([string] $BaseUrl, [int] $TimeoutSeconds)
    $url = $BaseUrl.TrimEnd('/') + '/api/v1/health/ready'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = 'no answer'
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 150
            if ($response.StatusCode -eq 200) {
                Write-Step "GET $url -> 200."
                return $true
            }
            $last = "HTTP $($response.StatusCode)"
        }
        catch {
            $last = $_.Exception.Message
        }
        Start-Sleep -Seconds 10
    }
    Write-Step "GET $url not ready within $TimeoutSeconds s (last: $last)." 'WRN'
    return $false
}

if (-not ([Uri] $ApiBaseUrl).IsLoopback) {
    Write-Step "ApiBaseUrl '$ApiBaseUrl' is not a loopback address; the API binds to 127.0.0.1 (spec section 14)." 'ERR'
    exit 1
}

Write-Step "DockerMode=$DockerMode WslDistro=$WslDistro ComposeDir=$ComposeDir AppExe=$AppExe ApiBaseUrl=$ApiBaseUrl"

if (-not (Test-Path -LiteralPath (Join-Path $ComposeDir 'docker-compose.yml'))) {
    Write-Step "docker-compose.yml not found in '$ComposeDir'." 'ERR'
    exit 1
}
if (-not (Test-Path -LiteralPath (Join-Path $ComposeDir '.env'))) {
    Write-Step "No .env in '$ComposeDir' (copy .env.example, README-hermes.md section 2); compose will refuse to start." 'WRN'
}

if (-not $PSCmdlet.ShouldProcess("Docker ($DockerMode)", 'Start and wait')) {
    Write-Step "Dry run: would start Docker ($DockerMode), run docker compose up -d in '$ComposeDir', wait for 127.0.0.1:$HermesPort, start or verify the PostgreSQL service '$PostgresService' (127.0.0.1:$PostgresPort), start '$AppExe', wait for $ApiBaseUrl/api/v1/health/ready and read its database check."
    if (-not (Test-Path -LiteralPath $AppExe)) {
        Write-Step "Dry run: '$AppExe' does not exist yet." 'WRN'
    }
    exit 0
}

if (-not (Start-Docker -TimeoutSeconds $DockerWaitSeconds)) { exit 1 }
if (-not (Start-Hermes -Directory $ComposeDir)) { exit 1 }
$hermesUp = Wait-HermesPort -Port $HermesPort -TimeoutSeconds $HermesWaitSeconds

# PostgreSQL before the app: the bootstrap admin runs once at start and skips if the database is not Ready.
if ($SkipPostgres) {
    Write-Step 'PostgreSQL step skipped (-SkipPostgres).' 'WRN'
}
else {
    [void] (Start-Postgres -Name $PostgresService -Port $PostgresPort -TimeoutSeconds $PostgresWaitSeconds)
}

if (-not (Start-Api -Exe $AppExe)) { exit 1 }
$ready = Wait-ApiReady -BaseUrl $ApiBaseUrl -TimeoutSeconds $ApiWaitSeconds
if ($ready) { Test-DatabaseCheck -BaseUrl $ApiBaseUrl }

if ($hermesUp -and $ready) {
    Write-Step 'All up.'
    exit 0
}
if (-not $ready) {
    Write-Step "The API did not become ready at $ApiBaseUrl/api/v1/health/ready; read C:\qb-autopost\logs\qbautopost-*.log." 'ERR'
    exit 1
}
Write-Step 'The API runs, but Hermes is not healthy: new jobs will fail at T1 until it is (README-hermes.md section 3).' 'WRN'
exit 2
