[CmdletBinding()]
param([ValidateSet('Server', 'Agent', 'Desktop')][string]$Component = 'Server')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Development.ps1')
$context = Get-DevelopmentContext
$dataRoot = Join-Path $context.ArtifactsRoot "Runtime/Development/$Component"
$logs = Join-Path $dataRoot 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$project = Join-Path $context.RepositoryRoot "src/Servitium.$Component"
$arguments = @('run', '--project', ('"' + $project + '"'), '--no-launch-profile', '--', '--environment', 'Development')
$childEnvironment = @{ SERVITIUM_DATA_ROOT = $dataRoot; ASPNETCORE_ENVIRONMENT = 'Development'; DOTNET_ENVIRONMENT = 'Development' }
if ($Component -eq 'Server') { $childEnvironment.ASPNETCORE_URLS = 'http://127.0.0.1:5344' }
if ($Component -eq 'Desktop') {
    $childEnvironment.SERVITIUM_DESKTOP_TEST_DATA_ROOT = $dataRoot
    $arguments += '--isolated-test'
}
$process = Start-Process -FilePath $context.Dotnet -ArgumentList $arguments -WorkingDirectory $context.RepositoryRoot -Environment $childEnvironment -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'stdout.log') -RedirectStandardError (Join-Path $logs 'stderr.log')
Write-Output "Servitium.$Component PID=$($process.Id) Data=$dataRoot Logs=$logs"
Write-Output "Stop only this process with Stop-Process -Id $($process.Id)."
