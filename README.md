# HackerNewsApi

ASP.NET Core 10 API that returns the best *n* Hacker News stories by score, plus React and Blazor UIs.

## Projects

| Name | Path |
|---|---|
| Solution | `HackerNewsApi.slnx` |
| API | `src/HackerNews.BestStories.Api` |
| Blazor UI | `src/HackerNews.BestStories.Blazor` |
| API Tests | `tests/HackerNews.BestStories.Api.Tests` |
| Blazor Tests | `tests/HackerNews.BestStories.Blazor.Tests` |
| React UI | `src/HackerNews.BestStories.React` |

HTTP contract: `GET /api/best-stories?n={n}`.

The `n` parameter specifies how many stories to return. It is required and must be an integer from **1 to 500** (or the configured `HackerNews:MaxStories`). Missing, malformed, or out-of-range `n` returns `400 Bad Request`. When fewer usable stories exist, the response contains `min(n, usableCount)` stories; it never fabricates entries.

JSON fields: `title`, `uri`, `postedBy`, `time` (`2019-10-12T13:43:01+00:00`), `score`, `commentCount`.

## Local run

Prerequisites: .NET 10 SDK (`global.json` permits newer 10.0 feature bands), Node.js 22.12+ (CI uses Node 22), and npm. Run each application in a separate terminal, starting from the repository root.

PowerShell:

```powershell
dotnet run --project src\HackerNews.BestStories.Api --launch-profile http
# API: http://localhost:5182/api/best-stories?n=20
# Scalar: http://localhost:5182/swagger
# OpenAPI: http://localhost:5182/openapi/v1.json

# In a second terminal:
npm.cmd --prefix src\HackerNews.BestStories.React ci
npm.cmd --prefix src\HackerNews.BestStories.React run dev
# React: http://localhost:5173 (Vite proxies /api to 5182)

# In a third terminal:
dotnet run --project src\HackerNews.BestStories.Blazor --launch-profile http
# Blazor: http://localhost:5288 (server calls http://localhost:5182)
```

Bash (also use separate terminals for the three applications):

```bash
dotnet run --project src/HackerNews.BestStories.Api --launch-profile http
npm --prefix src/HackerNews.BestStories.React ci
npm --prefix src/HackerNews.BestStories.React run dev
dotnet run --project src/HackerNews.BestStories.Blazor --launch-profile http
```

Both UIs request `n=500` on initialization and paginate the loaded results without per-page API requests. Default page size is **20**. Blazor prerendering and interactive initialization can each fetch the ranking; this is not a guarantee of exactly one request per browser visit. If you lower the API's `MaxStories`, adjust the UIs' fixed request count as well.

### Supplying `n` in Scalar

In Scalar, enable the `n` query-parameter row and enter the count. Confirm the actual browser request URL contains `?n=200`; a visible placeholder or example alone does not prove the value was sent. The live `/openapi/v1.json` documents `n` as required, with configured bounds and example `1`, not a server-side default.

Try these direct requests:

- Local instance: http://localhost:5182/api/best-stories?n=200
- Docker Compose: http://localhost:8080/api/best-stories?n=200

The old `Invalid n` range message also represented missing input. Missing `n` now explicitly identifies the required query parameter and provides a request example; out-of-range integers still report the configured range. A received `n=200` is valid when `HackerNews:MaxStories` is `500`. If an actual request containing `?n=200` still returns that validation error with a maximum of `500`, compare the running instance with the inspected source rather than changing the cap speculatively.

The UIs allow up to four total attempts (three retries) for `502`/`503`/`504`/`429`, with backoff of 250ms, 500ms, and 1s. `400` is not retried. These quick retries do not bypass the API's failure cooldown; during a longer outage, retry after that cooldown expires.

API validation (`400`), rate-limit (`429`), and handled error (`500`/`502`/`504`) responses use `application/problem+json`, matching OpenAPI. Malformed integer query values also return a `400` problem in both Development and Production. Successful story arrays remain `application/json`.

Blazor Server needs WebSockets (SignalR). Reverse proxies must allow the `/_blazor` upgrade.

Set `ApiBaseUrl` if the API is not on http://localhost:5182.

