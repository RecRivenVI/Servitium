[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
if (Test-Path -LiteralPath $artifactRoot) {
    $resolvedArtifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
    $expectedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
    if (-not $resolvedArtifactRoot.Equals($expectedArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedArtifactRoot.StartsWith($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected artifacts path: $resolvedArtifactRoot"
    }

    Remove-Item -LiteralPath $resolvedArtifactRoot -Recurse -Force
}

Write-Output "Cleaned $artifactRoot"
