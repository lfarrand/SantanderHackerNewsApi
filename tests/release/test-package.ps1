#requires -Version 7.4
param([scriptblock]$BundleAction, [string]$FixtureVersion = '1.2.3-rc.1')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/package-release.ps1')

function Assert-Rejected([string]$Name, [scriptblock]$Action, [string]$Message) {
    $caught = $null
    try { & $Action } catch { $caught = $_ }
    if (-not $caught -or $caught.ToString() -notlike "*$Message*") { throw "$Name expected '$Message', got '$caught'" }
    Write-Host "PASS $Name"
}

function Write-TestFile([string]$Path, [string]$Text = 'payload') {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text)
}

function New-TestTar([string]$Path, [string]$Service) {
    $stream = [IO.File]::Create($Path)
    $writer = [System.Formats.Tar.TarWriter]::new($stream)
    try {
        $configName = $script:inspections[$Service].Id.Substring(7) + '.json'
        $manifest = ConvertTo-Json -Depth 10 -InputObject @(@{ Config = $configName; RepoTags = @($script:inspections[$Service].RepoTags); Layers = @('layer.tar') })
        if ($script:fault -eq 'tar-tag') { $manifest = $manifest.Replace($script:inspections[$Service].RepoTags[0], 'wrong:1.2.3') }
        foreach ($item in @(@($configName, $script:configs[$Service]), @('manifest.json', $manifest), @('layer.tar', 'controlled layer'))) {
            if ($script:fault -eq 'tar-missing-layer' -and $item[0] -eq 'layer.tar') { continue }
            $entry = [System.Formats.Tar.UstarTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $item[0])
            $entry.DataStream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($item[1]))
            try { $writer.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
        }
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('../../artifacts/releases/test-' + [guid]::NewGuid().ToString('N'))))
$null = [IO.Directory]::CreateDirectory($root)
$script:inspections = @{}
$script:configs = @{}
$script:containers = @{}
$script:commands = [Collections.Generic.List[string]]::new()
$script:fault = ''
$version = $FixtureVersion
$commit = 'a' * 40
try {
    foreach ($service in @('api', 'web', 'blazor')) {
        $config = @{ os = 'linux'; architecture = 'amd64'; config = @{ Labels = @{ 'org.opencontainers.image.version' = $version; 'org.opencontainers.image.revision' = $commit }; Entrypoint = @($service) } } | ConvertTo-Json -Depth 10
        $script:configs[$service] = $config
        $id = 'sha256:' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($config))).ToLowerInvariant()
        $script:inspections[$service] = [pscustomobject]@{ Id = $id; Os = 'linux'; Architecture = 'amd64'; Config = ($config | ConvertFrom-Json).config; RepoTags = @("hackernews-$service`:$version") }
    }
    $ids = @{}
    foreach ($service in $script:inspections.Keys) { $ids[$service] = $script:inspections[$service].Id }
    $metadata = @(foreach ($scenario in @('Smoke', 'Browser')) {
        $path = Join-Path $root "$scenario.json"
        Write-TestFile $path (@{ scenario = $scenario; verified = $true; imageIds = $ids } | ConvertTo-Json)
        $path
    })
    $parameters = @{ Version = $version; Commit = $commit; ApiImage = "hackernews-api:$version"; WebImage = "hackernews-web:$version"; BlazorImage = "hackernews-blazor:$version"; TestedStackMetadata = $metadata; OutputDirectory = (Join-Path $root 'bundles') }

    # Controlled Docker boundary only; production extraction, identity and archive assertions remain intact.
    function Invoke-ReleaseDocker([string[]]$Arguments, [int]$TimeoutSeconds = 120) {
        $script:commands.Add($Arguments -join ' ')
        switch ($Arguments[0]) {
            'image' {
                $service = ($script:inspections.Keys | Where-Object { $Arguments[2] -in $script:inspections[$_].RepoTags })
                if (-not $service) { throw 'controlled missing image' }
                return ConvertTo-Json -Depth 10 -InputObject @($script:inspections[$service])
            }
            'create' {
                if ($Arguments[2] -ne 'never' -or $Arguments[4] -ne 'none') { throw 'Unsafe container creation.' }
                $script:containers[$Arguments[6]] = ($script:inspections.Keys | Where-Object { $script:inspections[$_].Id -eq $Arguments[7] })
                return $Arguments[6]
            }
            'cp' {
                if ($script:fault -in @('copy', 'copy-and-cleanup')) { throw 'controlled copy failure' }
                $service = $script:containers[$Arguments[1].Split(':')[0]]
                $destination = $Arguments[2]
                if ($service -eq 'web') {
                    if ($Arguments[1] -like '*/default.conf') { Write-TestFile $destination 'server { location /api/ { proxy_pass http://api:8080/; } }' }
                    else {
                        Write-TestFile (Join-Path $destination 'index.html') '<script src="/assets/main.js"></script>'
                        Write-TestFile (Join-Path $destination 'assets/main.js') 'console.log("release")'
                        Write-TestFile (Join-Path $destination '50x.html') 'excluded nginx default'
                    }
                }
                else {
                    $assembly = if ($service -eq 'api') { 'HackerNews.BestStories.Api' } else { 'HackerNews.BestStories.Blazor' }
                    foreach ($extension in @('dll', 'deps.json', 'runtimeconfig.json')) {
                        if ($script:fault -ne 'missing-output' -or $extension -ne 'dll') { Write-TestFile (Join-Path $destination "$assembly.$extension") }
                    }
                    Write-TestFile (Join-Path $destination 'appsettings.json') '{}'
                    Write-TestFile (Join-Path $destination "$assembly.pdb") 'excluded symbols'
                    Write-TestFile (Join-Path $destination 'web.config') 'excluded IIS config'
                    Write-TestFile (Join-Path $destination 'logs/runtime.log') 'excluded log'
                    if ($service -eq 'blazor') { Write-TestFile (Join-Path $destination 'wwwroot/app.css') 'body {}' }
                    if ($script:fault -eq 'forbidden') { Write-TestFile (Join-Path $destination 'credentials.json') 'forbidden' }
                }
                return ''
            }
            'save' {
                $service = ($script:inspections.Keys | Where-Object { $Arguments[3] -in $script:inspections[$_].RepoTags })
                New-TestTar $Arguments[2] $service
                return ''
            }
            'rm' {
                $script:containers.Remove($Arguments[2])
                if ($script:fault -in @('cleanup', 'copy-and-cleanup')) { throw 'controlled cleanup failure' }
                return ''
            }
            default { throw "Unexpected Docker operation: $($Arguments -join ' ')" }
        }
    }

    foreach ($bad in @('', 'v1.2.3', '1.2', '01.2.3', '1.2.3-01', '1.2.3+meta', '1.2.3;bad', "1.2.3`n")) {
        Assert-Rejected 'malformed-version' { Assert-ReleaseVersion $bad } 'Invalid normalized'
    }
    foreach ($good in @('0.0.0', '1.2.3', $version, '1.2.3-0.a-1')) { Assert-ReleaseVersion $good }
    if ((Get-ReleaseVersion 'v1.2.3') -cne '1.2.3') { throw 'Tag normalization failed.' }
    foreach ($path in @('../evil', '/root', 'a\b', 'a//b', 'C:/evil', 'a/../b', 'applications/api/credentials.json', 'applications/api/testhost.dll', 'applications/api/logs/a.log', 'applications/react/html/node_modules/a.js')) {
        Assert-Rejected 'unsafe-path' { Assert-ReleasePayloadPath $path 'all-linux-amd64' } 'payload'
    }
    Assert-Rejected 'missing-evidence' { Get-ReleaseTestedIdentities @($metadata[0]) } 'exactly two'
    Assert-Rejected 'duplicate-evidence' { Get-ReleaseTestedIdentities @($metadata[0], $metadata[0]) } 'distinct verified'
    $original = [IO.File]::ReadAllText($metadata[1])
    Write-TestFile $metadata[1] ($original.Replace('true', 'false'))
    Assert-Rejected 'unverified-evidence' { Invoke-ReleasePackage @parameters } 'distinct verified'
    Write-TestFile $metadata[1] ($original.Replace($ids.api, ('sha256:' + ('b' * 64))))
    Assert-Rejected 'identity-evidence' { Invoke-ReleasePackage @parameters } 'identity mismatch'
    Write-TestFile $metadata[1] $original
    $image = $script:inspections.api
    Assert-Rejected 'image-identity' { Assert-ReleaseImage $image $parameters.ApiImage ('sha256:' + ('b' * 64)) $version $commit } 'identity mismatch'
    $image.Architecture = 'arm64'
    Assert-Rejected 'image-platform' { Invoke-ReleasePackage @parameters } 'platform mismatch'
    $image.Architecture = 'amd64'
    $image.Config.Labels.'org.opencontainers.image.version' = '9.9.9'
    Assert-Rejected 'image-label' { Invoke-ReleasePackage @parameters } 'label mismatch'
    $image.Config.Labels.'org.opencontainers.image.version' = $version
    $image.Config.Labels.'org.opencontainers.image.revision' = 'b' * 40
    Assert-Rejected 'revision-label' { Invoke-ReleasePackage @parameters } 'label mismatch'
    $image.Config.Labels.'org.opencontainers.image.revision' = $commit
    Assert-Rejected 'unversioned-tag' { Assert-ReleaseImage $image 'hackernews-api:latest' $ids.api $version $commit } 'versioned image tag'
    $invalid = $parameters.Clone()
    $invalid.WebImage = 'missing:1.2.3'
    Assert-Rejected 'missing-image' { Invoke-ReleasePackage @invalid } 'controlled missing image'
    $invalid = $parameters.Clone()
    $invalid.OutputDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../TestResults/release-escape'))
    Assert-Rejected 'output-escape' { Invoke-ReleasePackage @invalid } 'must be beneath'
    foreach ($case in @(@('missing-output', 'Missing required payload'), @('forbidden', 'forbidden payload'), @('copy', 'controlled copy failure'), @('cleanup', 'controlled cleanup failure'), @('copy-and-cleanup', 'controlled copy failure'), @('tar-tag', 'Image TAR tag mismatch'), @('tar-missing-layer', 'Missing image TAR layer'))) {
        $script:fault = $case[0]
        Assert-Rejected $case[0] { Invoke-ReleasePackage @parameters } $case[1]
        if ($script:containers.Count -ne 0 -or (Test-Path $parameters.OutputDirectory)) { throw 'Failure left containers or transferable bundles.' }
    }
    $script:fault = ''
    Invoke-ReleasePackage @parameters
    Assert-ReleaseBundleSet $parameters.OutputDirectory $version $commit
    if ($BundleAction) { & $BundleAction $parameters.OutputDirectory $version $commit }
    $expectedNames = @("hackernews-$version-api-linux-amd64.zip", "hackernews-$version-blazor-linux-amd64.zip", "hackernews-$version-react.zip", "hackernews-$version-api-image-linux-amd64.zip", "hackernews-$version-blazor-image-linux-amd64.zip", "hackernews-$version-react-image-linux-amd64.zip", "hackernews-$version-all-linux-amd64.zip", 'SHA256SUMS')
    if (@(Compare-Object -CaseSensitive $expectedNames @(Get-ReleaseFiles $parameters.OutputDirectory)).Count) { throw 'Incorrect exact asset names.' }
    Write-Host 'PASS seven-bundles'
    Assert-Rejected 'stale-output' { Invoke-ReleasePackage @parameters } 'must not exist'
    $zip = Join-Path $parameters.OutputDirectory "hackernews-$version-all-linux-amd64.zip"
    $clean = Join-Path $root 'clean'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $clean)
    $manifest = Assert-ReleaseZip $zip $version $commit
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $paths = [string[]]@($archive.Entries.FullName)
        $sorted = [string[]]$paths.Clone()
        [Array]::Sort($sorted, [StringComparer]::Ordinal)
        if (($paths -join '|') -cne ($sorted -join '|')) { throw 'Archive order is not stable.' }
        foreach ($entry in $archive.Entries) {
            if ($entry.LastWriteTime.Year -ne 2000 -or $entry.FullName -match 'web\.config|50x\.html|\.pdb|\.log|\.zip') { throw 'Unstable timestamp or excluded payload.' }
        }
    }
    finally { $archive.Dispose() }
    Write-Host 'PASS stable-layout-exclusions'
    foreach ($file in $manifest.payload) {
        if ((Get-FileHash -LiteralPath (Join-Path $clean $file.path)).Hash.ToLowerInvariant() -cne $file.sha256) { throw 'Clean extraction hash mismatch.' }
    }
    Write-Host 'PASS clean-extraction'
    Assert-Rejected 'size-boundary' { Assert-ReleaseZip $zip $version $commit (Get-Item $zip).Length } 'size limit'
    Write-TestFile (Join-Path $clean 'README.md') ('X' * (Get-Item (Join-Path $clean 'README.md')).Length)
    $badDirectory = Join-Path $root 'bad'
    $null = [IO.Directory]::CreateDirectory($badDirectory)
    $badZip = Join-Path $badDirectory ([IO.Path]::GetFileName($zip))
    New-ReleaseZip $clean $badZip
    Assert-Rejected 'payload-checksum' { Assert-ReleaseZip $badZip $version $commit } 'checksum mismatch'
    Write-TestFile (Join-Path $parameters.OutputDirectory 'SHA256SUMS') ('bad' + [IO.File]::ReadAllText((Join-Path $parameters.OutputDirectory 'SHA256SUMS')))
    Assert-Rejected 'asset-checksum' { Assert-ReleaseBundleSet $parameters.OutputDirectory $version $commit } 'Asset checksum mismatch'
    Remove-Item -LiteralPath $zip
    Assert-Rejected 'incomplete-set' { Assert-ReleaseBundleSet $parameters.OutputDirectory $version $commit } 'exactly seven'
    if (@($script:commands | Where-Object { $_ -match '^(build|pull|run|start|export) ' }).Count -or $script:containers.Count) { throw 'Unexpected mutation or container leak.' }
    Write-Host 'PASS no-build-no-start-owned-cleanup'
}
finally { Remove-Item -LiteralPath $root -Recurse -Force }