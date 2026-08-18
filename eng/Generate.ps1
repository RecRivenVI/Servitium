[CmdletBinding()]
param(
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$contractRoot = Join-Path $repositoryRoot 'src\Servitium.Contracts'
$manifestPath = Join-Path $contractRoot 'error-codes.json'
$sourcePath = Join-Path $contractRoot 'ErrorCodes.cs'
$protoPath = Join-Path $contractRoot 'Protos\agent-control.proto'
$zhHansPath = Join-Path $contractRoot 'Localization\zh-Hans.json'
$enUsPath = Join-Path $contractRoot 'Localization\en-US.json'
$uiContractPath = Join-Path $contractRoot 'Ui\ui-contract.json'
$informationArchitecturePath = Join-Path $repositoryRoot 'docs\design\information-architecture.md'
$protocolPath = Join-Path $repositoryRoot 'docs\architecture\protocols-and-contracts.md'
$eventSourcePath = Join-Path $contractRoot 'Events\EventContracts.cs'
$desktopResourceRoot = Join-Path $repositoryRoot 'src\Servitium.Desktop\Resources'
$openApiPath = Join-Path $repositoryRoot 'artifacts\openapi\swagger.json'
$generatedProtocolPath = Join-Path $repositoryRoot 'src\Servitium.Client\Generated\GeneratedProtocolContract.g.cs'

$manifestValues = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
$manifestSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($value in $manifestValues) {
    if (-not $manifestSet.Add([string]$value)) {
        throw "Duplicate error code in manifest: $value"
    }
}

$source = Get-Content -LiteralPath $sourcePath -Raw
$sourceMatches = [regex]::Matches($source, 'public const string \w+ = "([a-z0-9-]+)";')
$sourceSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($match in $sourceMatches) {
    if (-not $sourceSet.Add($match.Groups[1].Value)) {
        throw "Duplicate error-code constant value: $($match.Groups[1].Value)"
    }
}

if (-not $sourceSet.SetEquals($manifestSet)) {
    $missingInSource = @($manifestSet | Where-Object { -not $sourceSet.Contains($_) })
    $missingInManifest = @($sourceSet | Where-Object { -not $manifestSet.Contains($_) })
    throw "Error-code sources differ. MissingInSource=$($missingInSource -join ',') MissingInManifest=$($missingInManifest -join ',')"
}

$proto = Get-Content -LiteralPath $protoPath -Raw
if (-not $proto.Contains('syntax = "proto3";', [System.StringComparison]::Ordinal) -or
    -not $proto.Contains('package servitium.agent.v1;', [System.StringComparison]::Ordinal) -or
    -not $proto.Contains('service AgentControl', [System.StringComparison]::Ordinal) -or
    -not $proto.Contains('rpc Connect(stream AgentMessage) returns (stream ServerMessage);', [System.StringComparison]::Ordinal)) {
    throw 'The AgentControl protobuf does not declare the required syntax, package, and bidirectional service.'
}

$protocol = Get-Content -LiteralPath $protocolPath -Raw
$commandSection = $protocol.Substring(
    $protocol.IndexOf('### 6.4', [System.StringComparison]::Ordinal),
    $protocol.IndexOf('### 6.5', [System.StringComparison]::Ordinal) - $protocol.IndexOf('### 6.4', [System.StringComparison]::Ordinal))
$requiredCommands = @($commandSection -split "`r?`n" | Where-Object { $_ -match '^\|' } | ForEach-Object {
    [regex]::Matches($_, '`([A-Z][A-Za-z]+)`') | ForEach-Object { $_.Groups[1].Value }
})
foreach ($command in $requiredCommands) {
    $enumName = [regex]::Replace($command, '(?<!^)([A-Z])', '_$1').ToUpperInvariant()
    if (-not $proto.Contains("$enumName =", [System.StringComparison]::Ordinal)) {
        throw "The AgentControl protobuf is missing command $command ($enumName)."
    }
}

$zhHans = Get-Content -LiteralPath $zhHansPath -Raw | ConvertFrom-Json -AsHashtable
$enUs = Get-Content -LiteralPath $enUsPath -Raw | ConvertFrom-Json -AsHashtable
$zhKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$enKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($key in $zhHans.Keys) { $null = $zhKeys.Add([string]$key) }
foreach ($key in $enUs.Keys) { $null = $enKeys.Add([string]$key) }
if (-not $zhKeys.SetEquals($enKeys)) {
    throw 'The zh-Hans and en-US localization key sets differ.'
}

foreach ($key in $zhKeys) {
    if ($key -cnotmatch '^[a-z]+\.[a-z0-9-]+(?:\.[a-z0-9-]+)*$') {
        throw "Invalid localization key: $key"
    }
    if ([string]::IsNullOrWhiteSpace([string]$zhHans[$key]) -or [string]::IsNullOrWhiteSpace([string]$enUs[$key])) {
        throw "Localization values cannot be empty: $key"
    }
}

foreach ($errorCode in $manifestSet) {
    if (-not $zhKeys.Contains("error.$errorCode")) {
        throw "Missing localized error code: error.$errorCode"
    }
}

$uiContract = Get-Content -LiteralPath $uiContractPath -Raw | ConvertFrom-Json
$contractPageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($page in $uiContract.pages) {
    if (-not $contractPageIds.Add([string]$page.id)) {
        throw "Duplicate UI contract page: $($page.id)"
    }
    foreach ($textKey in $page.textKeys) {
        if (-not $zhKeys.Contains([string]$textKey)) {
            throw "UI contract page $($page.id) references an unknown localization key: $textKey"
        }
    }
}

$informationArchitecture = Get-Content -LiteralPath $informationArchitecturePath -Raw
$documentPageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($informationArchitecture, '(?m)^\| (PG-[0-9]{3}) \|')) {
    $null = $documentPageIds.Add($match.Groups[1].Value)
}
if (-not $contractPageIds.SetEquals($documentPageIds)) {
    throw 'The UI contract page identifiers differ from information-architecture.md.'
}

