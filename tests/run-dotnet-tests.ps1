param(
    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 60,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runId = 'xunit-v4-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$failed = $false
Push-Location $root
try {
    foreach ($suite in @('Api', 'Blazor')) {
        $name = "HackerNews.BestStories.$suite.Tests"
        $projectDirectory = Join-Path $PSScriptRoot $name
        $results = Join-Path $projectDirectory "TestResults\$runId"
        [System.IO.Directory]::CreateDirectory($results) | Out-Null
        $arguments = @('test', '--project', (Join-Path $projectDirectory "$name.csproj"),
            '--timeout', "${TimeoutSeconds}s", '--parallel', 'collections', '--max-threads', '4',
            '--report-xunit-xml', '--report-xunit-xml-filename', 'results.xml',
            '--results-directory', $results)
        if ($NoBuild) { $arguments += '--no-build' }
        Write-Host "dotnet $($arguments -join ' ')"
        & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $results 'console.log') | Out-Host
        $exitCode = $LASTEXITCODE
        $report = Join-Path $results 'results.xml'
        $summary = [ordered]@{
            suite = $suite; exitCode = $exitCode; total = 0; passed = 0; failed = 0; skipped = 0
            failures = @(); missingFromBaseline = @(); addedSinceBaseline = @()
        }
        if (Test-Path $report) {
            $xml = [System.Xml.XmlDocument]::new()
            $xml.Load($report)
            $tests = @($xml.SelectNodes('//test'))
            $summary.total = $tests.Count
            $summary.passed = @($tests | Where-Object result -eq 'Pass').Count
            $summary.failed = @($tests | Where-Object result -eq 'Fail').Count
            $summary.skipped = @($tests | Where-Object result -eq 'Skip').Count
            $summary.failures = @($tests | Where-Object result -eq 'Fail' | ForEach-Object {
                [ordered]@{
                    name = $_.name
                    message = $_.SelectSingleNode('failure/message').InnerText
                    stackTrace = $_.SelectSingleNode('failure/stack-trace').InnerText
                }
            })
            $baselinePath = Join-Path $projectDirectory 'TestResults\xunit-v2-baseline\baseline.trx'
            if (Test-Path $baselinePath) {
                $baseline = [System.Xml.XmlDocument]::new()
                $baseline.Load($baselinePath)
                $before = @($baseline.SelectNodes('//*[local-name()="UnitTestResult"]') | ForEach-Object { $_.testName })
                # xUnit XML C-escapes display names (including quotes and backslashes); VSTest TRX did not.
                $after = @($tests | ForEach-Object { [System.Text.RegularExpressions.Regex]::Unescape($_.name) })
                $summary.missingFromBaseline = @($before | Where-Object { $_ -cnotin $after })
                $summary.addedSinceBaseline = @($after | Where-Object { $_ -cnotin $before })
                if ($before.Count -ne $after.Count -or $summary.missingFromBaseline.Count -or $summary.addedSinceBaseline.Count) {
                    $failed = $true
                }
            }
        } else {
            $summary.failures = @([ordered]@{ name = '<test runner>'; message = 'No XML report was produced; inspect console.log.' })
            $failed = $true
        }
        $json = $summary | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText((Join-Path $results 'summary.json'), $json)
        Write-Host $json
        Write-Host "Results: $results"
        if ($exitCode -ne 0 -or $summary.failed -gt 0 -or $summary.skipped -gt 0 -or $summary.total -eq 0) {
            $failed = $true
        }
    }
} finally {
    Pop-Location
}
if ($failed) { exit 1 }
