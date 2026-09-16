#Requires -Version 5.1
# Runs every test project. No test contacts a real QuickBooks or Hermes (CLAUDE.md rule 1).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet test (Join-Path $root 'QbAutopost.sln') --logger 'console;verbosity=minimal' @args
exit $LASTEXITCODE
