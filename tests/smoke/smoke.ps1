param(
    [ValidateSet('Smoke', 'Browser')]
    [string]$Scenario = 'Smoke',
    [switch]$NoBuild,
    [string]$ApiImage,
    [string]$WebImage,
    [string]$BlazorImage,
    [string]$ResultsDirectory,
    [string]$TestedStackMetadata,
    [ValidatePattern('^172\.(1[6-9]|2[0-9]|3[01])\.\d{1,3}\.0/24$')]
    [string]$Subnet = '172.29.240.0/24'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = 'hn-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $root "TestResults/$project"
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
[IO.Directory]::CreateDirectory($ResultsDirectory) | Out-Null
$savedEnvironment = @{}
foreach ($name in @('HN_SMOKE_PROJECT', 'HN_DOCKER_SUBNET', 'HN_PROXY_IP', 'HN_SMOKE_API_IMAGE', 'HN_SMOKE_WEB_IMAGE', 'HN_SMOKE_BLAZOR_IMAGE', 'HN_SMOKE_SCENARIO')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$resourcesMayExist = $false
$transcribing = $false
$failure = $null
try {
Start-Transcript -Path (Join-Path $ResultsDirectory 'harness.log') | Out-Null
$transcribing = $true
[IO.File]::WriteAllText((Join-Path $ResultsDirectory 'stack.json'), (@{ scenario = $Scenario; project = $project; verified = $false } | ConvertTo-Json))
. (Join-Path $PSScriptRoot 'BrowserGate.ps1')
$images = @{ api = $ApiImage; web = $WebImage; blazor = $BlazorImage }
foreach ($service in @('api', 'web', 'blazor')) {
    if ([string]::IsNullOrWhiteSpace($images[$service])) {
        if ($NoBuild) { throw "-NoBuild requires an explicit image for $service." }
        $images[$service] = "${project}-${service}:smoke"
    }
    if ($NoBuild) {
        & docker image inspect $images[$service] | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Required local $service image '$($images[$service])' is missing; no build or pull is allowed." }
    }
}
$composeArgs = @('--project-name', $project, '--project-directory', $root,
    '-f', (Join-Path $root 'docker-compose.yml'), '-f', (Join-Path $PSScriptRoot 'compose.smoke.yml'))

function Invoke-Compose {
    & docker compose @composeArgs @args
    if ($LASTEXITCODE -ne 0) { throw "docker compose $args failed ($LASTEXITCODE)." }
}

function Get-AddressNumber([string]$Address) {
    $bytes = [Net.IPAddress]::Parse($Address).GetAddressBytes()
    return [long]$bytes[0] * 16777216 + [long]$bytes[1] * 65536 + [long]$bytes[2] * 256 + $bytes[3]
}

function Assert-Smoke([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

# Reject overlaps, including broader existing CIDRs, before creating anything.
$start = Get-AddressNumber ($Subnet.Split('/')[0])
$networkIds = @(& docker network ls -q)
if ($LASTEXITCODE -ne 0) { throw 'Cannot list Docker networks.' }
$networks = @()
if ($networkIds.Count -gt 0) {
    $networks = & docker network inspect @networkIds | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect Docker networks.' }
}
foreach ($network in $networks) {
    foreach ($config in $network.IPAM.Config) {
        if ($config.Subnet -and $config.Subnet -notmatch ':') {
            $parts = $config.Subnet.Split('/')
            $otherStart = Get-AddressNumber $parts[0]
            $otherEnd = $otherStart + [math]::Pow(2, 32 - [int]$parts[1]) - 1
            if ($start -le $otherEnd -and $start + 255 -ge $otherStart) {
                throw "$Subnet overlaps Docker network $($network.Name) ($($config.Subnet)); select another -Subnet."
            }
        }
    }
}

$env:HN_SMOKE_PROJECT = $project
$env:HN_DOCKER_SUBNET = $Subnet
$env:HN_PROXY_IP = $Subnet.Replace('.0/24', '.10')
$env:HN_SMOKE_API_IMAGE = $images.api
$env:HN_SMOKE_WEB_IMAGE = $images.web
$env:HN_SMOKE_BLAZOR_IMAGE = $images.blazor
$env:HN_SMOKE_SCENARIO = $Scenario
    Write-Host "Smoke project: $project; isolated subnet: $Subnet; trusted proxy: $env:HN_PROXY_IP"
    $config = Invoke-Compose config --format json | ConvertFrom-Json
    Assert-Smoke ($config.services.api.environment.HackerNews__BaseUrl -eq 'http://upstream:8080/v0/') 'API must use only the deterministic fixture.'
    Assert-Smoke (-not $config.networks.'hn-backend'.internal) 'Loopback publishing requires a non-internal bridge.'
    Assert-Smoke ($config.networks.'hn-backend'.ipam.config[0].subnet -eq $Subnet) 'Smoke must use the collision-checked subnet.'
    Assert-Smoke ($config.services.api.environment.ReverseProxy__KnownProxies__0 -eq $env:HN_PROXY_IP) 'API must trust the smoke proxy IP.'
    Assert-Smoke ($config.services.api.environment.ReverseProxy__KnownNetworks__0 -eq "$env:HN_PROXY_IP/32") 'Smoke must trust only the proxy, not the whole subnet.'
    Assert-Smoke ($config.services.web.networks.'hn-backend'.ipv4_address -eq $env:HN_PROXY_IP) 'Proxy must have the trusted IP.'
    Assert-Smoke ($config.services.blazor.environment.ApiClient__RequestTimeoutSeconds -eq '130' -and $config.services.blazor.environment.ApiClient__RefreshTimeoutSeconds -eq '120') 'Blazor must use fixed valid smoke timeouts.'
    foreach ($service in @('api', 'web', 'blazor')) {
        $ports = @($config.services.$service.ports)
        Assert-Smoke ($ports.Count -eq 1 -and $ports[0].host_ip -eq '127.0.0.1') "$service must publish only one loopback port."
        Assert-Smoke ($config.services.$service.container_name -eq "$project-$service" -and $config.services.$service.image -eq $images[$service]) "$service must use isolated container names and the selected image."
        Assert-Smoke ($config.services.$service.pull_policy -eq 'never') "$service must not pull an application image."
        Assert-Smoke ($config.services.$service.restart -eq 'no') "$service must not restart."
        foreach ($dependency in $config.services.$service.depends_on.PSObject.Properties.Name) {
            Assert-Smoke ($dependency -in @('upstream', 'api', 'web', 'blazor')) "$service has an unexpected dependency: $dependency."
        }
    }

    Assert-Smoke ($config.services.upstream.environment.HN_SMOKE_SCENARIO -eq $Scenario) 'Fixture scenario must match the requested scenario.'
    if (-not $NoBuild) { Invoke-Compose build --quiet api web blazor }
    $imageIds = @{}
    foreach ($service in @('api', 'web', 'blazor')) {
        $imageIds[$service] = (& docker image inspect --format '{{.Id}}' $images[$service]).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Cannot resolve $service image identity." }
    }
    if ($TestedStackMetadata) {
        $tested = [IO.File]::ReadAllText([IO.Path]::GetFullPath($TestedStackMetadata)) | ConvertFrom-Json
        Assert-Smoke ($tested.verified -eq $true) 'Previous stack validation must have succeeded.'
        foreach ($service in @('api', 'web', 'blazor')) {
            Assert-Smoke ($tested.imageIds.$service -ceq $imageIds[$service]) "$service differs from the previously tested image."
        }
    }
    function Assert-ImageIdentities {
        foreach ($service in @('api', 'web', 'blazor')) {
            $current = & docker image inspect --format '{{.Id}}' $images[$service]
            if ($LASTEXITCODE -ne 0 -or $current -cne $imageIds[$service]) { throw "$service image identity changed." }
            $running = & docker inspect --format '{{.Image}}' "$project-$service"
            if ($LASTEXITCODE -ne 0 -or $running -cne $imageIds[$service]) { throw "$service container uses a different image." }
        }
    }
    $resourcesMayExist = $true
    Invoke-Compose up -d --no-build --wait --wait-timeout 90 upstream api web blazor
    Assert-ImageIdentities
    $apiAddress = (Invoke-Compose port api 8080).Trim()
    $webAddress = (Invoke-Compose port web 80).Trim()
    $blazorAddress = (Invoke-Compose port blazor 8080).Trim()
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'stack.json'), (@{
        scenario = $Scenario; project = $project; apiUrl = "http://$apiAddress"
        webUrl = "http://$webAddress"; blazorUrl = "http://$blazorAddress"; images = $images
        imageIds = $imageIds; verified = $false
    } | ConvertTo-Json))
    Assert-Smoke ($blazorAddress -match '^127\.0\.0\.1:\d+$' -and $blazorAddress -notmatch ':808[012]$') 'Expected nondefault Blazor loopback port.'
    Write-Host "Loopback API: http://$apiAddress ; React/proxy: http://$webAddress ; Blazor: http://$blazorAddress"
    foreach ($address in @($apiAddress, $webAddress)) {
        Assert-Smoke ($address -match '^127\.0\.0\.1:\d+$' -and $address -notmatch ':808[012]$') 'Expected nondefault loopback port.'
        $health = Invoke-WebRequest -UseBasicParsing -Uri "http://$address/health" -TimeoutSec 10
        Assert-Smoke ($health.StatusCode -eq 200 -and $health.Content -eq 'Healthy') "Health failed at $address."
    }
    Write-Host 'PASS test_loopback_health: API and nginx /health both HTTP 200 Healthy'

    $index = Invoke-WebRequest -UseBasicParsing -Uri "http://$webAddress/" -TimeoutSec 10
    $route = Invoke-WebRequest -UseBasicParsing -Uri "http://$webAddress/smoke-route" -TimeoutSec 10
    Assert-Smoke ($index.StatusCode -eq 200 -and $index.Content -match '<div id="root"></div>') 'React index did not load.'
    Assert-Smoke ($route.StatusCode -eq 200 -and $route.Content -eq $index.Content) 'React route must fall back to the SPA index.'
    $assetMatch = [regex]::Match($index.Content, 'src="(/assets/[^" ]+\.js)"')
    Assert-Smoke $assetMatch.Success 'React index must reference its built JavaScript asset.'
    $asset = Invoke-WebRequest -UseBasicParsing -Uri ("http://$webAddress" + $assetMatch.Groups[1].Value) -TimeoutSec 10
    Assert-Smoke ($asset.StatusCode -eq 200 -and $asset.Headers['Content-Type'] -match 'javascript' -and $asset.RawContentLength -gt 0) 'React JavaScript asset did not load.'
    Write-Host 'PASS test_react_route: root and deep route serve identical SPA HTML; referenced JavaScript returns HTTP 200'

    if ($Scenario -eq 'Browser') {
        # Browser traffic belongs to this fresh stack, never verify.py's timed sequence.
        $browserStories = Invoke-WebRequest -UseBasicParsing -Uri "http://$webAddress/api/best-stories?n=500" -TimeoutSec 30
        $browserRows = @($browserStories.Content | ConvertFrom-Json)
        Assert-Smoke ($browserRows.Count -eq 25) 'Browser scenario must contain exactly 25 stories.'
        for ($i = 0; $i -lt 25; $i++) {
            $id = 201 + $i
            Assert-Smoke ($browserRows[$i].title -eq "Browser story $id" -and $browserRows[$i].score -eq (1000 - [math]::Floor($i / 2) * 10)) 'Browser stories must use deterministic score/tie ordering.'
        }
        $browserDirectory = Join-Path $root 'tests/browser'
        $startInfo = [Diagnostics.ProcessStartInfo]::new('node')
        $startInfo.WorkingDirectory = $browserDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.ArgumentList.Add((Join-Path $browserDirectory 'node_modules/@playwright/test/cli.js'))
        $startInfo.ArgumentList.Add('test')
        $startInfo.Environment['HN_WEB_URL'] = "http://$webAddress"
        $startInfo.Environment['HN_BLAZOR_URL'] = "http://$blazorAddress"
        Invoke-BrowserGate -StartInfo $startInfo -ResultsDirectory $ResultsDirectory
        Write-Host 'PASS Chromium: React and Blazor interactive pagination against 25 fixture stories.'
    }
    else {
    $result = Invoke-WebRequest -UseBasicParsing -Uri "http://$webAddress/api/best-stories?n=1" -TimeoutSec 15
    $stories = @($result.Content | ConvertFrom-Json)
    Assert-Smoke ($result.StatusCode -eq 200 -and $stories.Count -eq 1 -and $stories[0].score -eq 900 -and $stories[0].title -eq 'Highest score, last upstream') 'Loopback /api must return the fixture winner.'
    Write-Host 'PASS test_loopback_proxy: /api/best-stories?n=1 returns the fixture winner, score 900'

    # Keep Blazor traffic outside verify.py's timed rate-limit sequence.
    $blazor = $null
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            $blazor = Invoke-WebRequest -UseBasicParsing -Uri "http://$blazorAddress/" -TimeoutSec 5
            break
        }
        catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Seconds 2
        }
    }
    Assert-Smoke ($blazor.StatusCode -eq 200 -and $blazor.Headers['Content-Type'] -match 'text/html') 'Blazor root must return HTML.'
    Assert-Smoke ($blazor.Content -notmatch 'class="error"|No stories to display\.|Loading') 'Blazor must prerender without an error, empty, or loading state.'
    $bodyMatch = [regex]::Match($blazor.Content, '(?s)<tbody>(.*?)</tbody>')
    Assert-Smoke $bodyMatch.Success 'Blazor must render a story table body.'
    $rows = [regex]::Matches($bodyMatch.Groups[1].Value, '(?s)<tr>(.*?)</tr>')
    $expectedRows = @(
        @(104, 'Highest score, last upstream', 'dora', 900, 4),
        @(102, 'Tie, smaller ID', 'bob', 150, 2),
        @(103, 'Tie, larger ID', 'carol', 150, 3),
        @(101, 'First, not best', 'alice', 5, 1)
    )
    Assert-Smoke ($rows.Count -eq $expectedRows.Count) 'Blazor must prerender exactly four fixture rows.'
    for ($i = 0; $i -lt $expectedRows.Count; $i++) {
        $expected = $expectedRows[$i]
        $cells = @([regex]::Matches($rows[$i].Value, '(?s)<td\b[^>]*>(.*?)</td>') | ForEach-Object {
            [Net.WebUtility]::HtmlDecode([regex]::Replace($_.Groups[1].Value, '<[^>]*>', '')).Trim()
        })
        $expectedCells = @([string]($i + 1), $expected[1], $expected[2], '2023-11-14T22:13:20+00:00', [string]$expected[3], [string]$expected[4])
        Assert-Smoke ($cells.Count -eq 6 -and ($cells -join '|') -ceq ($expectedCells -join '|')) "Blazor fixture row $($i + 1) has incorrect values or ordering."
        Assert-Smoke ($rows[$i].Value.Contains('href="https://fixture.invalid/' + $expected[0] + '"')) "Blazor fixture row $($i + 1) has the wrong link."
    }
    Write-Host "PASS test_blazor_prerender: four exact fixture rows in score/tie order, all six columns and links; readiness attempt $attempt/10 (5s requests, 2s retry delay)"

    foreach ($resource in @(@('href="(app\.css)"', 'text/css'), @('src="(_framework/blazor\.web(?:\.[a-zA-Z0-9_-]+)?\.js)"', 'javascript'))) {
        $resourceMatch = [regex]::Match($blazor.Content, $resource[0])
        Assert-Smoke $resourceMatch.Success "Blazor must reference a resource matching $($resource[0])."
        $resourcePath = $resourceMatch.Groups[1].Value
        $response = Invoke-WebRequest -UseBasicParsing -Uri ("http://$blazorAddress/" + $resourcePath) -TimeoutSec 10
        Assert-Smoke ($response.StatusCode -eq 200 -and $response.Headers['Content-Type'] -match $resource[1] -and $response.RawContentLength -gt 0) "Blazor resource $resourcePath did not load."
        Write-Host "PASS test_blazor_resource: $resourcePath HTTP 200, nonempty, expected media type (10s timeout)"
    }
    $negotiation = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "http://$blazorAddress/_blazor/negotiate?negotiateVersion=1" -TimeoutSec 10
    $connection = $negotiation.Content | ConvertFrom-Json
    Assert-Smoke ($negotiation.StatusCode -eq 200 -and $negotiation.Headers['Content-Type'] -match 'application/json') 'Blazor negotiation must return HTTP 200 JSON.'
    Assert-Smoke ($connection.negotiateVersion -eq 1 -and -not [string]::IsNullOrWhiteSpace($connection.connectionId) -and -not [string]::IsNullOrWhiteSpace($connection.connectionToken) -and -not $connection.error) 'Blazor negotiation must return connection identifiers without errors.'
    $webSockets = @($connection.availableTransports | Where-Object { $_.transport -eq 'WebSockets' -and $_.transferFormats -contains 'Binary' })
    Assert-Smoke ($webSockets.Count -eq 1) 'Blazor negotiation must offer binary WebSockets.'
    Write-Host 'PASS test_blazor_negotiate: valid v1 connection identifiers and binary WebSockets transport (10s timeout); interactive browser circuit not tested'

    Invoke-Compose exec -T upstream python -B /smoke/verify.py
    $secondClient = Invoke-WebRequest -UseBasicParsing -Uri "http://$webAddress/api/best-stories?n=1" -TimeoutSec 10
    Assert-Smoke ($secondClient.StatusCode -eq 200 -and $secondClient.Content -eq $result.Content) 'A second real client must remain usable after the fixture client exhausts its limit.'
    Write-Host 'PASS test_distinct_proxy_clients: loopback client still gets identical HTTP 200 after fixture client is rate limited'
    }
    Invoke-Compose ps
    Assert-ImageIdentities
    $metadata = [IO.File]::ReadAllText((Join-Path $ResultsDirectory 'stack.json')) | ConvertFrom-Json
    $metadata.verified = $false
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'stack.json'), ($metadata | ConvertTo-Json))
}
catch {
    $failure = $_
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'failure.txt'), ($_ | Out-String))
    throw
}
finally {
    try {
        if ($resourcesMayExist) {
            try {
                $status = & docker compose @composeArgs ps --all 2>&1
                [IO.File]::WriteAllLines((Join-Path $ResultsDirectory 'services.txt'), [string[]]$status)
                $logs = & docker compose @composeArgs logs --no-color --tail 200 2>&1
                [IO.File]::WriteAllLines((Join-Path $ResultsDirectory 'services.log'), [string[]]$logs)
            }
            catch { Write-Warning "Could not capture diagnostics: $_" }
            Invoke-Compose down --volumes --timeout 10
            $leftContainers = @(& docker ps -aq --filter "label=com.docker.compose.project=$project")
            if ($LASTEXITCODE -ne 0) { throw 'Cannot verify container cleanup.' }
            $leftNetworks = @(& docker network ls -q --filter "label=com.docker.compose.project=$project")
            if ($LASTEXITCODE -ne 0) { throw 'Cannot verify network cleanup.' }
            $leftVolumes = @(& docker volume ls -q --filter "label=com.docker.compose.project=$project")
            if ($LASTEXITCODE -ne 0) { throw 'Cannot verify volume cleanup.' }
            Assert-Smoke ($leftContainers.Count + $leftNetworks.Count + $leftVolumes.Count -eq 0) "Smoke resources remain for $project."
            Write-Host "PASS cleanup: no containers/networks/volumes remain for $project; images retained"
            if (-not $failure) {
                $metadata.verified = $true
                [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'stack.json'), ($metadata | ConvertTo-Json))
            }
        }
    }
    catch {
        [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'cleanup-failure.txt'), ($_ | Out-String))
        if (-not $failure) { throw }
        Write-Warning "Stack cleanup also failed: $_"
    }
    finally {
        foreach ($name in $savedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
        if ($transcribing) { Stop-Transcript | Out-Null }
    }
}