$eventSection = $protocol.Substring(
    $protocol.IndexOf('### 4.3', [System.StringComparison]::Ordinal),
    $protocol.IndexOf('### 4.4', [System.StringComparison]::Ordinal) - $protocol.IndexOf('### 4.3', [System.StringComparison]::Ordinal))
$documentEvents = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($eventSection, '`([a-z][a-z0-9.-]+)`')) {
    $null = $documentEvents.Add($match.Groups[1].Value)
}
$eventSource = Get-Content -LiteralPath $eventSourcePath -Raw
$sourceEvents = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($match in [regex]::Matches($eventSource, 'public const string \w+ = "([a-z][a-z0-9.-]+)";')) {
    $null = $sourceEvents.Add($match.Groups[1].Value)
}
if (-not $sourceEvents.SetEquals($documentEvents)) {
    throw 'The event type constants differ from protocols-and-contracts.md section 4.3.'
}

function New-ReswContent {
    param([System.Collections.IDictionary]$Values)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<?xml version="1.0" encoding="utf-8"?>')
    $lines.Add('<root>')
    $lines.Add('  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>')
    $lines.Add('  <resheader name="version"><value>2.0</value></resheader>')
    $lines.Add('  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms</value></resheader>')
    $lines.Add('  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms</value></resheader>')
    foreach ($key in @($Values.Keys | Sort-Object)) {
        $escapedKey = [System.Security.SecurityElement]::Escape([string]$key)
        $escapedValue = [System.Security.SecurityElement]::Escape([string]$Values[$key])
        $lines.Add("  <data name=`"$escapedKey`" xml:space=`"preserve`"><value>$escapedValue</value></data>")
    }
    $lines.Add('</root>')
    return ($lines -join "`n") + "`n"
}

$generatedResources = @{
    'zh-Hans' = New-ReswContent $zhHans
    'en-US' = New-ReswContent $enUs
}
foreach ($language in $generatedResources.Keys) {
    $directory = Join-Path $desktopResourceRoot $language
    $path = Join-Path $directory 'Resources.resw'
    $expected = $generatedResources[$language]
    if ($Verify) {
        if (-not (Test-Path -LiteralPath $path) -or (Get-Content -LiteralPath $path -Raw) -cne $expected) {
            throw "Generated Desktop resource is stale: $path"
        }
    }
    else {
        $null = New-Item -ItemType Directory -Force -Path $directory
        Set-Content -LiteralPath $path -Value $expected -Encoding utf8NoBOM -NoNewline
    }
}

& (Join-Path $PSScriptRoot 'Export-OpenApi.ps1') -Configuration Release -OutputPath $openApiPath
if ($LASTEXITCODE -ne 0) {
    throw "OpenAPI export failed with exit code $LASTEXITCODE."
}

$openApi = Get-Content -LiteralPath $openApiPath -Raw | ConvertFrom-Json
$httpMethods = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('get', 'post', 'put', 'patch', 'delete', 'head', 'options'),
    [System.StringComparer]::OrdinalIgnoreCase)
