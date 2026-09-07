#requires -Version 7.4
Set-StrictMode -Version Latest

function Assert-ReleaseVersion([string]$Version) {
    if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?\z') {
        throw "Invalid normalized release version: $Version"
    }
}

function Get-ReleaseVersion([string]$Tag) {
    if (-not $Tag.StartsWith('v', [StringComparison]::Ordinal)) { throw 'Release tag must start with v.' }
    $version = $Tag.Substring(1)
    Assert-ReleaseVersion $version
    return $version
}

function Assert-ReleaseCommit([string]$Commit) {
    if ($Commit -cnotmatch '^[0-9a-f]{40}\z') { throw 'Commit must be a full lowercase Git SHA-1.' }
}

function Get-ReleaseAssetNames([string]$Version) {
    Assert-ReleaseVersion $Version
    foreach ($kind in @('api-linux-amd64', 'blazor-linux-amd64', 'react', 'api-image-linux-amd64', 'blazor-image-linux-amd64', 'react-image-linux-amd64', 'all-linux-amd64')) {
        "hackernews-$Version-$kind.zip"
    }
}

function Invoke-ReleaseDocker([string[]]$Arguments, [int]$TimeoutSeconds = 120) {
    $info = [Diagnostics.ProcessStartInfo]::new('docker')
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Cannot start docker.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
            throw "docker $($Arguments[0]) exceeded ${TimeoutSeconds}s."
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) { throw 'Docker streams did not close.' }
        if ($process.ExitCode -ne 0) { throw "docker $($Arguments -join ' ') failed ($($process.ExitCode)): $($stderr.Result)" }
        return $stdout.Result.Trim()
    }
    finally { $process.Dispose() }
}

function Assert-ReleaseImage($Image, [string]$Reference, [string]$Id, [string]$Version, [string]$Commit) {
    if ($Id -cnotmatch '^sha256:[0-9a-f]{64}\z' -or $Image.Id -cne $Id) { throw "Image identity mismatch: $Reference" }
    if ($Image.Os -cne 'linux' -or $Image.Architecture -cne 'amd64') { throw "Image platform mismatch: $Reference" }
    if ($Image.Config.Labels.'org.opencontainers.image.version' -cne $Version -or
        $Image.Config.Labels.'org.opencontainers.image.revision' -cne $Commit) { throw "Image label mismatch: $Reference" }
    if ($Reference -cnotmatch ('^[a-z0-9][a-z0-9./:_-]*:' + [regex]::Escape($Version) + '\z') -or $Reference -cnotin $Image.RepoTags) {
        throw "Expected explicit versioned image tag: $Reference"
    }
}

function Get-ReleaseTestedIdentities([string[]]$MetadataPaths) {
    if ($MetadataPaths.Count -ne 2) { throw 'Require exactly two verified stack reports: Smoke and Browser.' }
    $scenarios = @{}
    $ids = @{}
    foreach ($path in $MetadataPaths) {
        $metadata = [IO.File]::ReadAllText([IO.Path]::GetFullPath($path)) | ConvertFrom-Json
        if ($metadata.verified -isnot [bool] -or -not $metadata.verified -or $metadata.scenario -cnotin @('Smoke', 'Browser') -or $scenarios.ContainsKey($metadata.scenario)) {
            throw 'Require distinct verified Smoke and Browser stack reports.'
        }
        $scenarios[$metadata.scenario] = $true
        foreach ($service in @('api', 'web', 'blazor')) {
            $id = $metadata.imageIds.$service
            if ($id -cnotmatch '^sha256:[0-9a-f]{64}\z' -or ($ids.ContainsKey($service) -and $ids[$service] -cne $id)) { throw "Tested image identity mismatch: $service" }
            $ids[$service] = $id
        }
    }
    return $ids
}

function Assert-ReleasePath([string]$Path) {
    if ($Path -cnotmatch '^[A-Za-z0-9_@][A-Za-z0-9_@./+ -]*\z' -or $Path -match '(^|/)\.{1,2}(/|$)|//|[. ](/|$)' -or
        $Path -match '(?i)(^|/)(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|/|$)' -or
        $Path -match '(?i)(^|/)(logs?|TestResults|tests?|node_modules|obj|bin|\.git|\.env|credentials?|secrets?|cache|caches)(/|\.|$)|(?i)(testhost|xunit|coverlet|Microsoft\.Testing)|\.(pdb|cs|csproj|ps1|zip|log|pem|key|pfx)$') {
        throw "Unsafe or forbidden payload path: $Path"
    }
}

