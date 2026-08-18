[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Unit', 'Architecture', 'Integration')]
    [string]$Category,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resultRoot = Join-Path $repositoryRoot "artifacts\test-results\$($Category.ToLowerInvariant())"
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null

$projects = switch ($Category) {
    'Unit' {
        @(
            'tests\Servitium.Domain.Tests\Servitium.Domain.Tests.csproj',
            'tests\Servitium.Application.Tests\Servitium.Application.Tests.csproj',
            'tests\Servitium.Desktop.Tests\Servitium.Desktop.Tests.csproj'
        )
    }
    'Architecture' {
        @(
            'tests\Servitium.Domain.Tests\Servitium.Domain.Tests.csproj',
            'tests\Servitium.Application.Tests\Servitium.Application.Tests.csproj'
        )
    }
    'Integration' {
        @(
            'tests\Servitium.Infrastructure.IntegrationTests\Servitium.Infrastructure.IntegrationTests.csproj',
            'tests\Servitium.Server.IntegrationTests\Servitium.Server.IntegrationTests.csproj',
            'tests\Servitium.Agent.IntegrationTests\Servitium.Agent.IntegrationTests.csproj',
            'tests\Servitium.Client.ContractTests\Servitium.Client.ContractTests.csproj'
        )
    }
}

foreach ($project in $projects) {
    $projectPath = Join-Path $repositoryRoot $project
    $arguments = @(
        'test',
        $projectPath,
        '--configuration', $Configuration,
        '--no-build',
        '--no-restore',
        '--filter', "Category=$Category",
        '--results-directory', $resultRoot,
        '--logger', 'trx'
    )
    if ($project -like '*Servitium.Desktop.Tests*') {
        $arguments += '--property:Platform=x64'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed for $project with exit code $LASTEXITCODE."
    }
}

if ($Category -eq 'Unit' -or $Category -eq 'Integration') {
    & (Join-Path $PSScriptRoot 'Test-JavaFixture.ps1') -Category $Category
}
