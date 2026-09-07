# Release regression commands

Use PowerShell 7.4 or newer. No .NET suites, image builds, stack startup, Git
tags, releases, or credentials are needed by these tests.

Fast, credential-free regression (controlled Docker boundary, real packager,
ZIP/TAR parsing and validation; normally a few seconds):

```powershell
pwsh -NoProfile -File tests\release\test-package.ps1
pwsh -NoProfile -File tests\release\test-publish.ps1
pwsh -NoProfile -File tests\release\test-publish.ps1 -Stable
pwsh -NoProfile -File tests\release\test-workflow.ps1
```

Publisher regressions reuse the package fixture through `-BundleAction`, retaining
all production ZIP/TAR/hash validation and cleanup. Only GitHub CLI and checkout
identity are substituted; no GitHub request is made. Both version modes test tag
peeling/movement, draft create/resume, identical partial/complete drafts, upload
failures/corruption, conflicts, incomplete local/remote sets and published-release
protection. Small owned PowerShell children test native exit/timeout handling.
Use a 60-second deadline for each script. `test-workflow.ps1` exercises the actual
release-job predicate and checks the retained MTP/OpenAPI/image/artifact contract;
also run `actionlint -shellcheck= .github/workflows/ci.yml` for YAML/Actions syntax.
Neither test emulates hosted Actions or proves real Chromium/image gate success.

Real local image extraction, loadable TAR round trips and default/release
Compose runtime comparison (start with a 120-second command deadline):

```powershell
pwsh -NoProfile -File tests\release\test-images.ps1 `
  -ApiImage hackernews-beststories-api:step3 `
  -WebImage hackernews-beststories-web:step3 `
  -BlazorImage hackernews-beststories-blazor:step3
```

This creates unique temporary Docker tags, pristine stopped network-none
containers and invocation-owned directories. It removes only its own resources;
source tags/images remain. Docker operations check exit codes and have deadlines.
It never starts applications or contacts Hacker News. Without metadata this
tests packaging components, **not** successful browser validation or the complete
real-image release gate.

To exercise real seven-bundle packaging too, call the script from PowerShell
with `-TestedStackMetadata @('TestResults/http/stack.json',
'TestResults/browser/stack.json')`. Both reports must be verified, must represent
distinct Smoke and Browser scenarios, and must contain the same three image IDs.
The regression deletes its generated bundles after validation.

## Retaining validated bundles

After both stack gates have succeeded, invoke the production packager from
PowerShell (the output directory must not already exist):

```powershell
& .\build\package-release.ps1 -Version '1.2.3' -Commit $commit `
  -ApiImage 'hackernews-beststories-api:1.2.3' `
  -WebImage 'hackernews-beststories-web:1.2.3' `
  -BlazorImage 'hackernews-beststories-blazor:1.2.3' `
  -TestedStackMetadata @('TestResults/http/stack.json', 'TestResults/browser/stack.json') `
  -OutputDirectory 'artifacts/releases/1.2.3'
```

`$commit` must be the full lowercase 40-character commit matching the images'
OCI revision labels. Version labels must equal the normalized version (no `v`,
no build metadata); references must have that exact version tag. Retagging is
not rebuilding, but changing labels would change identity and require retesting.
The packager requires both final images' publish outputs at `/app`; the
Dockerfiles' `/app/publish` paths belong to their discarded build stages.

Shared functions in `build/release/Release.Common.ps1` are intended for the
publisher too: `Get-ReleaseVersion`, `Get-ReleaseAssetNames`, and
`Assert-ReleaseBundleSet -Directory ... -Version ... -Commit ...`.
The latter revalidates exactly seven ZIPs plus SHA256SUMS, payload hashes,
source identities (classic Docker or containerd OCI), and per-ZIP size limits.
Docker save defaults to 300 seconds in packaging; other native calls default
to 120 seconds. Archive creation/verification is synchronous and should be
covered by the calling CI job's finite deadline.