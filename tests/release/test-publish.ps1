#requires -Version 7.4
param([switch]$Stable)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/publish-release.ps1')
$shell = (Get-Command pwsh).Source
$output = Invoke-ReleaseProcess $shell @('-NoProfile', '-Command', '[Console]::Out.Write("o" * 1048576); [Console]::Error.Write("e" * 1048576)') 10
if ($output.Length -ne 1048576 -or $output -cne ('o' * 1048576)) { throw 'Native output capture failed.' }
Write-Host 'PASS concurrent-streams'
foreach ($probe in @(@('native-nonzero', 'exit 9', 'failed (9)'), @('native-timeout', 'Start-Sleep -Seconds 30', 'exceeded 1s'))) {
    $caught = $null
    try { Invoke-ReleaseProcess $shell @('-NoProfile', '-Command', $probe[1]) 1 } catch { $caught = $_ }
    if (-not $caught -or -not $caught.ToString().Contains($probe[2])) { throw "$($probe[0]): $caught" }
    Write-Host "PASS $($probe[0])"
}
$fixtureVersion = if ($Stable) { '1.2.3' } else { '1.2.3-rc.1' }
& (Join-Path $PSScriptRoot 'test-package.ps1') -FixtureVersion $fixtureVersion -BundleAction {
    param($Directory, $Version, $Commit)
    . (Join-Path $PSScriptRoot '../../build/publish-release.ps1') -Commit $Commit
    $tag = "v$Version"
    $isPrerelease = $Version.Contains('-')
    $parameters = @{ Tag = $tag; Commit = $Commit; Repository = 'owner/repository'; BundleDirectory = $Directory }
    function New-Asset($Name) {
        $file = Get-Item -LiteralPath (Join-Path $Directory $Name)
        return @{ name = $Name; size = $file.Length; state = 'uploaded'; digest = 'sha256:' + (Get-FileHash $file.FullName).Hash.ToLowerInvariant() }
    }
    function Invoke-ReleaseProcess([string]$FileName, [string[]]$Arguments, [int]$TimeoutSeconds = 120) {
        if ($FileName -ne 'git' -or $Arguments[-1] -ne 'HEAD') { throw 'Unexpected native call.' }
        if ($script:case -eq 'wrong-checkout') { return 'b' * 40 }
        return $Commit
    }
    function Invoke-ReleaseGh([string[]]$Arguments) {
        $script:calls.Add($Arguments -join ' ')
        $endpoint = @($Arguments | Where-Object { $_ -like 'repos/*' }) | Select-Object -First 1
        if ($endpoint -like '*/git/ref/tags/*') {
            $script:tagReads++
            if ($script:case -eq 'missing-tag') { throw 'controlled missing tag' }
            $sha = if ($script:case -eq 'wrong-tag' -or ($script:case -eq 'moved-tag' -and $script:tagReads -gt 1)) { 'b' * 40 } else { $Commit }
            $type = if ($script:case -eq 'annotated-tag') { 'tag' } else { 'commit' }
            return @{ object = @{ type = $type; sha = $sha } } | ConvertTo-Json
        }
        if ($endpoint -like '*/git/tags/*') { return @{ object = @{ type = 'commit'; sha = $Commit } } | ConvertTo-Json }
        if ($endpoint -match '/releases\?') { return ConvertTo-Json -Depth 10 -InputObject @(@($script:release | Where-Object { $null -ne $_ })) }
        if ('POST' -in $Arguments) {
            if ('draft=true' -notin $Arguments -or 'make_latest=false' -notin $Arguments) { throw 'Unsafe draft creation.' }
            if ("prerelease=$($isPrerelease.ToString().ToLowerInvariant())" -notin $Arguments) { throw 'Wrong draft prerelease flag.' }
            $script:release = @{ id = 7; tag_name = $tag; target_commitish = $Commit; body = "HackerNews release $tag`nSource commit: $Commit"; draft = $true; prerelease = $isPrerelease }
            return $script:release | ConvertTo-Json
        }
        if ($endpoint -like '*/assets?*') { return ConvertTo-Json -Depth 10 -InputObject @(@($script:assets.ToArray())) }
        if ($Arguments[0] -eq 'release') {
            if ('--clobber' -in $Arguments) { throw 'Overwriting assets is forbidden.' }
            if ($script:case -eq 'failed-upload' -and $script:uploads.Count -eq 2) { throw 'controlled failed upload' }
            $name = [IO.Path]::GetFileName($Arguments[3])
            $script:uploads.Add($name)
            if ($script:case -ne 'incomplete-remote') {
                $asset = New-Asset $name
                if ($script:case -eq 'corrupt-upload') { $asset.digest = 'sha256:' + ('0' * 64) }
                $script:assets.Add($asset)
            }
            return ''
        }
        if ('PATCH' -in $Arguments) {
            if ("prerelease=$($isPrerelease.ToString().ToLowerInvariant())" -notin $Arguments -or 'make_latest=false' -notin $Arguments -or 'draft=false' -notin $Arguments) { throw 'Unsafe publication flags.' }
            $script:published = $true
            $script:release.draft = $false
            return '{}'
        }
        if ($endpoint -eq 'repos/owner/repository/releases/7') { return $script:release | ConvertTo-Json }
        throw "Unexpected gh call: $($Arguments -join ' ')"
    }
    $cases = @{
        'lightweight-tag' = ''; 'annotated-tag' = ''; 'partial-draft' = ''; 'complete-draft' = ''
        'missing-tag' = 'controlled missing tag'; 'wrong-tag' = 'Remote tag commit mismatch'; 'moved-tag' = 'Remote tag commit mismatch'
        'wrong-checkout' = 'Checkout commit mismatch'; 'failed-upload' = 'controlled failed upload'
        'conflicting-draft' = 'Conflicting draft identity'; 'published-release' = 'already-published'
        'conflicting-asset' = 'Remote asset conflict'; 'incomplete-remote' = 'Incomplete remote asset set'; 'corrupt-upload' = 'Remote asset conflict'
        'incomplete-local' = 'exactly seven'
        'wrong-manifest-commit' = 'Manifest identity mismatch'
    }
    foreach ($caseName in $cases.Keys) {
        $script:case = $caseName
        $script:release = $null
        $script:assets = [Collections.Generic.List[object]]::new()
        $script:uploads = [Collections.Generic.List[string]]::new()
        $script:calls = [Collections.Generic.List[string]]::new()
        $script:tagReads = 0
        $script:published = $false
        $names = @(Get-ReleaseAssetNames $Version) + @('SHA256SUMS')
        if ($caseName -in @('partial-draft', 'complete-draft', 'conflicting-draft', 'published-release', 'conflicting-asset')) {
            $script:release = @{ id = 7; tag_name = $tag; target_commitish = $Commit; body = "HackerNews release $tag`nSource commit: $Commit"; draft = $true; prerelease = $isPrerelease }
            $script:assets.Add((New-Asset $names[0]))
            if ($caseName -eq 'complete-draft') { foreach ($name in $names[1..7]) { $script:assets.Add((New-Asset $name)) } }
            if ($caseName -eq 'conflicting-draft') { $script:release.body = 'unrelated draft' }
            if ($caseName -eq 'published-release') { $script:release.draft = $false }
            if ($caseName -eq 'conflicting-asset') { $script:assets[0].digest = 'sha256:' + ('0' * 64) }
        }
        $local = $parameters.Clone()
        if ($caseName -eq 'incomplete-local') { $local.BundleDirectory = Split-Path $Directory }
        if ($caseName -eq 'wrong-manifest-commit') { $local.Commit = 'b' * 40 }
        $caught = $null
        try { Invoke-ReleasePublish @local } catch { $caught = $_ }
        $expected = $cases[$caseName]
        if ($expected) {
            if (-not $caught -or $caught.ToString() -notlike "*$expected*" -or $script:published) { throw "$caseName expected '$expected' and no publication, got '$caught'" }
            if ($script:release -and $caseName -ne 'published-release' -and -not $script:release.draft) { throw 'Failure did not leave draft unpublished.' }
            if ($caseName -eq 'failed-upload' -and ($script:assets.Count -ne 2 -or $script:uploads.Count -ne 2)) { throw 'Failed upload lost the resumable partial draft.' }
            if ($caseName -in @('missing-tag', 'wrong-tag', 'wrong-checkout', 'incomplete-local', 'conflicting-draft', 'published-release', 'conflicting-asset') -and $script:uploads.Count) { throw 'Early guard uploaded assets.' }
        }
        else {
            if ($caught -or -not $script:published -or $script:assets.Count -ne 8 -or $script:tagReads -ne 2) { throw "$caseName failed: $caught" }
            $count = if ($caseName -eq 'partial-draft') { 7 } elseif ($caseName -eq 'complete-draft') { 0 } else { 8 }
            if ($script:uploads.Count -ne $count -or ($count -eq 7 -and $names[0] -in $script:uploads)) { throw 'Did not skip only identical existing assets.' }
            if ($caseName -eq 'annotated-tag' -and @($script:calls | Where-Object { $_ -like '*/git/tags/*' }).Count -ne 2) { throw 'Annotated tags were not peeled twice.' }
        }
        Write-Host "PASS $caseName"
    }
}