function Assert-ReleasePayloadPath([string]$Path, [string]$Kind) {
    Assert-ReleasePath $Path
    if ($Path -in @('README.md', 'compose.release.yml')) { return }
    $allowed = switch ($Kind) {
        'api-linux-amd64' { '^applications/api/' }
        'blazor-linux-amd64' { '^applications/blazor/' }
        'react' { '^applications/react/' }
        'api-image-linux-amd64' { '^images/api\.tar$' }
        'blazor-image-linux-amd64' { '^images/blazor\.tar$' }
        'react-image-linux-amd64' { '^images/react\.tar$' }
        'all-linux-amd64' { '^(applications/(api|blazor|react)/|images/(api|blazor|react)\.tar$)' }
        default { throw "Unknown bundle kind: $Kind" }
    }
    if ($Path -cnotmatch $allowed) { throw "Unexpected bundle payload: $Path" }
    if ($Path -cmatch '^applications/(api|blazor)/(.*)$') {
        $relative = $Matches[2]
        if ($relative -cnotmatch '^[^/]+\.(dll|deps\.json|runtimeconfig\.json|staticwebassets\.[a-z.]+\.json)$|^appsettings(?:\.[A-Za-z]+)?\.json$|^wwwroot/|^runtimes/[a-z0-9-]+/(native/[^/]+\.so|lib/net[0-9.]+/[^/]+\.dll)$|^[a-z]{2}(?:-[A-Za-z]+)?/[^/]+\.resources\.dll$') {
            throw "Non-publish application file: $Path"
        }
    }
    if ($Path -cmatch '^applications/react/(.*)$' -and $Matches[1] -cnotmatch '^(html/(index\.html|assets/[^/]+\.(js|css|woff2?|svg|png|jpg|webp)|[^/]+\.(svg|ico|png|txt))|nginx/default\.conf)$') {
        throw "Non-production React file: $Path"
    }
}

function Get-ReleaseRequiredPaths([string]$Kind) {
    'README.md'
    'compose.release.yml'
    foreach ($app in @('api', 'blazor')) {
        if ($Kind -eq "$app-linux-amd64" -or $Kind -eq 'all-linux-amd64') {
            $assembly = if ($app -eq 'api') { 'HackerNews.BestStories.Api' } else { 'HackerNews.BestStories.Blazor' }
            foreach ($extension in @('dll', 'deps.json', 'runtimeconfig.json')) { "applications/$app/$assembly.$extension" }
            if ($app -eq 'api') { "applications/$app/appsettings.json" }
        }
    }
    if ($Kind -in @('react', 'all-linux-amd64')) { 'applications/react/html/index.html'; 'applications/react/nginx/default.conf' }
    foreach ($app in @('api', 'blazor', 'react')) {
        if ($Kind -eq "$app-image-linux-amd64" -or $Kind -eq 'all-linux-amd64') { "images/$app.tar" }
    }
}

