[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Development.ps1')
$context = Get-DevelopmentContext
$sourceRoot = Join-Path $context.RepositoryRoot 'tests/Fixtures/Servitium.JavaServerFixture/src'
$outputRoot = Join-Path $context.ArtifactsRoot 'Build/Fixtures'
$classesRoot = Join-Path $context.ArtifactsRoot ('Build/Java/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $classesRoot, $outputRoot -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.java' -File -Recurse | Sort-Object FullName)
& javac '-encoding' 'UTF-8' '-d' $classesRoot @($sources.FullName)
if ($LASTEXITCODE -ne 0) { throw "javac exited with $LASTEXITCODE." }
$jarPath = Join-Path $outputRoot 'Servitium.JavaServerFixture.jar'
& jar '--create' '--file' $jarPath '--main-class' 'servitium.fixture.Main' '-C' $classesRoot '.'
if ($LASTEXITCODE -ne 0) { throw "jar exited with $LASTEXITCODE." }
Write-Output $jarPath
