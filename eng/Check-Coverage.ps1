[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$coverageRoot = Join-Path $repositoryRoot 'artifacts\test-results\coverage'
$coverageRunRoot = Join-Path $coverageRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Path $coverageRunRoot -Force | Out-Null

$coverageProjects = @(
    'tests\Servitium.Domain.Tests\Servitium.Domain.Tests.csproj',
    'tests\Servitium.Application.Tests\Servitium.Application.Tests.csproj',
    'tests\Servitium.Infrastructure.IntegrationTests\Servitium.Infrastructure.IntegrationTests.csproj',
    'tests\Servitium.Server.IntegrationTests\Servitium.Server.IntegrationTests.csproj',
    'tests\Servitium.Agent.IntegrationTests\Servitium.Agent.IntegrationTests.csproj'
)
foreach ($project in $coverageProjects) {
    & dotnet 'test' (Join-Path $repositoryRoot $project) '--configuration' $Configuration '--no-build' '--no-restore' '--collect' 'XPlat Code Coverage' '--settings' (Join-Path $PSScriptRoot 'coverage.runsettings') '--results-directory' $coverageRunRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Coverage test run failed for $project with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot 'Test-JavaFixture.ps1') -Category Unit
& (Join-Path $PSScriptRoot 'Test-JavaFixture.ps1') -Category Integration

$coverageFiles = @(Get-ChildItem -LiteralPath $coverageRunRoot -Filter 'coverage.cobertura.xml' -File -Recurse)
if ($coverageFiles.Count -eq 0) {
    throw 'Coverage collection produced no Cobertura reports.'
}

$lineHits = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
$lineProjects = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)

foreach ($coverageFile in $coverageFiles) {
    [xml]$report = Get-Content -LiteralPath $coverageFile.FullName -Raw
    foreach ($package in @($report.coverage.packages.package)) {
        $packageName = [string]$package.name
        foreach ($class in @($package.classes.class)) {
            $fileName = [string]$class.filename
            $fileName = $fileName.Replace('/', '\')
            $sourceIndex = $fileName.IndexOf('src\', [System.StringComparison]::OrdinalIgnoreCase)
            if ($sourceIndex -ge 0) {
                $fileName = $fileName.Substring($sourceIndex)
            }
            elseif ($fileName.StartsWith('Servitium.', [System.StringComparison]::OrdinalIgnoreCase)) {
                # coverlet 会按测试宿主产生仓库相对路径和项目相对路径；统一后才能按源码行取最大命中数。
                $fileName = 'src\' + $fileName
            }

            foreach ($line in @($class.lines.line)) {
                if ($null -eq $line -or [string]::IsNullOrWhiteSpace([string]$line.number)) {
                    continue
                }

                $key = "$packageName|$fileName|$($line.number)"
                $hits = [int]$line.hits
                if (-not $lineHits.ContainsKey($key) -or $hits -gt $lineHits[$key]) {
                    $lineHits[$key] = $hits
                    $lineProjects[$key] = $packageName
                }
            }
        }
    }
}

$totalLines = $lineHits.Count
$coveredLines = @($lineHits.Values | Where-Object { $_ -gt 0 }).Count
$overallRate = if ($totalLines -eq 0) { 1.0 } else { $coveredLines / $totalLines }
if ($overallRate -lt 0.70) {
    throw ('Overall line coverage {0:P2} is below 70%.' -f $overallRate)
}

foreach ($projectName in @('Servitium.Domain', 'Servitium.Application')) {
    $projectKeys = @($lineProjects.Keys | Where-Object { $lineProjects[$_] -eq $projectName })
    $projectCovered = @($projectKeys | Where-Object { $lineHits[$_] -gt 0 }).Count
    $rate = if ($projectKeys.Count -eq 0) { 1.0 } else { $projectCovered / $projectKeys.Count }
    if ($rate -lt 0.85) {
        throw ("{0} line coverage {1:P2} is below 85%." -f $projectName, $rate)
    }
}

Write-Output ('Coverage passed: overall {0:P2}; Domain/Application satisfy 85%.' -f $overallRate)
