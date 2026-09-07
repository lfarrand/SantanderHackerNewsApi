#requires -Version 7.4
[CmdletBinding()]
param([string]$Tag, [string]$Commit, [string]$Repository, [string]$BundleDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release/Release.Common.ps1')

function Invoke-ReleaseProcess([string]$FileName, [string[]]$Arguments, [int]$TimeoutSeconds = 120) {
    $info = [Diagnostics.ProcessStartInfo]::new($FileName)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = $false
    $failure = $null
    try {
        if (-not $process.Start()) { throw "Cannot start $FileName." }
        $started = $true
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw "$FileName exceeded ${TimeoutSeconds}s." }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) { throw "$FileName streams did not close." }
        if ($process.ExitCode -ne 0) { throw "$FileName failed ($($process.ExitCode)): $($stderr.Result)" }
        return $stdout.Result.Trim()
    }
    catch { $failure = $_; throw }
    finally {
        try {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        }
        catch { if ($failure) { Write-Warning "Secondary process cleanup failure: $_" } else { throw } }
        finally { $process.Dispose() }
    }
}

# The only GitHub boundary; offline regressions replace this function, never the validators.
function Invoke-ReleaseGh([string[]]$Arguments) { Invoke-ReleaseProcess 'gh' $Arguments 300 }

function Assert-RemoteReleaseTag([string]$Repository, [string]$Tag, [string]$Commit) {
    $reference = Invoke-ReleaseGh @('api', "repos/$Repository/git/ref/tags/$Tag") | ConvertFrom-Json
    $object = $reference.object
    $seen = @{}
    for ($depth = 0; $object.type -ceq 'tag'; $depth++) {
        if ($depth -ge 10 -or $seen.ContainsKey($object.sha)) { throw 'Invalid annotated tag chain.' }
        $seen[$object.sha] = $true
        $object = (Invoke-ReleaseGh @('api', "repos/$Repository/git/tags/$($object.sha)") | ConvertFrom-Json).object
    }
    if ($object.type -cne 'commit' -or $object.sha -cne $Commit) { throw 'Remote tag commit mismatch.' }
}

function Get-RemoteReleaseAssets([string]$Repository, $ReleaseId) {
    $pages = Invoke-ReleaseGh @('api', '--paginate', '--slurp', "repos/$Repository/releases/$ReleaseId/assets?per_page=100") | ConvertFrom-Json
    foreach ($page in $pages) { foreach ($asset in $page) { $asset } }
}

function Assert-RemoteReleaseAsset($Asset, [string]$Directory, [string[]]$Names) {
    if ($Asset.name -cnotin $Names) { throw "Unexpected remote asset: $($Asset.name)" }
    $file = Get-Item -LiteralPath (Join-Path $Directory $Asset.name)
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Asset.state -cne 'uploaded' -or $Asset.size -ne $file.Length -or $Asset.digest -cne $digest) { throw "Remote asset conflict: $($Asset.name)" }
}

function Invoke-ReleasePublish {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Commit,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$BundleDirectory
    )
    $version = Get-ReleaseVersion $Tag
    Assert-ReleaseCommit $Commit
    if ($Repository -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z') { throw 'Invalid repository.' }
    Assert-ReleaseBundleSet $BundleDirectory $version $Commit
    $head = Invoke-ReleaseProcess 'git' @('-C', $PSScriptRoot, 'rev-parse', 'HEAD')
    if ($head -cne $Commit) { throw 'Checkout commit mismatch.' }
    Assert-RemoteReleaseTag $Repository $Tag $Commit
    $identity = "HackerNews release $Tag`nSource commit: $Commit"
    $prerelease = $version.Contains('-')
    $pages = Invoke-ReleaseGh @('api', '--paginate', '--slurp', "repos/$Repository/releases?per_page=100") | ConvertFrom-Json
    $matches = @(foreach ($page in $pages) { foreach ($item in $page) { if ($item.tag_name -ceq $Tag) { $item } } })
    if ($matches.Count -gt 1) { throw 'Ambiguous release identity.' }
    if ($matches.Count -eq 0) {
        $release = Invoke-ReleaseGh @('api', '--method', 'POST', "repos/$Repository/releases", '-f', "tag_name=$Tag", '-f', "target_commitish=$Commit", '-f', "name=$Tag", '-f', "body=$identity", '-F', 'draft=true', '-F', "prerelease=$($prerelease.ToString().ToLowerInvariant())", '-f', 'make_latest=false') | ConvertFrom-Json
    }
    else { $release = $matches[0] }
    if (-not $release.draft) { throw 'Refusing already-published release.' }
    if ($release.tag_name -cne $Tag -or $release.body -cne $identity -or $release.target_commitish -cne $Commit -or $release.prerelease -ne $prerelease) { throw 'Conflicting draft identity.' }
    $names = @((Get-ReleaseAssetNames $version)) + @('SHA256SUMS')
    $assets = @(Get-RemoteReleaseAssets $Repository $release.id)
    if (@($assets | ForEach-Object { $_.name } | Select-Object -Unique).Count -ne $assets.Count) { throw 'Duplicate remote assets.' }
    foreach ($asset in $assets) { Assert-RemoteReleaseAsset $asset $BundleDirectory $names }
    foreach ($name in $names) {
        if ($name -cnotin @($assets | ForEach-Object { $_.name })) {
            $null = Invoke-ReleaseGh @('release', 'upload', $Tag, (Join-Path ([IO.Path]::GetFullPath($BundleDirectory)) $name), '--repo', $Repository)
        }
    }
    $assets = @(Get-RemoteReleaseAssets $Repository $release.id)
    if ($assets.Count -ne $names.Count -or @(Compare-Object -CaseSensitive $names @($assets.name)).Count) { throw 'Incomplete remote asset set.' }
    foreach ($asset in $assets) { Assert-RemoteReleaseAsset $asset $BundleDirectory $names }
    Assert-RemoteReleaseTag $Repository $Tag $Commit
    $current = Invoke-ReleaseGh @('api', "repos/$Repository/releases/$($release.id)") | ConvertFrom-Json
    if (-not $current.draft -or $current.body -cne $identity -or $current.tag_name -cne $Tag -or $current.target_commitish -cne $Commit -or $current.prerelease -ne $prerelease) { throw 'Release identity changed before publication.' }
    $null = Invoke-ReleaseGh @('api', '--method', 'PATCH', "repos/$Repository/releases/$($release.id)", '-F', 'draft=false', '-F', "prerelease=$($prerelease.ToString().ToLowerInvariant())", '-f', 'make_latest=false')
    Write-Host "Published verified $Tag ($Commit)."
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-ReleasePublish @PSBoundParameters }