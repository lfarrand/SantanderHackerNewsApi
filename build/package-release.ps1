#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Commit,
    [string]$ApiImage,
    [string]$WebImage,
    [string]$BlazorImage,
    [string[]]$TestedStackMetadata,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release/Release.Common.ps1')

function Copy-ReleaseApplication([string]$Service, [string]$ImageId, [string]$Destination, [string]$TemporaryDirectory) {
    $container = 'hn-package-' + [guid]::NewGuid().ToString('N')
    $created = $false
    $failure = $null
    try {
        # No process is started and no network, bind mount, or application volume is used.
        $null = Invoke-ReleaseDocker @('create', '--pull', 'never', '--network', 'none', '--name', $container, $ImageId)
        $created = $true
        $raw = Join-Path $TemporaryDirectory $container
        $null = [IO.Directory]::CreateDirectory($raw)
        $null = [IO.Directory]::CreateDirectory($Destination)
        if ($Service -eq 'web') {
            $null = [IO.Directory]::CreateDirectory((Join-Path $raw 'html'))
            $null = [IO.Directory]::CreateDirectory((Join-Path $raw 'nginx'))
            $null = Invoke-ReleaseDocker @('cp', "${container}:/usr/share/nginx/html/.", (Join-Path $raw 'html'))
            $null = Invoke-ReleaseDocker @('cp', "${container}:/etc/nginx/conf.d/default.conf", (Join-Path $raw 'nginx/default.conf'))
        }
        else { $null = Invoke-ReleaseDocker @('cp', "${container}:/app/.", $raw) }
        $app = if ($Service -eq 'web') { 'react' } else { $Service }
        foreach ($path in Get-ReleaseFiles $raw) {
            # Symbols, IIS configuration and runtime logs are not Linux application payloads.
            if ($path -match '^logs(/|$)|\.pdb$' -or $path -ceq 'web.config' -or ($Service -eq 'web' -and $path -ceq 'html/50x.html')) { continue }
            Assert-ReleasePayloadPath "applications/$app/$path" 'all-linux-amd64'
            $target = Join-Path $Destination $path
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy((Join-Path $raw $path), $target, $false)
        }
    }
    catch { $failure = $_ }
    finally {
        # The generated name is invocation-owned, including when create timed out after daemon acceptance.
        try {
            if ($created) { $null = Invoke-ReleaseDocker @('rm', '-v', $container) }
            else {
                $existing = Invoke-ReleaseDocker @('ps', '-aq', '--filter', "name=^/$container$")
                if ($existing) { $null = Invoke-ReleaseDocker @('rm', '-v', $container) }
            }
        }
        catch {
            if ($failure) { Write-Warning "Secondary container cleanup failure: $_" }
            else { $failure = $_ }
        }
    }
    if ($failure) { throw $failure }
}

