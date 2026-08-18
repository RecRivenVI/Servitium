[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$docsRoot = Join-Path $repositoryRoot 'docs'
$failures = [System.Collections.Generic.List[string]]::new()
$markdownFiles = @(Get-ChildItem -LiteralPath $docsRoot -Filter '*.md' -File -Recurse)

foreach ($file in $markdownFiles) {
    $relativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $file.FullName).Replace('\', '/')
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ([string]::IsNullOrWhiteSpace($content) -or ($content -split "`r?`n").Count -lt 3) {
        $failures.Add("Empty or title-only Markdown file: $relativePath")
    }

    $relativeDocsPath = [System.IO.Path]::GetRelativePath($docsRoot, $file.FullName).Replace('\', '/')
    foreach ($segment in $relativeDocsPath.Split('/')) {
        if ($segment -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.md)?$') {
            $failures.Add("Invalid docs path casing or name: $relativePath")
            break
        }
    }

    foreach ($linkMatch in [regex]::Matches($content, '\[[^\]]+\]\((?<target>[^)]+)\)')) {
        $target = $linkMatch.Groups['target'].Value.Trim()
        if ($target.StartsWith('http://', [System.StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith('mailto:', [System.StringComparison]::OrdinalIgnoreCase) -or
            $target.StartsWith('#', [System.StringComparison]::Ordinal)) {
            continue
        }

        $pathPart = $target.Split('#')[0]
        if ([string]::IsNullOrWhiteSpace($pathPart)) {
            continue
        }

        $decodedPath = [System.Uri]::UnescapeDataString($pathPart)
        $resolvedTarget = [System.IO.Path]::GetFullPath((Join-Path $file.DirectoryName $decodedPath))
        if (-not (Test-Path -LiteralPath $resolvedTarget)) {
            $failures.Add("Broken Markdown link in ${relativePath}: $target")
        }
    }
}

$functionalSpecPath = Join-Path $docsRoot 'product\functional-specification.md'
$functionalSpec = Get-Content -LiteralPath $functionalSpecPath -Raw
$definedRequirements = @([regex]::Matches($functionalSpec, '(?m)^### (REQ-[A-Z0-9]+-\d{3})\b') | ForEach-Object { $_.Groups[1].Value })
$definedRequirementSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($requirement in $definedRequirements) {
    [void]$definedRequirementSet.Add($requirement)
}
if ($definedRequirements.Count -ne $definedRequirementSet.Count) {
    $failures.Add('Duplicate requirement identifiers exist in functional-specification.md.')
}

$allDocsText = ($markdownFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join [Environment]::NewLine
$referencedRequirements = @([regex]::Matches($allDocsText, '\bREQ-[A-Z0-9]+-\d{3}\b') | ForEach-Object { $_.Value } | Sort-Object -Unique)
foreach ($requirement in $referencedRequirements) {
    if (-not $definedRequirementSet.Contains($requirement)) {
        $failures.Add("Undefined requirement identifier is referenced: $requirement")
    }
}

$traceability = Get-Content -LiteralPath (Join-Path $docsRoot 'testing\requirements-traceability.md') -Raw
$mappedRequirements = @([regex]::Matches($traceability, '(?m)^\| (REQ-[A-Z0-9]+-\d{3}) \|') | ForEach-Object { $_.Groups[1].Value })
$mappedRequirementSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($requirement in $mappedRequirements) {
    [void]$mappedRequirementSet.Add($requirement)
}
if (-not $mappedRequirementSet.SetEquals($definedRequirementSet)) {
    $failures.Add('requirements-traceability.md does not contain exactly one row for every requirement.')
}

$masterPlan = Get-Content -LiteralPath (Join-Path $docsRoot 'development\master-plan.md') -Raw
$expectedPhases = @([regex]::Matches($masterPlan, '`phases/(?<name>[a-z0-9-]+\.md)`') | ForEach-Object { $_.Groups['name'].Value } | Sort-Object -Unique)
$actualPhases = @(Get-ChildItem -LiteralPath (Join-Path $docsRoot 'development\phases') -Filter '*.md' -File | ForEach-Object Name | Sort-Object -Unique)
if ([string]::Join('|', $expectedPhases) -ne [string]::Join('|', $actualPhases)) {
    $failures.Add('Phase files do not match the master-plan stage list.')
}

$acceptanceSources = @(
    (Join-Path $docsRoot 'testing\backend-acceptance.md')
    (Join-Path $docsRoot 'testing\frontend-acceptance.md')
    (Join-Path $docsRoot 'testing\user-testing-procedure.md')
)
$definedAcceptanceIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($sourcePath in $acceptanceSources) {
    $sourceText = Get-Content -LiteralPath $sourcePath -Raw
    foreach ($match in [regex]::Matches($sourceText, '\b(?:BA-[A-Z]\d{2}|FA-[A-Z]\d{2}|U-\d{2,3})\b')) {
        [void]$definedAcceptanceIds.Add($match.Value)
    }
}

$acceptanceMatrix = Get-Content -LiteralPath (Join-Path $docsRoot 'testing\acceptance-matrix.md') -Raw
$coveredAcceptanceIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($acceptanceMatrix, '\b(?:BA-[A-Z]\d{2}|FA-[A-Z]\d{2}|U-\d{2,3})\b')) {
    [void]$coveredAcceptanceIds.Add($match.Value)
}

foreach ($range in [regex]::Matches($acceptanceMatrix, '(?<prefix>BA-[A-Z]|FA-[A-Z]|U-)(?<start>\d{2,3})\s*…\s*(?<endPrefix>BA-[A-Z]|FA-[A-Z]|U-)(?<end>\d{2,3})')) {
    $prefix = $range.Groups['prefix'].Value
    if ($prefix -ne $range.Groups['endPrefix'].Value) {
        $failures.Add("Acceptance range prefixes differ: $($range.Value)")
        continue
    }

    $start = [int]$range.Groups['start'].Value
    $end = [int]$range.Groups['end'].Value
    $width = $range.Groups['start'].Value.Length
    foreach ($number in $start..$end) {
        [void]$coveredAcceptanceIds.Add($prefix + $number.ToString("D$width", [System.Globalization.CultureInfo]::InvariantCulture))
    }
}

foreach ($acceptanceId in $definedAcceptanceIds) {
    if (-not $coveredAcceptanceIds.Contains($acceptanceId)) {
        $failures.Add("Acceptance item is not covered by acceptance-matrix.md: $acceptanceId")
    }
}

$formalRoots = @('product', 'architecture', 'design', 'engineering', 'development\phases')
$placeholderPattern = '待研究|待定|TODO|视情况|可能使用|参考 MCSManager|参考 HMCL 实现'
foreach ($formalRoot in $formalRoots) {
    $formalPath = Join-Path $docsRoot $formalRoot
    foreach ($file in Get-ChildItem -LiteralPath $formalPath -Filter '*.md' -File -Recurse) {
        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            $lineNumber++
            if ($line -match $placeholderPattern -and
                $line -notmatch '占位词|禁止提交未登记|正式产品文档中无') {
                $failures.Add("Ambiguous placeholder text in $($file.FullName):$lineNumber")
            }
        }
    }
}

$protectedPatterns = @(
    '^README\.md$', '^AGENTS\.md$', '^docs/index\.md$', '^docs/product/', '^docs/reference/',
    '^docs/architecture/', '^docs/design/', '^docs/engineering/',
    '^docs/development/development-workflow\.md$', '^docs/development/master-plan\.md$',
    '^docs/development/risk-register\.md$', '^docs/development/phases/',
    '^docs/testing/test-strategy\.md$', '^docs/testing/backend-acceptance\.md$',
    '^docs/testing/frontend-acceptance\.md$', '^docs/testing/user-testing-procedure\.md$',
    '^docs/delivery/delivery-plan\.md$'
)
$changedFiles = @(& git -C $repositoryRoot 'diff' '--name-only' 'HEAD' '--')
if ($LASTEXITCODE -ne 0) {
    $failures.Add('Could not inspect protected-document changes with git.')
}
else {
    $approvedRequests = Get-Content -LiteralPath (Join-Path $docsRoot 'records\change-requests.md') -Raw
    foreach ($changedFile in $changedFiles) {
        $normalized = $changedFile.Replace('\', '/')
        if ($protectedPatterns | Where-Object { $normalized -match $_ }) {
            if (-not $approvedRequests.Contains('已批准', [System.StringComparison]::Ordinal)) {
                $failures.Add("Protected document changed without an approved change request: $normalized")
            }
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Documentation consistency check failed with $($failures.Count) issue(s)."
}

Write-Output "Documentation consistency check passed: $($markdownFiles.Count) Markdown files, $($definedRequirementSet.Count) requirements, and $($definedAcceptanceIds.Count) acceptance items."
