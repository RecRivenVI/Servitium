[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('Development', 'BackendGate', 'FrontendGate', 'Delivery')]
    [string]$Profile = 'Development',
    [string[]]$Stages
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportRoot = Join-Path $repositoryRoot "artifacts\test-results\validate-$timestamp"
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

$registeredStages = [ordered]@{
    '1' = @{ Name = '环境检查'; Action = { & (Join-Path $PSScriptRoot 'CheckEnvironment.ps1') -Profile $Profile } }
    '2' = @{ Name = '锁定依赖还原'; Action = {
        & dotnet 'restore' (Join-Path $repositoryRoot 'Servitium.sln') '--locked-mode'
        if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    } }
    '3' = @{ Name = '生成代码一致性'; Action = { & (Join-Path $PSScriptRoot 'Generate.ps1') -Verify } }
    '4' = @{ Name = 'Release 构建'; Action = { & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration -NoRestore } }
    '5' = @{ Name = '分析器'; Action = {
        & dotnet 'build' (Join-Path $repositoryRoot 'Servitium.sln') '--configuration' $Configuration '--no-restore' '--maxcpucount:1' '/p:RunAnalyzers=true'
        if ($LASTEXITCODE -ne 0) { throw "Analyzer build failed with exit code $LASTEXITCODE." }
    } }
    '6' = @{ Name = '格式'; Action = {
        & dotnet 'format' (Join-Path $repositoryRoot 'Servitium.sln') '--verify-no-changes' '--no-restore' '--verbosity' 'quiet'
        if ($LASTEXITCODE -ne 0) { throw "Format verification failed with exit code $LASTEXITCODE." }
    } }
    '9' = @{ Name = '单元测试'; Action = { & (Join-Path $PSScriptRoot 'Test.ps1') -Category Unit -Configuration $Configuration } }
    '10' = @{ Name = '架构测试'; Action = { & (Join-Path $PSScriptRoot 'Test.ps1') -Category Architecture -Configuration $Configuration } }
    '11' = @{ Name = '集成测试'; Action = { & (Join-Path $PSScriptRoot 'Test.ps1') -Category Integration -Configuration $Configuration } }
    '17' = @{ Name = '覆盖率门禁'; Action = { & (Join-Path $PSScriptRoot 'Check-Coverage.ps1') -Configuration $Configuration } }
    '18' = @{ Name = '依赖漏洞'; Action = { & (Join-Path $PSScriptRoot 'Check-Dependencies.ps1') } }
    '21' = @{ Name = '文档一致性'; Action = { & (Join-Path $PSScriptRoot 'Check-Docs.ps1') } }
}

$selectedStageIds = if ($null -eq $Stages -or $Stages.Count -eq 0) {
    @($registeredStages.Keys)
}
else {
    @($Stages | ForEach-Object { $_.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) } | ForEach-Object { $_.Trim() })
}

foreach ($stageId in $selectedStageIds) {
    if (-not $registeredStages.Contains($stageId)) {
        throw "Stage $stageId is not registered in the current repository phase."
    }

    $stage = $registeredStages[$stageId]
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $logPath = Join-Path $reportRoot ("stage-{0}.log" -f $stageId.PadLeft(2, '0'))
    Write-Output "[$stageId] $($stage.Name)"
    try {
        & $stage.Action *>&1 | Tee-Object -FilePath $logPath
        $stopwatch.Stop()
        $results.Add([PSCustomObject]@{ Stage = $stageId; Name = $stage.Name; Result = '通过'; Duration = $stopwatch.Elapsed })
    }
    catch {
        $stopwatch.Stop()
        $_ | Out-String | Add-Content -LiteralPath $logPath
        $results.Add([PSCustomObject]@{ Stage = $stageId; Name = $stage.Name; Result = '失败'; Duration = $stopwatch.Elapsed })
        $results | Format-Table -AutoSize | Out-String | Write-Output
        Write-Error "Validation failed at stage $stageId ($($stage.Name)). Report: $reportRoot"
        throw
    }
}

$results | Format-Table -AutoSize | Out-String | Write-Output
Write-Output "Validation passed. Report: $reportRoot"