Blazor's `ApiClient:RequestTimeoutSeconds` defaults to **130 seconds**, allowing the API's default 120-second refresh to complete or return a timeout response. `ApiClient:RefreshTimeoutSeconds` declares that API deadline (default 120); keep it aligned with the API's `HackerNews:RefreshTimeoutSeconds`. Startup rejects a nonpositive refresh budget, a request timeout at or below it, or a value beyond `HttpClient`'s supported range. In Compose, `HN_REFRESH_TIMEOUT_SECONDS` supplies both refresh settings; set `HN_BLAZOR_TIMEOUT_SECONDS` above it (allow headroom for transit). Outside Compose use `ApiClient__RequestTimeoutSeconds` and `ApiClient__RefreshTimeoutSeconds`. This is a per-attempt timeout; caller cancellation still stops the wait, and timeout cancellation itself is not retried.

### Tests and build

PowerShell, from the repository root:

```powershell
dotnet test --project tests\HackerNews.BestStories.Api.Tests\HackerNews.BestStories.Api.Tests.csproj
dotnet test --project tests\HackerNews.BestStories.Blazor.Tests\HackerNews.BestStories.Blazor.Tests.csproj
dotnet build src\HackerNews.BestStories.Api\HackerNews.BestStories.Api.csproj -c Release
dotnet build src\HackerNews.BestStories.Blazor\HackerNews.BestStories.Blazor.csproj -c Release
npm.cmd --prefix src\HackerNews.BestStories.React ci
npm.cmd --prefix src\HackerNews.BestStories.React test
npm.cmd --prefix src\HackerNews.BestStories.React run build
```

The executable .NET test projects use **xUnit 4.0.0** (package `xunit.v3`) with the native **Microsoft.Testing.Platform (MTP)** runner selected by `global.json`, and **coverlet.MTP 10.0.1** for coverage. Each project's `testconfig.json` is copied alongside the test executable (`CopyToOutputDirectory="PreserveNewest"`) and supplies the Cobertura format and coverage filters automatically: the API suite includes only the `HackerNews.BestStories.Api` application assembly, and the Blazor suite includes only `HackerNews.BestStories.Blazor`. Both exclude test assemblies; Blazor also excludes `**/Components/Layout/MainLayout.razor`.

To generate Cobertura coverage, run each project separately and use its own results directory:

```powershell
dotnet test --project tests\HackerNews.BestStories.Api.Tests\HackerNews.BestStories.Api.Tests.csproj --coverlet --results-directory tests\HackerNews.BestStories.Api.Tests\TestResults\coverage
dotnet test --project tests\HackerNews.BestStories.Blazor.Tests\HackerNews.BestStories.Blazor.Tests.csproj --coverlet --results-directory tests\HackerNews.BestStories.Blazor.Tests\TestResults\coverage
```

Each coverage command writes a timestamped `coverage.cobertura.*.xml` report
under its project's `TestResults\coverage` directory.

For Bash, use `/` in project and results-directory paths and `npm` instead of `npm.cmd` in the commands above; run them from the repository root as well.

Optional PowerShell helper for both .NET suites (from the repository root):

```powershell
.\tests\run-dotnet-tests.ps1
# Reuse existing default-configuration (Debug) builds with a longer per-suite timeout:
.\tests\run-dotnet-tests.ps1 -TimeoutSeconds 120 -NoBuild
```

[`tests/run-dotnet-tests.ps1`](tests/run-dotnet-tests.ps1) defaults to a 60-second timeout per suite and builds unless `-NoBuild` is supplied. It captures `console.log`, requests `results.xml`, and writes `summary.json` beneath each suite's `tests/<project>/TestResults/xunit-v4-<UTC timestamp>/` directory. If the runner cannot produce XML, inspect `console.log` and the failure recorded in `summary.json`. Discovery comparison runs only when that suite's local `TestResults/xunit-v2-baseline/baseline.trx` exists; a fresh checkout is not guaranteed to have baseline artifacts. This helper does not enable coverage; use the separate coverage commands above.

Prior migration validation reported **API 142/142** and **Blazor 40/40** passing, preserved discovery, successful locked restores, and correctly scoped coverage. These are historical results, not fresh executions for this README review.

Tests cover raw JSON, ranking, shared cache fills, cancellation, failure cooldown, proxy trust, and UI links. See the [API tests](tests/HackerNews.BestStories.Api.Tests), [Blazor tests](tests/HackerNews.BestStories.Blazor.Tests), and [test helper](tests/run-dotnet-tests.ps1) for test sources and local result capture.

Controlled Docker checks (PowerShell 7.4+, a running Linux-container Docker daemon and Compose 2.24.4+):

