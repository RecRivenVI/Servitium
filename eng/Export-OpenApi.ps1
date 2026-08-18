[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\openapi\swagger.json')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $repositoryRoot 'src\Servitium.Server\Servitium.Server.csproj'
& dotnet build $serverProject '--configuration' $Configuration '--no-restore'
if ($LASTEXITCODE -ne 0) {
    throw "Server build failed with exit code $LASTEXITCODE."
}

$serverExecutable = Join-Path $repositoryRoot "artifacts\bin\Servitium.Server\$Configuration\net10.0\Servitium.Server.exe"
if (-not (Test-Path -LiteralPath $serverExecutable)) {
    throw "Server apphost was not produced: $serverExecutable"
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ('servitium-openapi-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporaryRoot
$standardOutput = Join-Path $temporaryRoot 'server.stdout.log'
$standardError = Join-Path $temporaryRoot 'server.stderr.log'
$previousDataRoot = $env:SERVITIUM_DATA_ROOT
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$serverProcess = $null

try {
    $env:SERVITIUM_DATA_ROOT = $temporaryRoot
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $serverProcess = Start-Process `
        -FilePath $serverExecutable `
        -ArgumentList @('--urls', "http://127.0.0.1:$port") `
        -RedirectStandardOutput $standardOutput `
        -RedirectStandardError $standardError `
        -WindowStyle Hidden `
        -PassThru

    $endpoint = "http://127.0.0.1:$port/swagger/v1/swagger.json"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    $response = $null
    do {
        Start-Sleep -Milliseconds 100
        if ($serverProcess.HasExited) {
            $failure = if (Test-Path -LiteralPath $standardError) { Get-Content -LiteralPath $standardError -Raw } else { '' }
            throw "Server exited with code $($serverProcess.ExitCode) while exporting OpenAPI. $failure"
        }

        try {
            $response = Invoke-WebRequest -Uri $endpoint -NoProxy -TimeoutSec 2
        }
        catch {
            $response = $null
        }
    }
    while ($null -eq $response -and [DateTimeOffset]::UtcNow -lt $deadline)

    if ($null -eq $response -or $response.StatusCode -ne 200) {
        $stdout = if (Test-Path -LiteralPath $standardOutput) { Get-Content -LiteralPath $standardOutput -Raw } else { '' }
        $stderr = if (Test-Path -LiteralPath $standardError) { Get-Content -LiteralPath $standardError -Raw } else { '' }
        throw "OpenAPI endpoint did not become ready: $endpoint`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
    }

    $document = $response.Content | ConvertFrom-Json
    $pathCount = @($document.paths.PSObject.Properties).Count
    if ([string]$document.openapi -notmatch '^3\.' -or $pathCount -lt 75) {
        throw "The generated OpenAPI document is incomplete. Version=$($document.openapi) Paths=$pathCount"
    }

    $httpMethods = @('get', 'put', 'post', 'delete', 'options', 'head', 'patch', 'trace')
    $operationIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $operationCount = 0
    foreach ($pathProperty in $document.paths.PSObject.Properties) {
        foreach ($operationProperty in $pathProperty.Value.PSObject.Properties) {
            if ($operationProperty.Name -notin $httpMethods) {
                continue
            }

            $operationCount++
            $operation = $operationProperty.Value
            $operationLabel = "$($operationProperty.Name.ToUpperInvariant()) $($pathProperty.Name)"
            if ([string]::IsNullOrWhiteSpace([string]$operation.operationId) -or -not $operationIds.Add([string]$operation.operationId)) {
                throw "OpenAPI operation has a missing or duplicate operationId: $operationLabel"
            }

            $successResponses = @($operation.responses.PSObject.Properties | Where-Object { $_.Name -match '^[23]\d\d$' })
            if ($successResponses.Count -eq 0) {
                throw "OpenAPI operation does not declare a success response: $operationLabel"
            }

            $problemContent = $operation.responses.'400'.content.'application/problem+json'
            if ($null -eq $problemContent) {
                throw "OpenAPI operation does not declare application/problem+json: $operationLabel"
            }

            $problemProperties = @($problemContent.schema.properties.PSObject.Properties.Name)
            foreach ($requiredProperty in @('type', 'title', 'status', 'detail', 'instance', 'code', 'traceId', 'parameters', 'errors')) {
                if ($requiredProperty -notin $problemProperties) {
                    throw "OpenAPI problem response is missing '$requiredProperty': $operationLabel"
                }
            }
        }
    }

    if ($operationCount -lt 100) {
        throw "The generated OpenAPI operation inventory is incomplete. Operations=$operationCount"
    }

    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $resolvedOutput
    $null = New-Item -ItemType Directory -Force -Path $outputDirectory
    $normalizedContent = $response.Content.Replace(
        "http://127.0.0.1:$port/",
        'http://127.0.0.1:5344/',
        [System.StringComparison]::Ordinal)
    [System.IO.File]::WriteAllText($resolvedOutput, $normalizedContent + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Output "OpenAPI exported: $resolvedOutput ($pathCount paths, $operationCount operations)"
}
finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id
        $serverProcess.WaitForExit(5000) | Out-Null
    }

    $env:SERVITIUM_DATA_ROOT = $previousDataRoot
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTemporaryRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporaryRoot).StartsWith('servitium-openapi-', [System.StringComparison]::Ordinal)) {
        [System.IO.Directory]::Delete($resolvedTemporaryRoot, $true)
    }
}