function Get-ReleaseFiles([string]$Directory) {
    $files = [Collections.Generic.List[string]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Payload links are forbidden: $($item.FullName)" }
        if (-not $item.PSIsContainer) { $files.Add([IO.Path]::GetRelativePath($Directory, $item.FullName).Replace('\', '/')) }
    }
    $files.Sort([StringComparer]::Ordinal)
    return $files.ToArray()
}

function Write-ReleaseManifest([string]$Directory, [string]$Kind, [string]$Version, [string]$Commit, $Images) {
    $payload = @(foreach ($path in Get-ReleaseFiles $Directory) {
        Assert-ReleasePayloadPath $path $Kind
        $file = Join-Path $Directory $path
        @{ path = $path; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $file).Length }
    })
    $manifest = [ordered]@{ schemaVersion = 1; bundleKind = $Kind; version = $Version; commit = $Commit; platform = 'linux/amd64'; images = $Images; payload = $payload }
    [IO.File]::WriteAllText((Join-Path $Directory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 10))
}

function New-ReleaseZip([string]$Directory, [string]$Destination) {
    $archive = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($path in Get-ReleaseFiles $Directory) {
            $entry = $archive.CreateEntry($path, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $inputStream = [IO.File]::OpenRead((Join-Path $Directory $path))
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Assert-ReleaseImageTar([IO.Stream]$Stream, $Source, [string]$Version, [string]$Commit) {
    $reader = [System.Formats.Tar.TarReader]::new($Stream, $true)
    $json = @{}
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $metadataBytes = 0L
    try {
        while ($null -ne ($entry = $reader.GetNextEntry())) {
            if ($entry.Name -match '^/|\\|(^|/)\.\.(/|$)' -or -not $paths.Add($entry.Name)) { throw 'Unsafe or duplicate image TAR path.' }
            if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile, [System.Formats.Tar.TarEntryType]::Directory)) { throw 'Unexpected image TAR entry type.' }
            if ($entry.Name -eq 'manifest.json' -or $entry.Name -match '^[0-9a-f]{64}\.json$' -or ($entry.Name -match '^blobs/sha256/[0-9a-f]{64}$' -and $entry.Length -le 1MB)) {
                $metadataBytes += $entry.Length
                if ($entry.Length -gt 16MB -or $entry.Length -le 0 -or $metadataBytes -gt 64MB) { throw 'Invalid image TAR metadata size.' }
                $memory = [IO.MemoryStream]::new()
                try { $entry.DataStream.CopyTo($memory); $json[$entry.Name] = $memory.ToArray() } finally { $memory.Dispose() }
            }
            if ($entry.Name -match '^blobs/sha256/([0-9a-f]{64})$') {
                $expectedHash = $Matches[1]
                $blobHash = if ($json.ContainsKey($entry.Name)) { [Security.Cryptography.SHA256]::HashData([byte[]]$json[$entry.Name]) } else { [Security.Cryptography.SHA256]::HashData($entry.DataStream) }
                if ([Convert]::ToHexString($blobHash).ToLowerInvariant() -cne $expectedHash) { throw 'Image TAR blob checksum mismatch.' }
            }
        }
        if (-not $json.ContainsKey('manifest.json')) { throw 'Missing docker save manifest.' }
        $manifest = @([Text.Encoding]::UTF8.GetString($json['manifest.json']) | ConvertFrom-Json)
        if ($manifest.Count -ne 1 -or @($manifest[0].RepoTags).Count -ne 1 -or $manifest[0].RepoTags[0] -cne $Source.tag) { throw 'Image TAR tag mismatch.' }
        $config = $manifest[0].Config
        if (-not $json.ContainsKey($config)) { throw 'Image TAR identity mismatch.' }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$json[$config])).ToLowerInvariant()
        if ('sha256:' + $hash -cne $Source.id) {
            # Containerd-backed Docker reports an OCI descriptor ID; classic Docker reports the config ID.
            $pending = [Collections.Generic.Queue[string]]::new()
            $pending.Enqueue($Source.id)
            $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $found = $false
            while ($pending.Count) {
                $digest = $pending.Dequeue()
                if (-not $visited.Add($digest)) { continue }
                if ($visited.Count -gt 32 -or $digest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'Invalid OCI descriptor graph.' }
                $blob = 'blobs/sha256/' + $digest.Substring(7)
                if (-not $json.ContainsKey($blob)) { throw 'Missing OCI identity descriptor.' }
                $descriptor = [Text.Encoding]::UTF8.GetString($json[$blob]) | ConvertFrom-Json -AsHashtable
                if ($descriptor.ContainsKey('manifests')) {
                    foreach ($child in $descriptor.manifests) {
                        if ($child.platform.os -ceq 'linux' -and $child.platform.architecture -ceq 'amd64') { $pending.Enqueue($child.digest) }
                    }
                }
                elseif ($descriptor.ContainsKey('config') -and $descriptor.config.digest -ceq ('sha256:' + $hash)) {
                    foreach ($layer in $descriptor.layers) { if (-not $paths.Contains('blobs/sha256/' + $layer.digest.Substring(7))) { throw 'Missing OCI layer.' } }
                    $found = $true
                }
            }
            if (-not $found) { throw 'Image TAR identity mismatch.' }
        }
        $image = [Text.Encoding]::UTF8.GetString($json[$config]) | ConvertFrom-Json
        if ($image.os -cne 'linux' -or $image.architecture -cne 'amd64' -or $image.config.Labels.'org.opencontainers.image.version' -cne $Version -or $image.config.Labels.'org.opencontainers.image.revision' -cne $Commit) { throw 'Image TAR platform/label mismatch.' }
        if (@($manifest[0].Layers).Count -eq 0) { throw 'Image TAR has no layers.' }
        foreach ($layer in $manifest[0].Layers) { if (-not $paths.Contains($layer)) { throw 'Missing image TAR layer.' } }
    }
    finally { $reader.Dispose() }
}

function Assert-ReleaseZip([string]$Path, [string]$Version, [string]$Commit, [ValidateRange(1, 2147483648)][long]$MaximumBytes = 2147483648) {
    Assert-ReleaseVersion $Version
    Assert-ReleaseCommit $Commit
    if ((Get-Item -LiteralPath $Path).Length -ge $MaximumBytes) { throw "ZIP size limit exceeded: $Path" }
    $name = [IO.Path]::GetFileName($Path)
    if ($name -cnotin @(Get-ReleaseAssetNames $Version)) { throw "Unexpected ZIP name: $name" }
    $kind = $name.Substring("hackernews-$Version-".Length) -creplace '\.zip$', ''
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -cne 'manifest.json') { Assert-ReleasePayloadPath $entry.FullName $kind }
            if (-not $entries.TryAdd($entry.FullName, $entry)) { throw "Duplicate archive path: $($entry.FullName)" }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Archive links are forbidden.' }
        }
        if (-not $entries.ContainsKey('manifest.json') -or $entries['manifest.json'].Length -gt 10MB) { throw 'Missing or oversized manifest.' }
        $reader = [IO.StreamReader]::new($entries['manifest.json'].Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.schemaVersion -ne 1 -or $manifest.bundleKind -cne $kind -or $manifest.version -cne $Version -or $manifest.commit -cne $Commit -or $manifest.platform -cne 'linux/amd64') { throw 'Manifest identity mismatch.' }
        if (@($manifest.images.PSObject.Properties).Count -ne 3) { throw 'Manifest must identify three source images.' }
        foreach ($service in @('api', 'web', 'blazor')) {
            $image = $manifest.images.$service
            if ($image.id -cnotmatch '^sha256:[0-9a-f]{64}\z' -or $image.tag -cnotmatch ('^[a-z0-9][a-z0-9./:_-]*:' + [regex]::Escape($Version) + '\z')) { throw 'Invalid manifest source image.' }
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $manifest.payload) {
            Assert-ReleasePayloadPath $file.path $kind
            if (-not $seen.Add($file.path) -or -not $entries.ContainsKey($file.path)) { throw 'Missing or duplicate manifest payload.' }
            $entry = $entries[$file.path]
            if ($entry.FullName -cne $file.path -or $entry.Length -ne $file.size -or $entry.Length -le 0) { throw "Payload size/path mismatch: $($file.path)" }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
            if ($file.sha256 -cne $hash) { throw "Payload checksum mismatch: $($file.path)" }
            if ($file.path -cmatch '^images/(api|blazor|react)\.tar$') {
                $service = if ($Matches[1] -eq 'react') { 'web' } else { $Matches[1] }
                $stream = $entry.Open()
                try { Assert-ReleaseImageTar $stream $manifest.images.$service $Version $Commit } finally { $stream.Dispose() }
            }
        }
        if ($entries.Count -ne $seen.Count + 1) { throw 'Unmanifested ZIP payload.' }
        foreach ($required in Get-ReleaseRequiredPaths $kind) { if (-not $seen.Contains($required)) { throw "Missing required payload: $required" } }
        if ($kind -in @('blazor-linux-amd64', 'all-linux-amd64') -and -not @($seen | Where-Object { $_ -clike 'applications/blazor/wwwroot/*' }).Count) { throw 'Missing Blazor static assets.' }
        if ($kind -in @('react', 'all-linux-amd64') -and -not @($seen | Where-Object { $_ -cmatch '^applications/react/html/assets/.+\.js$' }).Count) { throw 'Missing React JavaScript assets.' }
        return $manifest
    }
    finally { $archive.Dispose() }
}

