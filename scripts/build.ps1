#Requires -Version 5.1
# Builds the solution with warnings as errors (plan §4).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $root 'QbAutopost.sln') -warnaserror @args
exit $LASTEXITCODE
