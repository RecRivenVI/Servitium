function Get-DevelopmentContext {
    $repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $localPath = Join-Path $PSScriptRoot 'Local.props'
    if (-not (Test-Path -LiteralPath $localPath)) {
        throw 'Copy tools/build/Local.props.example to tools/build/Local.props and configure the external artifacts root.'
    }
    [xml]$configuration = Get-Content -LiteralPath $localPath -Raw
    $dotnet = [string]$configuration.Project.PropertyGroup.ServitiumDotnetExe
    $artifactsRoot = [string]$configuration.Project.PropertyGroup.ServitiumArtifactsRoot
    if ([string]::IsNullOrWhiteSpace($dotnet) -or -not [IO.Path]::IsPathFullyQualified($artifactsRoot)) {
        throw 'Local.props must define ServitiumDotnetExe and an absolute ServitiumArtifactsRoot outside the checkout.'
    }
    $artifactsRoot = [IO.Path]::GetFullPath($artifactsRoot)
    if ($artifactsRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $artifactsRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ServitiumArtifactsRoot must be outside the checkout.'
    }
    [pscustomobject]@{ RepositoryRoot = $repositoryRoot; Dotnet = $dotnet; ArtifactsRoot = $artifactsRoot }
}