```powershell
pwsh -NoProfile -File .\tests\smoke\smoke.ps1 -ApiImage hackernews-beststories-api:local-gate -WebImage hackernews-beststories-web:local-gate -BlazorImage hackernews-beststories-blazor:local-gate -ResultsDirectory TestResults/stack-smoke
if ($LASTEXITCODE -ne 0) { throw 'HTTP smoke failed' }
# Install pinned browser tooling once, then test the SAME images in a fresh stack:
Push-Location tests/browser
npm ci
if ($LASTEXITCODE -ne 0) { throw 'Browser npm ci failed' }
npx playwright install chromium
if ($LASTEXITCODE -ne 0) { throw 'Chromium installation failed' }
Pop-Location
pwsh -NoProfile -File .\tests\smoke\smoke.ps1 -Scenario Browser -NoBuild -ApiImage hackernews-beststories-api:local-gate -WebImage hackernews-beststories-web:local-gate -BlazorImage hackernews-beststories-blazor:local-gate -ResultsDirectory TestResults/stack-browser -TestedStackMetadata TestResults/stack-smoke/stack.json
if ($LASTEXITCODE -ne 0) { throw 'Chromium gate failed' }
```

This builds and starts all three application images against a local upstream fixture, with isolated names, a collision-checked subnet, dynamic loopback ports, and fixed test timeouts. It verifies API/nginx contract, proxy trust, rate limits and upstream request counts; React HTML, deep-route fallback and referenced JavaScript; and Blazor's exact four prerendered fixture rows, stylesheet, framework script and SignalR negotiation. Blazor checks run outside the existing timed rate-limit sequence. Startup is bounded to 90 seconds; Blazor readiness allows 10 attempts (5-second requests, 2-second retry delays), with 10-second asset/negotiation requests.

Unlike default Compose, `tests/smoke/compose.smoke.yml` pins nginx to `HN_PROXY_IP` and sets API trust to that address in `ReverseProxy__KnownProxies__0` plus its `/32` in `ReverseProxy__KnownNetworks__0`. The harness explicitly starts `upstream api web blazor`, not Portainer. Its single-proxy assertions do **not** prove equivalent isolation for the default subnet-trusting stack.

The separate Chromium scenario uses 25 fixture stories and requires both React and Blazor interaction cases: 20 initial rows, six columns, score/tie ordering, links, navigation boundaries, page sizes 10/20/50/100, continuous ranks and partial final pages. It exercises Blazor circuit updates and React deep routes/local pagination without additional story fetches. Browser traffic stays outside the original four-story rate-limit sequence.

Use fresh results directories for each invocation; browser reports use invocation-owned locations so stale output cannot satisfy a new run. Diagnostics include transcript, service status/logs, resolved image IDs in `stack.json`, browser stdout/stderr, HTML/JUnit reports and failure attachments. Preflight/startup/browser/report/cleanup failures block the gate. The harness removes only its own containers/network/volume in `finally`, restores environment values and verifies cleanup; built images remain. It makes no Hacker News requests, though package/image downloads still require network access. The fixture bridge is not an outbound firewall. HTTP smoke alone does **not** prove browser interaction; only a passing separate Chromium report does.

## Docker (three application images, four default services)

| File | Image | Host port |
|---|---|---|
| `Dockerfile.api` | API | 8080 |
| `Dockerfile.react` | React (nginx, `/api` proxied to API) | 8081 |
| `Dockerfile.blazor` | Blazor Server (`ApiBaseUrl=http://api:8080/`) | 8082 |

Default `docker compose up --build` also starts the prebuilt `portainer/portainer-ce:lts` image as service/container `portainer`, publishing **9000/9443**. It mounts `/var/run/docker.sock` and persists `/data` in the named volume `portainer_data`. Docker-socket access grants privileged host-management capabilities; restrict access to trusted administrators and do **not** expose Portainer publicly. It is not part of the application request path and uses Compose's implicit default network, not `hn-backend`.

### Start the default stack

Prerequisites: Docker with a running **Linux-container** engine, Docker Compose v2 (2.24.4+ for the smoke override), and registry/package access for builds. Host .NET/Node installations are not required for Docker builds. Live stories additionally require outbound HTTPS/DNS access to `hacker-news.firebaseio.com`.

Before starting, ensure host ports **8080–8082, 9000 and 9443**, container names `hackernews-beststories-api`, `hackernews-beststories-web`, `hackernews-beststories-blazor`, `portainer`, and subnet **172.30.80.0/24** are available. Check Docker networks and LAN/VPN routes for overlaps; conflicts are deployment/environment blockers, not application defects. Review any `.env`, `HN_*`, or Compose overrides before interpreting a run as the default configuration. Default ports bind on all host interfaces, not just loopback; restrict Portainer's published bindings or firewall access before starting on an untrusted network.

From the repository root:

```powershell
docker compose config --quiet
docker compose up --build
```