$endpoints = [System.Collections.Generic.List[object]]::new()
$operationIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($pathProperty in $openApi.paths.PSObject.Properties) {
    foreach ($operation in $pathProperty.Value.PSObject.Properties) {
        if ($httpMethods.Contains($operation.Name)) {
            $operationId = [string]$operation.Value.operationId
            if ([string]::IsNullOrWhiteSpace($operationId) -or -not $operationIds.Add($operationId)) {
                throw "OpenAPI operationId is missing or duplicated: $($operation.Name.ToUpperInvariant()) $($pathProperty.Name) ($operationId)"
            }

            $successStatuses = [System.Collections.Generic.List[int]]::new()
            $responseContentTypes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($response in $operation.Value.responses.PSObject.Properties) {
                if ($response.Name -match '^[23][0-9][0-9]$') {
                    $successStatuses.Add([int]$response.Name)
                    if ($null -ne $response.Value.content) {
                        foreach ($content in $response.Value.content.PSObject.Properties) {
                            $null = $responseContentTypes.Add($content.Name)
                        }
                    }
                }
            }
            if ($successStatuses.Count -eq 0) {
                throw "OpenAPI operation has no success response: $($operation.Name.ToUpperInvariant()) $($pathProperty.Name)"
            }

            $problem = $operation.Value.responses.'400'.content.'application/problem+json'.schema
            if ($null -eq $problem -or $null -eq $problem.properties.code -or $null -eq $problem.properties.traceId -or
                $null -eq $problem.properties.detail -or $null -eq $problem.properties.errors) {
                throw "OpenAPI operation does not expose the complete Servitium Problem Details schema: $($operation.Name.ToUpperInvariant()) $($pathProperty.Name)"
            }

            $requestContentTypes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            if ($null -ne $operation.Value.requestBody -and $null -ne $operation.Value.requestBody.content) {
                foreach ($content in $operation.Value.requestBody.content.PSObject.Properties) {
                    $null = $requestContentTypes.Add($content.Name)
                }
            }
            $endpoints.Add([PSCustomObject]@{
                Method = $operation.Name.ToUpperInvariant()
                Path = $pathProperty.Name
                OperationId = $operationId
                SuccessStatuses = @($successStatuses | Sort-Object -Unique)
                RequestContentTypes = @($requestContentTypes | Sort-Object)
                ResponseContentTypes = @($responseContentTypes | Sort-Object)
            })
        }
    }
}

