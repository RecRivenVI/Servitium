[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = & dotnet 'list' (Join-Path $repositoryRoot 'Servitium.sln') 'package' '--vulnerable' '--include-transitive' 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Dependency vulnerability scan failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
}

$text = $output -join [Environment]::NewLine
Write-Output $text
if ($text.Contains('has the following vulnerable packages', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The dependency scan found known vulnerable packages.'
}

Write-Output 'Dependency vulnerability scan passed.'
