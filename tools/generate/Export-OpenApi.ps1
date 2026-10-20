[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [string]$OutputPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Development.ps1')
$context = Get-DevelopmentContext
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $context.ArtifactsRoot 'Build/OpenApi/swagger.json' }
& $context.Dotnet build (Join-Path $context.RepositoryRoot 'src/Servitium.Server') --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "Server build exited with $LASTEXITCODE." }
$assembly = & $context.Dotnet msbuild (Join-Path $context.RepositoryRoot 'src/Servitium.Server/Servitium.Server.csproj') "-property:Configuration=$Configuration" -getProperty:TargetPath
if ($LASTEXITCODE -ne 0) { throw "Resolving Server output exited with $LASTEXITCODE." }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$dataRoot = Join-Path $context.ArtifactsRoot ('Runtime/Generation/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
$process = Start-Process -FilePath $context.Dotnet -ArgumentList @(('"' + $assembly + '"'), '--urls', "http://127.0.0.1:$port") -WorkingDirectory $context.RepositoryRoot -Environment @{ SERVITIUM_DATA_ROOT = $dataRoot; ASPNETCORE_ENVIRONMENT = 'Development' } -RedirectStandardOutput (Join-Path $dataRoot 'stdout.log') -RedirectStandardError (Join-Path $dataRoot 'stderr.log') -WindowStyle Hidden -PassThru
try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    $response = $null
    do {
        if ($process.HasExited) { throw "OpenAPI host exited with $($process.ExitCode). Logs: $dataRoot" }
        try { $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port/swagger/v1/swagger.json" -NoProxy -TimeoutSec 2 }
        catch [Net.Http.HttpRequestException] { }
        catch [OperationCanceledException] { }
        if ($null -eq $response) { Start-Sleep -Milliseconds 100 }
    } while ($null -eq $response -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $response) { throw "OpenAPI host did not become ready. Logs: $dataRoot" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
    [IO.File]::WriteAllText($OutputPath, $response.Content.TrimEnd() + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output $OutputPath
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
}
