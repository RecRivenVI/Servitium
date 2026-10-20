[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Development.ps1')
$context = Get-DevelopmentContext
& (Join-Path $PSScriptRoot 'Build-JavaFixture.ps1')
Push-Location $context.RepositoryRoot
try {
    if (-not $NoRestore) {
        & $context.Dotnet restore Servitium.sln --locked-mode --disable-parallel
        if ($LASTEXITCODE -ne 0) { throw "Locked restore exited with $LASTEXITCODE." }
    }
    & $context.Dotnet build Servitium.sln --configuration $Configuration --no-restore --maxcpucount:1
    if ($LASTEXITCODE -ne 0) { throw "Build exited with $LASTEXITCODE." }
} finally { Pop-Location }