$protocolLines = [System.Collections.Generic.List[string]]::new()
$protocolLines.Add('// <auto-generated />')
$protocolLines.Add('using System;')
$protocolLines.Add('')
$protocolLines.Add('namespace Servitium.Client.Generated;')
$protocolLines.Add('')
$protocolLines.Add('internal static class GeneratedProtocolContract')
$protocolLines.Add('{')
$protocolLines.Add('    private static readonly Endpoint[] Endpoints =')
$protocolLines.Add('    [')
foreach ($endpoint in @($endpoints | Sort-Object Method, Path)) {
    $method = ([string]$endpoint.Method).Replace('"', '\"', [System.StringComparison]::Ordinal)
    $path = ([string]$endpoint.Path).Replace('\', '\\', [System.StringComparison]::Ordinal).Replace('"', '\"', [System.StringComparison]::Ordinal)
    $operationId = ([string]$endpoint.OperationId).Replace('"', '\"', [System.StringComparison]::Ordinal)
    $successStatuses = @($endpoint.SuccessStatuses) -join ', '
    $requestContentTypes = @($endpoint.RequestContentTypes | ForEach-Object { '"' + ([string]$_).Replace('"', '\"', [System.StringComparison]::Ordinal) + '"' }) -join ', '
    $responseContentTypes = @($endpoint.ResponseContentTypes | ForEach-Object { '"' + ([string]$_).Replace('"', '\"', [System.StringComparison]::Ordinal) + '"' }) -join ', '
    $protocolLines.Add("        new(`"$method`", `"$path`", `"$operationId`", [$successStatuses], [$requestContentTypes], [$responseContentTypes]),")
}
$protocolLines.Add('    ];')
$protocolLines.Add('')
$protocolLines.Add('    internal static int OperationCount => Endpoints.Length;')
$protocolLines.Add('')
$protocolLines.Add('    internal static bool Contains(string method, string requestPath)')
$protocolLines.Add('    {')
$protocolLines.Add('        ArgumentException.ThrowIfNullOrWhiteSpace(method);')
$protocolLines.Add('        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);')
$protocolLines.Add("        int queryStart = requestPath.IndexOf('?', StringComparison.Ordinal);")
$protocolLines.Add('        string path = queryStart < 0 ? requestPath : requestPath[..queryStart];')
$protocolLines.Add('        foreach (Endpoint endpoint in Endpoints)')
$protocolLines.Add('        {')
$protocolLines.Add('            if (string.Equals(endpoint.Method, method, StringComparison.OrdinalIgnoreCase) && Matches(endpoint.Path, path))')
$protocolLines.Add('            {')
$protocolLines.Add('                return true;')
$protocolLines.Add('            }')
$protocolLines.Add('        }')
$protocolLines.Add('')
$protocolLines.Add('        return false;')
$protocolLines.Add('    }')
$protocolLines.Add('')
$protocolLines.Add('    internal static bool ContainsOperationId(string operationId)')
$protocolLines.Add('    {')
$protocolLines.Add('        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);')
$protocolLines.Add('        foreach (Endpoint endpoint in Endpoints)')
$protocolLines.Add('        {')
$protocolLines.Add('            if (string.Equals(endpoint.OperationId, operationId, StringComparison.Ordinal))')
$protocolLines.Add('            {')
$protocolLines.Add('                return true;')
$protocolLines.Add('            }')
$protocolLines.Add('        }')
$protocolLines.Add('')
$protocolLines.Add('        return false;')
$protocolLines.Add('    }')
$protocolLines.Add('')
$protocolLines.Add('    internal static bool AllowsSuccessStatusCode(string method, string requestPath, int statusCode)')
$protocolLines.Add('    {')
$protocolLines.Add('        Endpoint? endpoint = Find(method, requestPath);')
$protocolLines.Add('        return endpoint is not null && Array.IndexOf(endpoint.Value.SuccessStatusCodes, statusCode) >= 0;')
$protocolLines.Add('    }')
$protocolLines.Add('')
$protocolLines.Add('    internal static bool AcceptsRequestContentType(string method, string requestPath, string contentType)')
$protocolLines.Add('    {')
$protocolLines.Add('        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);')
$protocolLines.Add('        Endpoint? endpoint = Find(method, requestPath);')
$protocolLines.Add('        return endpoint is not null && Array.Exists(endpoint.Value.RequestContentTypes, item => string.Equals(item, contentType, StringComparison.OrdinalIgnoreCase));')
$protocolLines.Add('    }')
$protocolLines.Add('')
$protocolLines.Add('    internal static bool ProducesResponseContentType(string method, string requestPath, string contentType)')
$protocolLines.Add('    {')
$protocolLines.Add('        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);')
$protocolLines.Add('        Endpoint? endpoint = Find(method, requestPath);')
$protocolLines.Add('        return endpoint is not null && Array.Exists(endpoint.Value.ResponseContentTypes, item => string.Equals(item, contentType, StringComparison.OrdinalIgnoreCase));')
$protocolLines.Add('    }')
$protocolLines.Add('')
$protocolLines.Add('    private static Endpoint? Find(string method, string requestPath)')
$protocolLines.Add('    {')
$protocolLines.Add('        ArgumentException.ThrowIfNullOrWhiteSpace(method);')
$protocolLines.Add('        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);')
$protocolLines.Add("        int queryStart = requestPath.IndexOf('?', StringComparison.Ordinal);")
$protocolLines.Add('        string path = queryStart < 0 ? requestPath : requestPath[..queryStart];')
$protocolLines.Add('        foreach (Endpoint endpoint in Endpoints)')
$protocolLines.Add('        {')
$protocolLines.Add('            if (string.Equals(endpoint.Method, method, StringComparison.OrdinalIgnoreCase) && Matches(endpoint.Path, path))')
$protocolLines.Add('            {')
$protocolLines.Add('                return endpoint;')
$protocolLines.Add('            }')
$protocolLines.Add('        }')
$protocolLines.Add('')
$protocolLines.Add('        return null;')
$protocolLines.Add('    }')
$protocolLines.Add('')
$protocolLines.Add('    private static bool Matches(string template, string path)')
$protocolLines.Add('    {')
$protocolLines.Add("        string[] expected = template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);")
$protocolLines.Add("        string[] actual = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);")
$protocolLines.Add('        if (expected.Length != actual.Length)')
$protocolLines.Add('        {')
$protocolLines.Add('            return false;')
$protocolLines.Add('        }')
$protocolLines.Add('')
$protocolLines.Add('        for (int index = 0; index < expected.Length; index++)')
$protocolLines.Add('        {')
$protocolLines.Add("            bool parameter = expected[index].StartsWith('{') && expected[index].EndsWith('}');")
$protocolLines.Add('            if (!parameter && !string.Equals(expected[index], actual[index], StringComparison.Ordinal))')
$protocolLines.Add('            {')
$protocolLines.Add('                return false;')
$protocolLines.Add('            }')
$protocolLines.Add('        }')
$protocolLines.Add('')
$protocolLines.Add('        return true;')
$protocolLines.Add('    }')
$protocolLines.Add('')
$protocolLines.Add('    private readonly record struct Endpoint(')
$protocolLines.Add('        string Method,')
$protocolLines.Add('        string Path,')
$protocolLines.Add('        string OperationId,')
$protocolLines.Add('        int[] SuccessStatusCodes,')
$protocolLines.Add('        string[] RequestContentTypes,')
$protocolLines.Add('        string[] ResponseContentTypes);')
$protocolLines.Add('}')
$protocolContent = ($protocolLines -join "`n") + "`n"
if ($Verify) {
    if (-not (Test-Path -LiteralPath $generatedProtocolPath) -or (Get-Content -LiteralPath $generatedProtocolPath -Raw) -cne $protocolContent) {
        throw "Generated protocol contract is stale: $generatedProtocolPath"
    }
}
else {
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $generatedProtocolPath)
    Set-Content -LiteralPath $generatedProtocolPath -Value $protocolContent -Encoding utf8NoBOM -NoNewline
}

$mode = if ($Verify) { 'verify' } else { 'check' }
Write-Output "Generation $mode passed: $($manifestSet.Count) error codes, $($zhKeys.Count) localization keys, $($contractPageIds.Count) pages, $($sourceEvents.Count) events, $($requiredCommands.Count) Agent commands, $($endpoints.Count) OpenAPI operations, and $($generatedResources.Count) Desktop resource sets are consistent."
