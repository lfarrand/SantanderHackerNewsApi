#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$ApiImage,
    [Parameter(Mandatory)][string]$WebImage,
    [Parameter(Mandatory)][string]$BlazorImage,
    [string[]]$TestedStackMetadata
)
$ErrorActionPreference = 'Stop'
$inputs = [ordered]@{ api = $ApiImage; web = $WebImage; blazor = $BlazorImage }
$evidence = $TestedStackMetadata
. (Join-Path $PSScriptRoot '../../build/package-release.ps1')

function ConvertTo-Canonical($Value) {
    if ($Value -is [Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object)) { $result[$key] = ConvertTo-Canonical $Value[$key] }
        return $result
    }
    if ($Value -is [array]) { return ,@($Value | ForEach-Object { ConvertTo-Canonical $_ }) }
    return $Value
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('../../artifacts/releases/image-test-' + [guid]::NewGuid().ToString('N'))))
$null = [IO.Directory]::CreateDirectory($root)
$ownedTags = [Collections.Generic.List[string]]::new()
$images = [ordered]@{}
$failure = $null
try {
    foreach ($service in $inputs.Keys) {
        $image = @(Invoke-ReleaseDocker @('image', 'inspect', $inputs[$service]) | ConvertFrom-Json)[0]
        $version = $image.Config.Labels.'org.opencontainers.image.version'
        $commit = $image.Config.Labels.'org.opencontainers.image.revision'
        Assert-ReleaseVersion $version
        Assert-ReleaseCommit $commit
        # Unique aliases never replace existing tags; remove only these aliases in finally.
        $tag = 'hn-package-test-' + [guid]::NewGuid().ToString('N') + "/${service}:$version"
        $null = Invoke-ReleaseDocker @('tag', $image.Id, $tag)
        $ownedTags.Add($tag)
        $images[$service] = @{ tag = $tag; id = $image.Id }
        $tagged = @(Invoke-ReleaseDocker @('image', 'inspect', $tag) | ConvertFrom-Json)[0]
        Assert-ReleaseImage $tagged $tag $image.Id $version $commit
        $app = if ($service -eq 'web') { 'react' } else { $service }
        $directory = Join-Path $root "applications/$app"
        Copy-ReleaseApplication $service $image.Id $directory $root
        foreach ($path in Get-ReleaseFiles $directory) { Assert-ReleasePayloadPath "applications/$app/$path" 'all-linux-amd64' }
        $kind = if ($service -eq 'web') { 'react' } else { "$service-linux-amd64" }
        foreach ($path in Get-ReleaseRequiredPaths $kind) {
            if ($path -clike 'applications/*' -and -not [IO.File]::Exists((Join-Path $root $path))) { throw "Missing extracted file: $path" }
        }
        Write-Host "PASS real-extraction-$service"
        $tar = Join-Path $root "$app.tar"
        $null = Invoke-ReleaseDocker @('save', '--output', $tar, $tag) -TimeoutSeconds 120
        $stream = [IO.File]::OpenRead($tar)
        try { Assert-ReleaseImageTar $stream $images[$service] $version $commit } finally { $stream.Dispose() }
        # Remove the owned alias first so load must actually restore its tag.
        $null = Invoke-ReleaseDocker @('image', 'rm', $tag)
        $null = Invoke-ReleaseDocker @('load', '--input', $tar) -TimeoutSeconds 120
        $loaded = @(Invoke-ReleaseDocker @('image', 'inspect', $tag) | ConvertFrom-Json)[0]
        Assert-ReleaseImage $loaded $tag $image.Id $version $commit
        if (($loaded.Config.Entrypoint | ConvertTo-Json -Compress) -cne ($image.Config.Entrypoint | ConvertTo-Json -Compress)) { throw 'Entrypoint changed on load.' }
        Write-Host "PASS docker-load-identity-$service"
    }
    $compose = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../build/release/compose.release.yml.template'))
    foreach ($service in $images.Keys) { $compose = $compose.Replace("@@$($service.ToUpperInvariant())@@", $images[$service].tag) }
    $composePath = Join-Path $root 'compose.release.yml'
    [IO.File]::WriteAllText($composePath, $compose)
    $release = Invoke-ReleaseDocker @('compose', '-p', 'hn-release-config-test', '-f', $composePath, 'config', '--format', 'json') | ConvertFrom-Json -AsHashtable
    $default = Invoke-ReleaseDocker @('compose', '-p', 'hn-release-config-test', '-f', (Join-Path $PSScriptRoot '../../docker-compose.yml'), 'config', '--format', 'json') | ConvertFrom-Json -AsHashtable
    if (($release.services.Keys | Sort-Object) -join ',' -cne 'api,blazor,web') { throw 'Unexpected release services.' }
    foreach ($service in @('api', 'web', 'blazor')) {
        if ($release.services[$service].ContainsKey('build') -or $release.services[$service].pull_policy -cne 'never' -or $release.services[$service].platform -cne 'linux/amd64') { throw 'Unsafe release service configuration.' }
        foreach ($key in @('ports', 'environment', 'networks', 'volumes', 'healthcheck', 'depends_on', 'restart')) {
            $expected = ConvertTo-Canonical $default.services[$service][$key] | ConvertTo-Json -Depth 30 -Compress
            $actual = ConvertTo-Canonical $release.services[$service][$key] | ConvertTo-Json -Depth 30 -Compress
            if ($actual -cne $expected) { throw "Runtime configuration differs: $service.$key" }
        }
    }
    $expected = ConvertTo-Canonical $default.networks['hn-backend'] | ConvertTo-Json -Depth 30 -Compress
    $actual = ConvertTo-Canonical $release.networks['hn-backend'] | ConvertTo-Json -Depth 30 -Compress
    if ($actual -cne $expected -or @($release.networks.Keys).Count -ne 1 -or @($release.volumes.Keys).Count -ne 1 -or -not $release.volumes.ContainsKey('api-logs')) { throw 'Release network/log-volume configuration differs.' }
    Write-Host 'PASS compose-runtime-parity-clean-directory'
    if ($evidence) {
        $bundles = Join-Path $root 'bundles'
        Invoke-ReleasePackage -Version $version -Commit $commit -ApiImage $images.api.tag -WebImage $images.web.tag -BlazorImage $images.blazor.tag -TestedStackMetadata $evidence -OutputDirectory $bundles
        Assert-ReleaseBundleSet $bundles $version $commit
        $clean = Join-Path $root 'combined-clean'
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $bundles "hackernews-$version-all-linux-amd64.zip"), $clean)
        $null = Invoke-ReleaseDocker @('compose', '-p', 'hn-release-config-test', '-f', (Join-Path $clean 'compose.release.yml'), 'config', '--quiet')
        Write-Host 'PASS real-seven-bundles'
    }
    else { Write-Host 'Component checks only: supply both verified stack reports to validate real seven-bundle packaging.' }
}
catch { $failure = $_ }
finally {
    foreach ($tag in $ownedTags) {
        try { $null = Invoke-ReleaseDocker @('image', 'rm', $tag) }
        catch { if ($failure) { Write-Warning "Secondary tag cleanup failure: $_" } else { $failure = $_ } }
    }
    try { Remove-Item -LiteralPath $root -Recurse -Force }
    catch { if ($failure) { Write-Warning "Secondary file cleanup failure: $_" } else { $failure = $_ } }
}
if ($failure) { throw $failure }