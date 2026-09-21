<#
.SYNOPSIS
  Builds the POC package (T-806): a self-contained QbAutopost (no .NET install needed on the server), the no-AI POC
  settings, rules, QuickBooks list import and sample job, the step script and README-POC.md, zipped under dist\.

.EXAMPLE
  .\scripts\build-poc-package.ps1                    # 64-bit QuickBooks (2022 and later)
  .\scripts\build-poc-package.ps1 -Runtime win-x86   # 32-bit QuickBooks
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-x86')]
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$name = "qbautopost-poc-$Runtime"
$stage = Join-Path $root "dist\$name"
$zip = Join-Path $root "dist\$name.zip"

if (Test-Path $stage) {
    # A file in the old staging folder can be held by a virus scanner or a still-running copy of the app.
    try { Remove-Item $stage -Recurse -Force -ErrorAction Stop }
    catch { $stage = Join-Path $root ("dist\$name-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
}
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

& dotnet publish (Join-Path $root 'src\QbAutopost.Api') -c Release -r $Runtime --self-contained true -o (Join-Path $stage 'app')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Development settings and development data must not ship; the POC settings are copied over appsettings.json there.
Remove-Item (Join-Path $stage 'app\appsettings.Development.json') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $stage 'app\data') -Recurse -Force -ErrorAction SilentlyContinue

$poc = Join-Path $stage 'poc'
New-Item -ItemType Directory -Force $poc | Out-Null
foreach ($item in 'appsettings.json', 'rules.json', 'tropicana-lists.iif', 'jobs') {
    Copy-Item (Join-Path $root "samples\poc\$item") $poc -Recurse -Force
}
Get-ChildItem $poc -Recurse -Directory -Filter output | Remove-Item -Recurse -Force

New-Item -ItemType Directory -Force (Join-Path $stage 'scripts') | Out-Null
Copy-Item (Join-Path $root 'scripts\qb-server-check.ps1') (Join-Path $stage 'scripts') -Force
Copy-Item (Join-Path $root 'samples\poc\README-POC.md') $stage -Force
Copy-Item (Join-Path $root 'steps.md') $stage -Force

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Package: $zip"