function Assert-ReleaseBundleSet([string]$Directory, [string]$Version, [string]$Commit) {
    Assert-ReleaseVersion $Version
    Assert-ReleaseCommit $Commit
    $names = @(Get-ReleaseAssetNames $Version)
    $actual = @(Get-ReleaseFiles $Directory)
    if (@(Compare-Object -CaseSensitive ($names + 'SHA256SUMS') $actual).Count) { throw 'Release requires exactly seven ZIPs and SHA256SUMS.' }
    $lines = [IO.File]::ReadAllLines((Join-Path $Directory 'SHA256SUMS'))
    if ($lines.Count -ne 7) { throw 'Expected seven checksum records.' }
    $sources = $null
    $payloadHashes = @{}
    $payloadOccurrences = @{}
    for ($i = 0; $i -lt $names.Count; $i++) {
        $path = Join-Path $Directory $names[$i]
        $expected = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $names[$i]
        if ($lines[$i] -cne $expected) { throw "Asset checksum mismatch: $($names[$i])" }
        $manifest = Assert-ReleaseZip $path $Version $Commit
        $identity = (@('api', 'web', 'blazor') | ForEach-Object { "$($_):$($manifest.images.$_.tag):$($manifest.images.$_.id)" }) -join '|'
        if ($null -ne $sources -and $sources -cne $identity) { throw 'Source image identities differ across bundles.' }
        $sources = $identity
        foreach ($file in $manifest.payload) {
            if ($file.path -ceq 'README.md') { continue }
            if ($payloadHashes.ContainsKey($file.path) -and $payloadHashes[$file.path] -cne $file.sha256) { throw "Payload differs across bundles: $($file.path)" }
            $payloadHashes[$file.path] = $file.sha256
            if (-not $payloadOccurrences.ContainsKey($file.path)) { $payloadOccurrences[$file.path] = 0 }
            $payloadOccurrences[$file.path]++
        }
    }
    foreach ($path in $payloadOccurrences.Keys) {
        $expectedCount = if ($path -ceq 'compose.release.yml') { 7 } else { 2 }
        if ($payloadOccurrences[$path] -ne $expectedCount) { throw "Combined/individual payload sets differ: $path" }
    }
}