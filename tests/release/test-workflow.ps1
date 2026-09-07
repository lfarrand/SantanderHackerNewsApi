#requires -Version 7.4
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/release/Release.Common.ps1')
$workflow = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../.github/workflows/ci.yml')).Replace("`r`n", "`n")
function Require([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "Workflow contract failed: $Name" }
    Write-Host "PASS $Name"
}
$release = ($workflow -split '(?m)^  release:\n')[1]
Require 'tag-and-branch-triggers' ($workflow.Contains("branches: [main, master]") -and $workflow.Contains("tags: ['v*']") -and $workflow.Contains('  pull_request:') -and -not $workflow.Contains('paths-ignore:'))
Require 'tag-safe-concurrency' ($workflow.Contains("cancel-in-progress: `${{ github.ref_type != 'tag' }}") -and $release.Contains('cancel-in-progress: false'))
Require 'publication-only-permissions' (($workflow -split '  release:')[0] -notmatch 'contents: write|GH_TOKEN:|pull-requests: write' -and $release.Contains('contents: write') -and $release.Contains('persist-credentials: false'))
Require 'artifact-identity' ($workflow.Contains('release-artifact-id: ${{ steps.release-bundles.outputs.artifact-id }}') -and $release.Contains('artifact-ids: ${{ needs.docker.outputs.release-artifact-id }}') -and $workflow.Contains('name: release-${{ github.run_id }}-${{ github.run_attempt }}') -and $workflow.Contains('compression-level: 0'))
Require 'three-explicit-builds' ([regex]::Matches($workflow, 'run: docker build --platform linux/amd64').Count -eq 3)
foreach ($image in @('API_IMAGE', 'WEB_IMAGE', 'BLAZOR_IMAGE')) {
    Require "same-reference-$image" ($workflow.Contains('-t "$' + $image + '"') -and [regex]::Matches($workflow, [regex]::Escape('$env:' + $image)).Count -eq 3)
}
Require 'sequential-evidence' ($workflow.IndexOf('Four-story HTTP smoke') -lt $workflow.IndexOf('Chromium against both UIs') -and $workflow.IndexOf('Chromium against both UIs') -lt $workflow.IndexOf('Package verified') -and $workflow.Contains("-TestedStackMetadata @('TestResults/stack-smoke/stack.json', 'TestResults/stack-browser/stack.json')"))
Require 'native-mtp-and-openapi' ($workflow.Contains('dotnet test --project') -and $workflow.Contains('--locked-mode') -and $workflow.Contains('--coverlet --report-xunit-xml') -and $workflow.Contains("test.get('result') == 'Pass'") -and $workflow.Contains("p.get('name') == os.environ['TEST_ASSEMBLY']") -and [regex]::Matches($workflow, 'uses: oasdiff/oasdiff-action/breaking@v0').Count -eq 2 -and $workflow.Contains("if: steps.baseline.outputs.exists == 'true'"))
Require 'no-pr-only-dependency' ($release.Contains('needs: [test, ui-test, docker]') -and -not $release.Contains('openapi-pr'))
$predicate = [regex]::Match($release, '(?m)^    if: (.+)$').Groups[1].Value
foreach ($event in @('pull_request', 'push')) {
    foreach ($refType in @('branch', 'tag')) {
        foreach ($failedJob in @('', 'test', 'ui-test', 'docker')) {
          foreach ($failureState in @('failure', 'skipped', 'cancelled')) {
            $expression = $predicate.Replace('github.event_name', "'$event'").Replace('github.ref_type', "'$refType'")
            foreach ($job in @('test', 'ui-test', 'docker')) {
                $result = if ($job -eq $failedJob) { $failureState } else { 'success' }
                $expression = $expression.Replace("needs.$job.result", "'$result'")
            }
            $expression = $expression.Replace('&&', '-and').Replace('==', '-eq')
            $actual = & ([scriptblock]::Create($expression))
            Require "event-$event-$refType-$failureState-$failedJob" ($actual -eq ($event -eq 'push' -and $refType -eq 'tag' -and -not $failedJob))
          }
        }
    }
}
foreach ($tag in @('v1.2.3', 'v1.2.3-rc.1')) { Require "valid-tag-$tag" ((Get-ReleaseVersion $tag) -ceq $tag.Substring(1)) }
foreach ($tag in @('v01.2.3', 'v1.2', 'v1.2.3-01', 'v1.2.3+build', "v1.2.3`n")) {
    $caught = $null
    try { Get-ReleaseVersion $tag } catch { $caught = $_ }
    Require 'invalid-tag-blocked-before-build' ($null -ne $caught -and $workflow.IndexOf('Get-ReleaseVersion $env:BUILD_REF_NAME') -lt $workflow.IndexOf('run: docker build'))
}