[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$developmentRoot = Join-Path $env:LOCALAPPDATA 'Servitium.Dev'
$serverDataRoot = Join-Path $developmentRoot 'Server'
$agentDataRoot = Join-Path $developmentRoot 'Agent'
New-Item -ItemType Directory -Path (Join-Path $serverDataRoot 'logs') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $agentDataRoot 'logs') -Force | Out-Null

$serverProcess = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', (Join-Path $repositoryRoot 'src\Servitium.Server'), '--no-launch-profile', '--', '--environment', 'Development') -WorkingDirectory $repositoryRoot -Environment @{
    SERVITIUM_DATA_ROOT = $serverDataRoot
    ASPNETCORE_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = 'http://127.0.0.1:5344'
} -RedirectStandardOutput (Join-Path $serverDataRoot 'logs\server-console.log') -RedirectStandardError (Join-Path $serverDataRoot 'logs\server-error.log') -WindowStyle Hidden -PassThru

$agentProcess = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--project', (Join-Path $repositoryRoot 'src\Servitium.Agent'), '--', '--environment', 'Development') -WorkingDirectory $repositoryRoot -Environment @{
    SERVITIUM_DATA_ROOT = $agentDataRoot
    DOTNET_ENVIRONMENT = 'Development'
} -RedirectStandardOutput (Join-Path $agentDataRoot 'logs\agent-console.log') -RedirectStandardError (Join-Path $agentDataRoot 'logs\agent-error.log') -WindowStyle Hidden -PassThru

try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    $ready = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri 'http://127.0.0.1:5344/health/ready' -NoProxy -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            if ($serverProcess.HasExited) {
                throw "Servitium.Server exited with code $($serverProcess.ExitCode)."
            }
        }

        Start-Sleep -Milliseconds 200
    }

    if (-not $ready) {
        throw 'Servitium.Server did not become ready within 30 seconds.'
    }

    Write-Output "Servitium.Server PID=$($serverProcess.Id) URL=http://127.0.0.1:5344 Data=$serverDataRoot"
    Write-Output "Servitium.Agent PID=$($agentProcess.Id) Data=$agentDataRoot"
    Write-Output 'Press Ctrl+C to stop both processes.'
    Wait-Process -Id @($serverProcess.Id, $agentProcess.Id)
}
finally {
    foreach ($process in @($agentProcess, $serverProcess)) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id
        }
    }
}
