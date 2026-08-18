[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Unit', 'Integration')]
    [string]$Category
)

$ErrorActionPreference = 'Stop'
$nativeErrorsWereEnabled = $PSNativeCommandUseErrorActionPreference
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$jarPath = Join-Path $repositoryRoot 'artifacts\test-fixtures\Servitium.JavaServerFixture.jar'
if (-not (Test-Path -LiteralPath $jarPath -PathType Leaf)) {
    throw "Java fixture is missing: $jarPath"
}

try {
    if ($Category -eq 'Unit') {
        $description = & java '-jar' $jarPath '--describe' '--startup-delay-ms' '7' '--output-interval-ms' '11' '--ignore-stop' '--crash-exit-code' '23'
        if ($LASTEXITCODE -ne 0) {
            throw "UNT-001 failed: fixture argument parsing exited with $LASTEXITCODE."
        }

        $parsed = $description | ConvertFrom-Json
        if ($parsed.startupDelayMs -ne 7 -or
            $parsed.outputIntervalMs -ne 11 -or
            $parsed.ignoreStop -ne $true -or
            $parsed.crashExitCode -ne 23) {
            throw "UNT-001 failed: unexpected parsed values: $description"
        }

        Write-Output 'UNT-001 passed: Java fixture arguments are parsed deterministically.'
        return
    }

    $gracefulOutput = 'stop' | & java '-jar' $jarPath
    if ($LASTEXITCODE -ne 0) {
        throw "INT-003 graceful fixture path exited with $LASTEXITCODE."
    }

    $gracefulText = $gracefulOutput -join [Environment]::NewLine
    if (-not $gracefulText.Contains('fixture-ready', [System.StringComparison]::Ordinal) -or
        -not $gracefulText.Contains('fixture-stopping', [System.StringComparison]::Ordinal)) {
        throw "INT-003 graceful fixture output was incomplete: $gracefulText"
    }

    $crashOutput = & java '-jar' $jarPath '--crash-exit-code' '17'
    if ($LASTEXITCODE -ne 17) {
        throw "INT-003 crash fixture path returned $LASTEXITCODE instead of 17. Output: $($crashOutput -join ' ')"
    }

    Write-Output 'INT-003 passed: Java fixture starts, stops by command, and returns a configured crash code.'
}
finally {
    $PSNativeCommandUseErrorActionPreference = $nativeErrorsWereEnabled
}
