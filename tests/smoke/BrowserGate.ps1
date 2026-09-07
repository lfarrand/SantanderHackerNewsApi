function Invoke-BrowserGate {
    param(
        [Diagnostics.ProcessStartInfo]$StartInfo,
        [string]$ResultsDirectory,
        [ValidateRange(1, 210)][int]$TimeoutSeconds = 210
    )

    $browserResults = Join-Path $ResultsDirectory ('browser-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($browserResults) | Out-Null
    $StartInfo.Environment['HN_BROWSER_RESULTS'] = $browserResults
    $StartInfo.UseShellExecute = $false
    $StartInfo.RedirectStandardOutput = $true
    $StartInfo.RedirectStandardError = $true
    $stdout = [IO.File]::Create((Join-Path $browserResults 'stdout.log'))
    $stderr = [IO.File]::Create((Join-Path $browserResults 'stderr.log'))
    $process = $null
    $failure = $null
    try {
        $process = [Diagnostics.Process]::Start($StartInfo)
        $outTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errTask = $process.StandardError.BaseStream.CopyToAsync($stderr)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Chromium tests exceeded the $TimeoutSeconds-second harness deadline."
        }
        if ($process.ExitCode -ne 0) { throw "Chromium tests failed ($($process.ExitCode))." }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask, $errTask), 10000)) {
            throw 'Browser output streams did not close within 10 seconds.'
        }
        if (-not [IO.File]::Exists((Join-Path $browserResults 'junit.xml'))) { throw 'Missing browser JUnit report.' }
        [xml]$report = [IO.File]::ReadAllText((Join-Path $browserResults 'junit.xml'))
        $cases = @($report.SelectNodes('//testcase'))
        $expected = @('React: real fixture stories and interactive pagination', 'Blazor: real fixture stories and interactive pagination')
        if ($cases.Count -ne 2 -or $report.SelectNodes('//failure|//error|//skipped').Count -ne 0) {
            throw 'Both browser tests must run and pass without skips.'
        }
        foreach ($name in $expected) {
            if (@($cases | Where-Object { $_.name -ceq $name }).Count -ne 1) { throw "Missing browser case: $name" }
        }
        if (-not [IO.File]::Exists((Join-Path $browserResults 'html/index.html'))) { throw 'Missing browser HTML report.' }
    }
    catch { $failure = $_; throw }
    finally {
        try {
            if ($process -and -not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(10000)) { throw 'Owned browser process did not terminate.' }
            }
            if ($process -and $outTask -and $errTask) {
                if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask, $errTask), 10000)) {
                    throw 'Owned browser output did not drain.'
                }
            }
        }
        catch {
            [IO.File]::WriteAllText((Join-Path $browserResults 'cleanup-failure.txt'), ($_ | Out-String))
            if (-not $failure) { throw }
            Write-Warning "Browser cleanup also failed: $_"
        }
        finally {
            $stdout.Dispose(); $stderr.Dispose()
            if ($process) { $process.Dispose() }
        }
    }
}