| Endpoint | URL |
|---|---|
| API | http://localhost:8080/api/best-stories?n=20 |
| API liveness | http://localhost:8080/health |
| API documentation (Scalar) | http://localhost:8080/swagger |
| React | http://localhost:8081 |
| Blazor | http://localhost:8082 |
| Portainer (HTTPS; certificate trust may require local setup) | https://localhost:9443 |
| Portainer (HTTP; also published by Compose) | http://localhost:9000 |

Compose waits for API `/health` before starting both UIs. This is application **liveness**, not an upstream check. React inherits `Dockerfile.react`'s health check of nginx's root page (`/`), not the proxied API `/health`; Blazor has no health check. These startup/health signals prove neither upstream readiness nor browser interactivity. Portainer has no API startup dependency. CI builds three application images, then runs isolated HTTP and Chromium stacks sequentially using those images (see [CI gates](#continuous-integration)); this does not test Portainer or prove default-stack subnet isolation.

### Docker validation scope

The Docker gate builds the three application images once and reuses those exact images for sequential, isolated stacks: a four-story HTTP smoke check followed by the separate 25-story Chromium scenarios for both UIs. See [Tests and build](#tests-and-build) for local commands and [Continuous integration](#continuous-integration) for the configured workflow gates.

The smoke stack uses a local upstream fixture, not live Hacker News, and starts only `upstream`, `api`, `web` and `blazor`; Portainer is deliberately excluded. Its API trusts only nginx's fixed `HN_PROXY_IP` (the proxy address and its `/32`), whereas default and release Compose use the configured application subnet for proxy trust. A passing fixture smoke or browser check therefore does not validate live-upstream behavior, Portainer, or default/release subnet-wide trust and isolation. The fixture network is not an outbound firewall.

The endpoint health signals, fixed startup/request timeouts, browser report requirements, diagnostics, and cleanup behavior described above remain the scope of these checks. They do not replace the default-stack liveness limitations or the timeout and safe-shutdown guidance below; normal shutdown retains named volumes, while `--volumes` is only for an intentionally disposable deployment.

### Diagnostics and shutdown

```powershell
docker compose ps -a
docker compose logs --no-color --tail 100 api web blazor portainer
docker compose logs -f api
Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:8080/health' -TimeoutSec 10
Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:8080/api/best-stories?n=20' -TimeoutSec 130
# Stop all four services while retaining both named volumes:
docker compose down --timeout 10
```

Normal `down` retains `api-logs` and `portainer_data`; restarting reuses Portainer's persisted state. Adding `--volumes` deletes these Compose-managed volumes, including Portainer's configuration/data, so use it only for an intentionally disposable deployment. Stopping this stack also stops its Portainer management UI; it is not a request to stop unrelated Docker workloads. Do not prune other deployments' resources.

The API logs to the **console**; the mounted `api-logs` volume is not currently a file-log destination. A healthy API with cold `502`/`504` story responses can indicate upstream DNS, connectivity or timeout trouble: inspect logs before attributing it to image/startup failure. Check registry connectivity for build/download failures. Blazor logs also warn that its container-local Data Protection keys are not persisted/encrypted; container replacement can invalidate protected data. Do not stop unrelated containers or prune pre-existing networks/volumes when diagnosing conflicts.

## nginx reverse proxy (React image)

`src/HackerNews.BestStories.React/nginx.conf` is baked into `Dockerfile.react`.

| Request | What nginx does |
|---|---|
| `/api/...` | `proxy_pass http://api:8080` with **no URI path**, so `/api/best-stories?n=20` is forwarded intact |
| `/health` | Proxied to the API liveness check |
| `/swagger`, `/openapi/...` | Proxied API documentation |
| `/assets/*` | Vite hashed files, cached 7 days |
| everything else | SPA `try_files` → `index.html` |

`api` is the Compose service name. A standalone `docker run` of the web image cannot resolve it unless you put both containers on one network.

The API accepts `X-Forwarded-For` and `X-Forwarded-Proto` **only from explicitly trusted peers**, consuming one hop before rate limiting and HTTPS redirection. nginx appends the real connecting address and supplies its scheme; a caller-supplied earlier forwarding chain cannot override that last hop. `X-Real-IP` is not used for identity. The limiter permits 60 requests per minute per observed client IP, per API instance; NAT clients can share a bucket. IPv4-mapped IPv6 addresses are normalized to IPv4 before partitioning, so equivalent direct and trusted-proxy client addresses share one allowance. Native IPv6 identities remain distinct; a NAT/proxy that genuinely changes the observed address can still change identity.

Default Compose uses the dedicated `hn-backend` network with dynamic application addresses. `HN_DOCKER_SUBNET` (default `172.30.80.0/24`) sets both its subnet and the API's `ReverseProxy__KnownNetworks__0`. Thus **every peer in that subnet**, not exclusively nginx, is trusted to supply forwarding headers and can affect client identity and scheme. The one-hop limit does not narrow which peers are trusted. Prefer narrowly trusted individual proxies for deployments requiring stronger isolation; do not trust the entire internet or containers you do not control. `HN_PROXY_IP` configures only the smoke override, not default Compose.

Timeouts must stay coordinated: `HN_REQUEST_TIMEOUT_SECONDS` defaults to 10 seconds per upstream call; `HN_REFRESH_TIMEOUT_SECONDS` defaults to 120 seconds for the overall API refresh and also declares that deadline to Blazor. Keep `HN_BLAZOR_TIMEOUT_SECONDS` (default 130) **greater** than the refresh deadline, with transit headroom, or Blazor rejects startup. nginx's fixed `proxy_read_timeout 125s` accommodates the default refresh; if increasing the refresh budget, update `src/HackerNews.BestStories.React/nginx.conf` to exceed it and rebuild `web`. An environment override alone does not change nginx's baked-in timeout.

Example PowerShell deployment overrides (choose ranges suitable for your environment):

```powershell
$env:HN_DOCKER_SUBNET = '172.30.81.0/24'
docker compose config --quiet
docker compose up --build
```

This proxy is **not** in front of Blazor. Blazor Server on 8082 talks to Kestrel directly. If you later put Blazor behind nginx you must proxy `/_blazor` with `Upgrade` / `Connection "upgrade"` and a long `proxy_read_timeout`. Do not add that to the React config.

Blazor's server-side calls identify the Blazor server, not each browser, to the API limiter. Direct API callers use their socket peer address; forwarding is ignored unless that peer is trusted. Additional proxy hops require a separately reviewed deployment configuration; the shipped API consumes only one forwarding hop, from any configured trusted peer (the whole application subnet in default Compose).

## Configuration

Bind these settings under `HackerNews` in `appsettings.json`, or use environment variables such as `HackerNews__CacheTtlSeconds`. Defaults are in `Configuration/HackerNewsOptions.cs`; the current `appsettings.json` does not override them.

| Setting | Code default | Compose default | Meaning |
|---|---|---|---|
| `BaseUrl` | `https://hacker-news.firebaseio.com/v0/` | same | Absolute HTTP(S) upstream root; keep the trailing `/` for relative request paths |
| `MaxStories` | 500 | 500 | Maximum caller `n`, **not** a candidate hydration cap |
| `CacheTtlSeconds` | 60 | 300 | Successful snapshot TTL, starting at fill completion |
| `FailureCacheTtlSeconds` | 30 | 30 | Retry cooldown for stale results or cold upstream errors |
| `MaxConcurrency` | 8 | 20 | Maximum simultaneous detail requests per API instance |
| `RequestTimeoutSeconds` | 10 | 10 | `HttpClient.Timeout` per upstream request |
| `RefreshTimeoutSeconds` | 120 | 120 | Overall shared snapshot deadline (IDs plus details) |

All numeric settings must be positive; invalid settings or a non-HTTP(S)/relative `BaseUrl` fail startup. Compose exposes `HN_BASE_URL`, `HN_REQUEST_TIMEOUT_SECONDS`, and `HN_REFRESH_TIMEOUT_SECONDS` overrides; other values can be changed with a Compose override file.

`ReverseProxy:KnownProxies` is an array of IP addresses; `ReverseProxy:KnownNetworks` is an array of bounded CIDR networks. Both default to empty, which disables forwarding even for loopback. Invalid addresses/networks and `/0` networks fail startup. Use `ReverseProxy__KnownProxies__0` for the first trusted IP outside Compose. Prefer individual proxy addresses; a configured network trusts every peer in that range. Default Compose populates `ReverseProxy__KnownNetworks__0` from `HN_DOCKER_SUBNET` (default `172.30.80.0/24`), not `KnownProxies[0]`. The smoke override replaces that network with the pinned proxy's `/32` and also sets its individual trusted IP.

`OpenApi:Enabled` (default `true`) serves `/openapi/v1.json` and Scalar interactive API docs at `/swagger` (redirects to `/swagger/`). Set it to `false` to turn both off.

## Caching

- Loading is **demand-driven**. There is no scheduled warmup or background refresh service. Cold or expired demand shares one per-key population task; callers requesting different `n` reuse the same full ranking.
- Fetch `beststories.json`, deduplicate all IDs, and hydrate all candidates with bounded workers. Sort usable stories by descending score, then internal ID ascending for stable ties; apply `Take(n)` last. A successful fill performs one ID request plus one detail request per distinct candidate, independent of caller count.
- `IBestStoriesService.RefreshAsync` actively loads and atomically replaces the cache entry without evicting it first. Warm reads can use the existing unexpired snapshot during refresh; expired reads wait. Concurrent refresh/read demand joins the same active fill. Refresh is not exposed as an extra HTTP endpoint.
- An HTTP, malformed-response, or timeout failure rejects the entire attempted snapshot. If a successful snapshot exists, serve it unchanged and cache that stale outcome for `FailureCacheTtlSeconds`. Otherwise cache the typed failure outcome and return `502` (upstream failure) or `504` (request/refresh timeout), **not** a successful empty array.
- During the failure cooldown, normal reads and explicit refresh do not restart upstream work. Explicit refresh uses only the remaining TTL and cannot extend the retry deadline. At expiry the next demand retries; another failure starts a new cooldown. **Stale snapshot age is not bounded by the cooldown**: an extended outage can keep the last successful snapshot indefinitely until a later fill succeeds or the process restarts.
- A caller disconnect cancels only that caller's wait, not other callers' shared work. Shared work stops on its refresh deadline or application shutdown. Unexpected application faults remain `500`, not stale successes; caller cancellation is not classified as an upstream timeout.
- Coordination, caching, and detail-request concurrency limits are per API instance, not distributed across replicas.

## Continuous integration

The CI pipeline is triggered by PRs to `main` / `master`, pushes to `main` / `master`, and `v*` tag pushes.

### Pipeline overview
- **Gates:** Independent API and Blazor native MTP suites, React unit tests, and OpenAPI contract validation.
- **Shared-image checks:** Builds three Linux/amd64 images (API, React, Blazor), followed by sequential isolated HTTP smoke (local upstream fixture) and Chromium UI (25-story scenarios) tests using those exact images.
- **Release:** Offline regressions, diagnostic retention, and tag-only packaging. Publication requires successful `test`, `ui-test` and `docker` jobs; PR-only jobs are gated.

### OpenAPI diff in CI
[`openapi/openapi-v1.json`](openapi/openapi-v1.json) is the committed published v1 contract. The `test` job exports live `/openapi/v1.json` from the test host (`OPENAPI_EXPORT_PATH=openapi/current-v1.json`), uploads it as `openapi-current`, requires the export and compares it against the baseline using [oasdiff](https://github.com/oasdiff/oasdiff) with `fail-on: ERR` (equivalent to `breaking --fail-on ERR`).

On pull requests, the separate `openapi-pr` job compares the committed spec on the base branch against the committed PR spec. If the base branch does not yet contain the file, it skips the comparison. This job does not run .NET tests or export a live document. Both comparisons are configured to fail on breaking changes classified as errors, such as removing documented fields or tightening parameter requirements.

The shared `docker` job has exactly three Linux/amd64 image builds (API, React, Blazor), followed by the four-story HTTP gate and both 25-story Chromium UI cases on fresh isolated stacks. It retains stack/browser diagnostics even on failure. Valid version tags additionally package those exact tested image IDs without rebuilds. Only successful `test`, `ui-test` and `docker` jobs enable publication; the PR-only job's skipped status does not block tags. Superseded PR runs cancel, but active tag publication does not. Read-only permissions are the default; write credentials are confined to publication.

Export the live test-host document and validate it against the committed contract, from the repository root. PowerShell (restores any previous environment value):

```powershell
$previousOpenApiExportPath = $env:OPENAPI_EXPORT_PATH
try {
    $env:OPENAPI_EXPORT_PATH = Join-Path $PWD 'openapi\current-v1.json'
    dotnet test --project tests\HackerNews.BestStories.Api.Tests\HackerNews.BestStories.Api.Tests.csproj -c Release --filter-class 'HackerNews.BestStories.Api.Tests.OpenApi.OpenApiContractTests'
} finally {
    if ($null -eq $previousOpenApiExportPath) {
        Remove-Item Env:OPENAPI_EXPORT_PATH -ErrorAction SilentlyContinue
    } else {
        $env:OPENAPI_EXPORT_PATH = $previousOpenApiExportPath
    }
}
```

Bash (environment value scoped to this command only):

```bash
OPENAPI_EXPORT_PATH="$PWD/openapi/current-v1.json" \
  dotnet test --project tests/HackerNews.BestStories.Api.Tests/HackerNews.BestStories.Api.Tests.csproj \
  -c Release --filter-class 'HackerNews.BestStories.Api.Tests.OpenApi.OpenApiContractTests'
```

`OpenApiDocument_ContainsExpectedBaselinePathsAndSchema` writes the export after its assertions pass. For an intentional contract change, obtain the new document from the running API, review and update `openapi/openapi-v1.json` and the test baseline deliberately, then rerun the checks. Do not blanket-ignore differences or overwrite a baseline merely to make CI green; breaking changes against the base branch still require resolution. The published v1 file names the OpenAPI document, not a new `/api/v1` route.

## Version-tag releases

Only pushed `vMAJOR.MINOR.PATCH[-prerelease]` tags qualify (for example `v1.2.3` or `v1.2.3-rc.1`). Leading-zero numeric identifiers, build metadata and malformed `v*` tags fail before image builds. PRs/branches never publish. No tag is created by the publisher. Tag movement, wrong checkout/manifest commits, incomplete bundles, changed image identities or failed required jobs block publication.

For `v1.2.3`, the release has exactly these eight uploaded assets:

| Asset | Contents |
|---|---|
| `hackernews-1.2.3-api-linux-amd64.zip` | Framework-dependent API publish output |
| `hackernews-1.2.3-blazor-linux-amd64.zip` | Framework-dependent Blazor output and static assets |
| `hackernews-1.2.3-react.zip` | Production static files and nginx/proxy configuration |
| `hackernews-1.2.3-api-image-linux-amd64.zip` | Loadable API image TAR |
| `hackernews-1.2.3-blazor-image-linux-amd64.zip` | Loadable Blazor image TAR |
| `hackernews-1.2.3-react-image-linux-amd64.zip` | Loadable React/nginx image TAR |
| `hackernews-1.2.3-all-linux-amd64.zip` | All six payloads under `applications/` and `images/`, not nested ZIPs |
| `SHA256SUMS` | SHA-256 hashes of the seven ZIPs |

Every ZIP includes `README.md`, `compose.release.yml` and `manifest.json` with schema/kind/version/commit/platform, source image tags/IDs and relative payload hashes. The manifest does not hash itself; `SHA256SUMS` covers whole archives. ZIPs must be strictly below 2 GiB. There are no Windows/ARM or self-contained bundles, fixture images, test executables, source/build caches, credentials or runtime logs.

### Install on Linux/amd64

Download the desired ZIP(s) and `SHA256SUMS`. Compare each downloaded ZIP's SHA-256 against its named record before extracting (`sha256sum <file>` on Linux, or `Get-FileHash -Algorithm SHA256 <file>` in PowerShell). If all seven ZIPs are downloaded, `sha256sum --check SHA256SUMS` verifies the complete set. Extract into a clean directory. The combined ZIP is the simplest Docker installation; from its extraction root:

```sh
docker load --input images/api.tar
docker load --input images/blazor.tar
docker load --input images/react.tar
docker compose -p hackernews-release -f compose.release.yml config --quiet
docker compose -p hackernews-release -f compose.release.yml up -d --no-build --pull never
```

Check each command's exit status before continuing. No checkout, rebuild or registry is needed. API/React/Blazor use ports 8080/8081/8082, with live Hacker News access required for stories. Release Compose retains the default timeouts, health dependencies, log-volume definition and **subnet trust**, but excludes Portainer and fixtures. Review ports/subnet collisions first. Unlike smoke's single trusted proxy, all peers in the release subnet are trusted; keep it private. Stop with `docker compose -p hackernews-release -f compose.release.yml down`; do not add `--volumes` unless intentionally deleting deployment data. The API currently logs to console, not the mounted log volume.

For standalone applications, install the **.NET 10 ASP.NET Core runtime for Linux x64** (not only the base runtime; no SDK required). Preserve the entire publish directory, including Blazor `wwwroot`. Run in separate Linux terminals:

```sh
cd applications/api
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_HTTP_PORTS=8080 dotnet HackerNews.BestStories.Api.dll
```

```sh
cd applications/blazor
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_HTTP_PORTS=8082 ApiBaseUrl=http://localhost:8080/ dotnet HackerNews.BestStories.Blazor.dll
```

`ApiBaseUrl` must be reachable from the Blazor **server**, with a trailing slash; replace localhost for a remote API. Configure proxy trust explicitly outside Compose. React's application ZIP does not contain nginx: serve `applications/react/html` with `applications/react/nginx/default.conf`. It requires an HTTP server, SPA fallback and same-origin `/api` proxy, not `file://`. The supplied configuration expects Docker DNS (including its resolver) and `api:8080` on the shared Docker network; outside Docker, adapt upstream/DNS configuration deliberately. Prefer the supplied image to preserve the tested proxy policy. Individual application ZIPs' Compose files require all three separately loaded images; they do not build from the application payloads.

### Publication and recovery

[`build/package-release.ps1`](build/package-release.ps1) consumes verified Smoke/Browser metadata and versioned images; it never rebuilds. [`build/publish-release.ps1`](build/publish-release.ps1) takes `-Tag`, `-Commit`, `-Repository owner/repository`, and `-BundleDirectory`. CI transfers only the bundles/checksums with seven-day retention and no additional artifact compression, and downloads by the exact producing job's artifact ID (names include run ID/attempt).

The publisher revalidates downloaded bundles, resolves lightweight/annotated remote tags to the checkout commit, creates a commit-identified draft or resumes a matching one, skips only identical uploaded assets, verifies remote names/sizes/SHA-256 digests, and rechecks tag/draft identity before publication. Conflicts and already-published releases are refused, never overwritten. Failures after draft creation leave it unpublished for inspection/retry; retain the exact original bundles when retrying. Prereleases are explicitly marked; **all automatic releases use `make_latest=false`**, so prereleases cannot replace stable/latest releases. A maintainer can promote an appropriate stable release separately. GitHub CLI calls are bounded to five minutes and require server-provided SHA-256 asset digests; missing digests fail closed.

## Validation evidence

This documentation review relied on configured workflow analysis and previously reported local execution results. No new hosted CI run, real Chromium stack, complete real-image packaging run, or live publication was performed. See the [Continuous integration](#continuous-integration) overview for current workflow gating and [release regression commands](tests/release/README.md) for detailed verification steps.

## Assumptions

- Hacker News `beststories.json` selects the candidate pool, **not a guaranteed score order**. All distinct candidates are hydrated before ranking; this API does not use a different collection such as `topstories`. The upstream can change during hydration, so this is a complete fetched snapshot, not a transactional instant across Firebase.
- `n` is an integer from 1 to `MaxStories` (default 500). Missing, malformed, or out-of-range `n` is `400`.
- Successfully fetched JSON `null`, non-story items, and null/empty/whitespace titles are excluded. A genuine shortage returns fewer than `n`. A valid empty ID collection is a successful `[]`; a null/malformed collection or failed fetch is not.
- A usable story with an absent/blank URL uses `https://news.ycombinator.com/item?id={id}` as `uri`. Missing `by` maps to an empty `postedBy` string. The response has exactly the six documented fields, with no public `id` or `url`.
- `time` is Unix seconds from HN, emitted as UTC ISO-8601 (`2019-10-12T13:43:01+00:00`). For usable stories, values outside .NET's supported range (`-62135596800` through `253402300799`, inclusive) are malformed upstream data: reject the refresh and use stale data or cold `502`, with the same failure cooldown. Interrupted upstream response bodies follow this failure policy too.
- `commentCount` is HN `descendants` (missing treated as 0).
- One in-process cache entry serves every `n`. That is enough for a single instance. Two API replicas each talk to HN; this repo does not add Redis.
- Last-known-good is only used after a fully successful fill; partial HTTP/JSON failures never publish a partial ranking. Cold failures return `502`/`504`, while stale successful snapshots can outlive repeated retry cooldowns.
- `/health` is process liveness, not “Firebase is reachable.”
- The React image hostname `api` only resolves on the Compose network. Blazor is not behind that nginx proxy.
- Tests substitute the upstream client or HTTP handler while service/cache integration tests retain the real implementations. Docker smoke checks use a local HTTP fixture; tests do not call Firebase or load-test public Hacker News.

## Enhancements (given more time)

- Shared cache (Redis / HybridCache L2) so multiple API replicas do not each hydrate HN.
- Readiness check distinct from `/health` (cache warm, or last-good present).
- Per-version OpenAPI documents and Swagger entries, not a single `v1` document.
- Automate reviewed OpenAPI baseline updates; keep oasdiff `--fail-on ERR` and the existing published/live contract checks.
- Pact or consumer-driven contracts if an external client repo appears.
- UI retry/backoff already exists; add a visible “retrying…” state and stop after a circuit-open signal if the API exposes one.
- Structured request-id propagation from nginx / Blazor into application logging (currently UTC console logging).
- Authentication is out of scope for the brief; if this were internal, add an authn gateway rather than app-level API keys.
- AWS (the role’s platform) is not in this take-home. The same Docker images would sit behind an ALB / ECS or App Runner without changing the v1 contract.
