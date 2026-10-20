[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [string]$Project = 'Servitium.sln')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Development.ps1')
$context = Get-DevelopmentContext
$resultRoot = Join-Path $context.ArtifactsRoot ('Validation/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
Push-Location $context.RepositoryRoot
try {
    & $context.Dotnet test $Project --configuration $Configuration --no-restore --maxcpucount:1 --logger trx --results-directory $resultRoot
    if ($LASTEXITCODE -ne 0) { throw "Tests exited with $LASTEXITCODE. Results: $resultRoot" }
} finally { Pop-Location }
