#Requires -Version 5.1
# Starts the API host (bind address, API key, company file and paths come from appsettings*.json);
# secrets come from appsettings or QBAUTOPOST__* environment variables, never from this script.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet run --project (Join-Path $root 'src/QbAutopost.Api/QbAutopost.Api.csproj') @args
exit $LASTEXITCODE
