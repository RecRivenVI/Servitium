[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'tests\Fixtures\Servitium.JavaServerFixture\src'
$fixtureObjectRoot = Join-Path $repositoryRoot 'artifacts\obj\Servitium.JavaServerFixture\java'
$classesRoot = Join-Path $fixtureObjectRoot 'classes'
$fixtureOutputRoot = Join-Path $repositoryRoot 'artifacts\test-fixtures'
$jarPath = Join-Path $fixtureOutputRoot 'Servitium.JavaServerFixture.jar'

if (Test-Path -LiteralPath $classesRoot) {
    $resolvedClassesRoot = (Resolve-Path -LiteralPath $classesRoot).Path
    $resolvedObjectRoot = [System.IO.Path]::GetFullPath($fixtureObjectRoot)
    if (-not $resolvedClassesRoot.StartsWith($resolvedObjectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected Java classes path: $resolvedClassesRoot"
    }

    Remove-Item -LiteralPath $resolvedClassesRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $classesRoot -Force | Out-Null
New-Item -ItemType Directory -Path $fixtureOutputRoot -Force | Out-Null
$javaSources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.java' -File -Recurse | Sort-Object FullName)
if ($javaSources.Count -eq 0) {
    throw 'No Java fixture sources were found.'
}

& javac '-encoding' 'UTF-8' '-d' $classesRoot @($javaSources.FullName)
if ($LASTEXITCODE -ne 0) {
    throw "javac failed with exit code $LASTEXITCODE."
}

& jar '--create' '--file' $jarPath '--main-class' 'servitium.fixture.Main' '-C' $classesRoot '.'
if ($LASTEXITCODE -ne 0) {
    throw "jar failed with exit code $LASTEXITCODE."
}

Write-Output $jarPath
