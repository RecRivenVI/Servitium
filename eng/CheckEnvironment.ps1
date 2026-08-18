[CmdletBinding()]
param(
    [ValidateSet('Development', 'BackendGate', 'FrontendGate', 'Delivery')]
    [string]$Profile = 'Development'
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()

if (-not $IsWindows) {
    $failures.Add('Servitium development requires Windows.')
}

if ($PSVersionTable.PSVersion.Major -lt 7) {
    $failures.Add("PowerShell 7 or later is required; found $($PSVersionTable.PSVersion).")
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$globalJson = Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json
$requiredSdk = [string]$globalJson.sdk.version
$installedSdks = @(& dotnet '--list-sdks')
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks | Where-Object { $_.StartsWith("$requiredSdk ", [System.StringComparison]::Ordinal) })) {
    $failures.Add(".NET SDK $requiredSdk is required.")
}

$nodeVersionText = (& node '--version' 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($nodeVersionText)) {
    $failures.Add('Node.js 22 LTS or later is required.')
}
else {
    $nodeVersion = [version]$nodeVersionText.TrimStart('v')
    if ($nodeVersion.Major -lt 22) {
        $failures.Add("Node.js 22 LTS or later is required; found $nodeVersionText.")
    }
}

$javaVersionLines = @(& java '-version' 2>&1)
if ($LASTEXITCODE -ne 0 -or -not ($javaVersionLines -join ' ').Contains('64-Bit', [System.StringComparison]::OrdinalIgnoreCase)) {
    $failures.Add('A working 64-bit Java runtime is required for test fixtures.')
}

$webViewPath = Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Microsoft\EdgeWebView\Application' -Filter 'msedgewebview2.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $webViewPath) {
    $failures.Add('Microsoft Edge WebView2 Evergreen Runtime is required.')
}

$windowsAppRuntime = Get-AppxPackage | Where-Object {
    $_.Name -like 'Microsoft.WindowsAppRuntime.*' -and $_.Architecture -eq 'X64'
} | Select-Object -First 1
if ($null -eq $windowsAppRuntime) {
    $failures.Add('An x64 Windows App Runtime package is required for WinUI 3.')
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Environment check failed for profile $Profile."
}

Write-Output "Environment check passed for profile $Profile."
Write-Output "PowerShell=$($PSVersionTable.PSVersion) SDK=$requiredSdk Node=$nodeVersionText Java=$($javaVersionLines[0])"
