#Requires -Version 5.1
<#
.SYNOPSIS
  Helpers shared by install-postgres.ps1, apply-migrations.ps1 and backup-postgres.ps1 (T-1016). Dot-source it; it runs nothing.

.DESCRIPTION
  A password never goes on a command line (any local user can read those): psql and pg_dump get it through the PGPASSWORD
  environment variable of their own process, and SQL that contains a password is piped to psql on standard input.
  Nothing here writes a secret to a file or to the console.
#>

function ConvertFrom-NpgsqlConnection {
    # "Host=127.0.0.1;Port=5432;Database=qbautopost;Username=qbautopost_app;Password=..." -> hashtable
    param([Parameter(Mandatory = $true)][string] $ConnectionString)
    $result = @{ Host = '127.0.0.1'; Port = '5432'; Database = ''; Username = ''; Password = '' }
    foreach ($part in $ConnectionString.Split(';')) {
        $pair = $part.Split('=', 2)
        if ($pair.Count -ne 2) { continue }
        $name = $pair[0].Trim().ToLowerInvariant()
        switch ($name) {
            'host' { $result.Host = $pair[1].Trim() }
            'server' { $result.Host = $pair[1].Trim() }
            'port' { $result.Port = $pair[1].Trim() }
            'database' { $result.Database = $pair[1].Trim() }
            'username' { $result.Username = $pair[1].Trim() }
            'user id' { $result.Username = $pair[1].Trim() }
            'password' { $result.Password = $pair[1] }
        }
    }
    return $result
}

function Find-PgTool {
    # Returns the full path of psql.exe / pg_dump.exe / pg_restore.exe, or throws.
    param([Parameter(Mandatory = $true)][string] $Name, [string] $PgBin = '')
    $candidates = @()
    if ($PgBin) { $candidates += (Join-Path $PgBin ($Name + '.exe')) }
    $candidates += (Join-Path $env:ProgramFiles ('PostgreSQL\16\bin\' + $Name + '.exe'))
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    $onPath = Get-Command ($Name + '.exe') -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "$Name.exe not found. Install PostgreSQL 16 (deploy\install-postgres.ps1) or pass -PgBin <folder with the PostgreSQL tools>."
}

function New-RandomSecret {
    # Letters and digits only (no characters that need quoting in SQL or a connection string); no modulo bias.
    param([int] $Length = 32)
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $limit = 256 - (256 % $alphabet.Length)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $builder = New-Object System.Text.StringBuilder
    try {
        $buffer = New-Object byte[] 64
        while ($builder.Length -lt $Length) {
            $rng.GetBytes($buffer)
            foreach ($b in $buffer) {
                if ($b -lt $limit -and $builder.Length -lt $Length) {
                    [void] $builder.Append($alphabet[$b % $alphabet.Length])
                }
            }
        }
    }
    finally {
        $rng.Dispose()
    }
    return $builder.ToString()
}

function Invoke-Pg {
    # Runs one PostgreSQL client tool with the connection in PG* variables of this process only.
    # -StdIn is piped to the tool (use it for SQL that contains a password). Returns the output lines.
    param(
        [Parameter(Mandatory = $true)][string] $Exe,
        [Parameter(Mandatory = $true)][hashtable] $Connection,
        [string[]] $Arguments = @(),
        [string] $StdIn = $null,
        [string] $Database = ''
    )
    $saved = @{}
    foreach ($name in 'PGHOST', 'PGPORT', 'PGDATABASE', 'PGUSER', 'PGPASSWORD', 'PGCONNECT_TIMEOUT') {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    try {
        $db = $Connection.Database
        if ($Database) { $db = $Database }
        [Environment]::SetEnvironmentVariable('PGHOST', $Connection.Host, 'Process')
        [Environment]::SetEnvironmentVariable('PGPORT', $Connection.Port, 'Process')
        [Environment]::SetEnvironmentVariable('PGDATABASE', $db, 'Process')
        [Environment]::SetEnvironmentVariable('PGUSER', $Connection.Username, 'Process')
        [Environment]::SetEnvironmentVariable('PGPASSWORD', $Connection.Password, 'Process')
        [Environment]::SetEnvironmentVariable('PGCONNECT_TIMEOUT', '10', 'Process')
        if ($null -ne $StdIn) {
            $output = $StdIn | & $Exe @Arguments
        }
        else {
            $output = & $Exe @Arguments
        }
        if ($LASTEXITCODE -ne 0) {
            throw ('{0} failed with exit code {1}.' -f [System.IO.Path]::GetFileName($Exe), $LASTEXITCODE)
        }
        return @($output)
    }
    finally {
        foreach ($name in $saved.Keys) {
            [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
        }
    }
}

function Get-MigrationIds {
    # The migration ids a deploy\migrations.sql script applies, in order, read from its own text.
    param([Parameter(Mandatory = $true)][string] $ScriptPath)
    $text = Get-Content -Raw -LiteralPath $ScriptPath
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, "'(\d{14}_[A-Za-z0-9_]+)'")) {
        if (-not $found.Contains($m.Groups[1].Value)) { $found.Add($m.Groups[1].Value) }
    }
    return $found.ToArray()
}

function Write-LogLine {
    param([string] $Tag, [string] $Message, [string] $Level = 'INF', [string] $LogFile = '')
    $line = '{0} [{1}] [{2}] {3}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Tag, $Message
    Write-Host $line
    if ($LogFile) {
        try { Add-Content -LiteralPath $LogFile -Value $line -Encoding ASCII } catch { }
    }
}
