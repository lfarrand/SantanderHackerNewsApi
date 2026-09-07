param(
    [string]$ApiImage,
    [string]$WebImage,
    [string]$BlazorImage,
    $Case = @('missing-reference', 'missing-image', 'browser-failure', 'browser-timeout', 'missing-report', 'stale-report', 'missing-html'),
    [string]$ResultsDirectory = 'TestResults/ci-step2'
)

$ErrorActionPreference = 'Stop'
$saved = @{}
$names = @('HN_SMOKE_PROJECT', 'HN_DOCKER_SUBNET', 'HN_PROXY_IP', 'HN_SMOKE_API_IMAGE', 'HN_SMOKE_WEB_IMAGE', 'HN_SMOKE_BLAZOR_IMAGE', 'HN_SMOKE_SCENARIO')
foreach ($name in $names) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, "sentinel-$name", 'Process')
}
try {
    . (Join-Path $PSScriptRoot 'BrowserGate.ps1')
    $cases = @(
        @{ name = 'missing-reference'; images = @{}; failure = 'requires an explicit image' },
        @{ name = 'missing-image'; images = @{ ApiImage = 'hn-smoke-missing-' + [guid]::NewGuid().ToString('N') }; failure = 'Required local api image' },
        @{ name = 'startup-failure'; images = @{ ApiImage = 'python:3.12.13-bookworm'; WebImage = $WebImage; BlazorImage = $BlazorImage }; failure = 'docker compose up' },
        @{ name = 'cleanup-failure'; images = @{ ApiImage = 'python:3.12.13-bookworm'; WebImage = $WebImage; BlazorImage = $BlazorImage }; failure = 'docker compose up' },
        @{ name = 'browser'; images = @{ ApiImage = $ApiImage; WebImage = $WebImage; BlazorImage = $BlazorImage }; failure = $null },
        @{ name = 'browser-failure'; failure = 'Chromium tests failed (17)' },
        @{ name = 'browser-timeout'; failure = '2-second harness deadline' },
        @{ name = 'missing-report'; failure = 'Missing browser JUnit report' },
        @{ name = 'stale-report'; failure = 'Missing browser JUnit report' },
        @{ name = 'missing-html'; failure = 'Missing browser HTML report' }
    )
    foreach ($selection in $Case) {
        if ($selection -notin $cases.name) { throw "Unknown harness case: $selection" }
    }
    foreach ($case in ($cases | Where-Object { $_.name -in $Case })) {
        $directory = [IO.Path]::GetFullPath((Join-Path $ResultsDirectory ($case.name + '-' + [guid]::NewGuid().ToString('N'))))
        $parameters = $case.images
        $caught = $null
        $substitute = $case.name -in @('browser-failure', 'browser-timeout', 'missing-report', 'stale-report', 'missing-html')
        try {
            if ($substitute) {
                if ($case.name -eq 'stale-report') {
                    $stale = Join-Path $directory 'browser'
                    [IO.Directory]::CreateDirectory((Join-Path $stale 'html')) | Out-Null
                    [IO.File]::WriteAllText((Join-Path $stale 'junit.xml'), '<testsuites><testsuite><testcase name="React: real fixture stories and interactive pagination"/><testcase name="Blazor: real fixture stories and interactive pagination"/></testsuite></testsuites>')
                    [IO.File]::WriteAllText((Join-Path $stale 'html/index.html'), 'stale report')
                }
                $startInfo = [Diagnostics.ProcessStartInfo]::new('node')
                $startInfo.ArgumentList.Add((Join-Path $PSScriptRoot 'browser-substitute.cjs'))
                $startInfo.ArgumentList.Add($case.name)
                Invoke-BrowserGate -StartInfo $startInfo -ResultsDirectory $directory -TimeoutSeconds 2
            }
            else {
                try {
                    if ($case.name -eq 'cleanup-failure') {
                        $dockerCommand = (Get-Command docker -CommandType Application | Select-Object -First 1).Source
                        function docker {
                            & $dockerCommand @args
                            if ($args -contains 'down' -and $LASTEXITCODE -eq 0) { $global:LASTEXITCODE = 17 }
                        }
                    }
                    & (Join-Path $PSScriptRoot 'smoke.ps1') -NoBuild -Scenario Browser -ResultsDirectory $directory @parameters
                }
                finally {
                    if ($case.name -eq 'cleanup-failure') { Remove-Item Function:docker }
                }
            }
        }
        catch { $caught = $_ }
        if ($case.failure) {
            if (-not $caught -or $caught.ToString() -notlike "*$($case.failure)*") { throw "Expected $($case.failure), got $caught" }
        }
        elseif ($caught) { throw $caught }
        if ($substitute) {
            $fresh = $startInfo.Environment['HN_BROWSER_RESULTS']
            foreach ($stream in @('stdout', 'stderr')) {
                if ([IO.File]::ReadAllText((Join-Path $fresh "$stream.log")) -notmatch "controlled browser $stream") { throw "Missing captured $stream" }
            }
            if ($case.name -eq 'browser-timeout') {
                foreach ($ownedId in ([IO.File]::ReadAllText((Join-Path $fresh 'owned-pids.json')) | ConvertFrom-Json)) {
                    $alive = $null
                    try { $alive = [Diagnostics.Process]::GetProcessById($ownedId) } catch [ArgumentException] { }
                    if ($alive) { try { if (-not $alive.HasExited) { throw "Owned process remains: $ownedId" } } finally { $alive.Dispose() } }
                }
            }
        }
        elseif ($case.failure) {
            foreach ($file in @('harness.log', 'failure.txt')) {
                if (-not [IO.File]::Exists((Join-Path $directory $file))) { throw "Missing diagnostic: $file" }
            }
        }
        if ($case.name -eq 'cleanup-failure') {
            if ([IO.File]::ReadAllText((Join-Path $directory 'cleanup-failure.txt')) -notmatch 'docker compose down') { throw 'Missing secondary cleanup failure.' }
            $stack = [IO.File]::ReadAllText((Join-Path $directory 'stack.json')) | ConvertFrom-Json
            foreach ($command in @(@('ps', '-aq'), @('network', 'ls', '-q'), @('volume', 'ls', '-q'))) {
                $remaining = @(& docker @command --filter "label=com.docker.compose.project=$($stack.project)")
                if ($LASTEXITCODE -ne 0 -or $remaining.Count -ne 0) { throw 'Cleanup regression left owned resources.' }
            }
        }
        foreach ($name in $names) {
            if ([Environment]::GetEnvironmentVariable($name, 'Process') -cne "sentinel-$name") { throw "Environment not restored: $name" }
        }
        if ($case.name -in @('startup-failure', 'browser')) {
            foreach ($file in @('harness.log', 'services.txt', 'services.log')) {
                if (-not (Test-Path (Join-Path $directory $file))) { throw "Missing diagnostic: $file" }
            }
            $transcript = [IO.File]::ReadAllText([IO.Path]::GetFullPath((Join-Path $directory 'harness.log')))
            if ($transcript -notmatch 'PASS cleanup: no containers/networks/volumes remain') { throw 'Cleanup was not verified.' }
            $status = [IO.File]::ReadAllText([IO.Path]::GetFullPath((Join-Path $directory 'services.txt')))
            if ($status -match 'portainer') { throw 'Portainer must never start.' }
        }
        Write-Host "PASS $($case.name): expected outcome and environment restoration"
    }
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}