function Invoke-ReleasePackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$ApiImage,
        [Parameter(Mandatory)][string]$WebImage,
        [Parameter(Mandatory)][string]$BlazorImage,
        [Parameter(Mandatory)][string[]]$TestedStackMetadata,
        [Parameter(Mandatory)][string]$OutputDirectory
    )
    Assert-ReleaseVersion $Version
    Assert-ReleaseCommit $Commit
    $ids = Get-ReleaseTestedIdentities $TestedStackMetadata
    $references = [ordered]@{ api = $ApiImage; web = $WebImage; blazor = $BlazorImage }
    $images = [ordered]@{}
    foreach ($service in $references.Keys) {
        $image = @(Invoke-ReleaseDocker @('image', 'inspect', $references[$service]) | ConvertFrom-Json)[0]
        Assert-ReleaseImage $image $references[$service] $ids[$service] $Version $Commit
        $images[$service] = [ordered]@{ tag = $references[$service]; id = $ids[$service] }
    }
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/releases'))
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $relative = [IO.Path]::GetRelativePath($root, $output)
    if ($relative -eq '.' -or $relative -match '^\.\.([/\\]|$)' -or [IO.Path]::IsPathRooted($relative)) { throw 'Output directory must be beneath artifacts/releases/.' }
    if (Test-Path -LiteralPath $output) { throw 'Output directory must not exist; refusing stale or partial bundles.' }
    # Reject redirected ancestors before creating or moving anything.
    for ($parent = [IO.DirectoryInfo]::new($output); $null -ne $parent; $parent = $parent.Parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Output ancestors must not be links.' }
    }
    $work = Join-Path $root ('.package-' + [guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($work)
    $failure = $null
    try {
        $payload = Join-Path $work 'payload'
        foreach ($service in $references.Keys) {
            $app = if ($service -eq 'web') { 'react' } else { $service }
            Copy-ReleaseApplication $service $ids[$service] (Join-Path $payload "applications/$app") $work
            $null = [IO.Directory]::CreateDirectory((Join-Path $payload 'images'))
            $null = Invoke-ReleaseDocker @('save', '--output', (Join-Path $payload "images/$app.tar"), $references[$service]) -TimeoutSeconds 300
        }
        $compose = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release/compose.release.yml.template'))
        foreach ($service in $references.Keys) { $compose = $compose.Replace("@@$($service.ToUpperInvariant())@@", $references[$service]) }
        $usage = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release/README.md.template')).Replace('@@VERSION@@', $Version).Replace('@@COMMIT@@', $Commit)
        $bundles = Join-Path $work 'bundles'
        $null = [IO.Directory]::CreateDirectory($bundles)
        foreach ($name in Get-ReleaseAssetNames $Version) {
            $kind = $name.Substring("hackernews-$Version-".Length) -creplace '\.zip$', ''
            $stage = Join-Path $work $kind
            $null = [IO.Directory]::CreateDirectory($stage)
            foreach ($path in Get-ReleaseFiles $payload) {
                $include = $kind -eq 'all-linux-amd64'
                foreach ($app in @('api', 'blazor', 'react')) {
                    $applicationKind = if ($app -eq 'react') { 'react' } else { "$app-linux-amd64" }
                    $include = $include -or ($kind -eq $applicationKind -and $path.StartsWith("applications/$app/", [StringComparison]::Ordinal)) -or ($kind -eq "$app-image-linux-amd64" -and $path -ceq "images/$app.tar")
                }
                if ($include) {
                    $target = Join-Path $stage $path
                    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                    [IO.File]::Copy((Join-Path $payload $path), $target)
                }
            }
            [IO.File]::WriteAllText((Join-Path $stage 'README.md'), $usage.Replace('@@KIND@@', $kind))
            [IO.File]::WriteAllText((Join-Path $stage 'compose.release.yml'), $compose)
            Write-ReleaseManifest $stage $kind $Version $Commit $images
            New-ReleaseZip $stage (Join-Path $bundles $name)
        }
        $checksums = @(foreach ($name in Get-ReleaseAssetNames $Version) { (Get-FileHash -LiteralPath (Join-Path $bundles $name) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name })
        [IO.File]::WriteAllLines((Join-Path $bundles 'SHA256SUMS'), $checksums)
        Assert-ReleaseBundleSet $bundles $Version $Commit
        foreach ($service in $references.Keys) {
            $image = @(Invoke-ReleaseDocker @('image', 'inspect', $references[$service]) | ConvertFrom-Json)[0]
            Assert-ReleaseImage $image $references[$service] $ids[$service] $Version $Commit
        }
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
        [IO.Directory]::Move($bundles, $output)
        Write-Host "Validated seven release bundles and SHA256SUMS: $output"
    }
    catch { $failure = $_ }
    finally {
        try { Remove-Item -LiteralPath $work -Recurse -Force }
        catch { if ($failure) { Write-Warning "Secondary staging cleanup failure: $_" } else { $failure = $_ } }
    }
    if ($failure) { throw $failure }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-ReleasePackage @PSBoundParameters }