# NuGet publishing setup

The workflow at [workflows/publish-nuget.yml](workflows/publish-nuget.yml) builds the solution, runs the test executable, packs the three libraries, and publishes them to nuget.org in dependency order. It runs on pushes/merges to `main`, or manually from the Actions tab when `main` is selected. The publish job explicitly checks `refs/heads/main`.

## One-time account setup

1. In `vadiks93/CacheAnalyzer`, open **Settings → Environments** and create an environment named `nuget`.
2. Configure its deployment branches to allow only the selected branch `main`.
3. Add an environment variable named `NUGET_USER` with your nuget.org profile username (not your email address or an API key).
4. On nuget.org, open **your username → Trusted Publishing** and create a GitHub policy with repository owner `vadiks93`, repository `CacheAnalyzer`, workflow filename `publish-nuget.yml`, and environment `nuget`.
5. Allow the policy to publish new packages and new versions for the IDs `CacheAnalyzer.Core`, `CacheAnalyzerHelper`, and `CacheAnalyzer.AspNetCore`. Confirm that these IDs are available or owned by your NuGet account. If an ID is unavailable, set an appropriate `PackageId` in its project and update the workflow's package list.

The workflow uses `NuGet/login@v1` and GitHub OIDC to obtain a short-lived publishing key. A long-lived API-key secret is not required. Account and environment setup must be completed before publishing will succeed.

## Versions and releases

The local development version is in [../Directory.Build.props](../Directory.Build.props), initially `0.1.0`. In GitHub Actions, the workflow uses its major/minor values and the workflow run number as the patch: `0.1.1`, `0.1.2`, and so on. The generated version is shown in the run summary and passed consistently to restore, build, and pack for all three libraries, so assembly/package versions and project-reference dependencies match.

Each push to `main` triggers a new run and automatically attempts to publish a new version after tests pass. A push containing multiple commits generates one version for that run. Starting a new manual run also uses a new number. Failed or superseded pending runs can leave gaps; rerunning an existing run keeps its version. Publishing uses `--skip-duplicate`, so a rerun can retry an interrupted release without replacing packages that already exist. Publication of the three packages is not atomic.

No version-bump commits or local hooks are needed; CI does not edit `Directory.Build.props`. To change the major/minor series, change its `<Version>` (for example to `0.2.0`); the workflow counter continues across series. Avoid manually publishing versions in the CI counter's future range, since duplicate versions are skipped. If this workflow previously ran, its next version uses the current counter rather than necessarily starting at `0.1.1`.

Package metadata includes the repository, author, description, tags, README, and MIT license expression. Each package includes the repository's `LICENSE` file.

Samples and tests are not packable. Both sample projects are built, but their live-host smoke checks are not part of the publishing workflow.

## Local verification

```powershell
dotnet restore CacheAnalyzer.slnx
dotnet build CacheAnalyzer.slnx -c Release --no-restore
dotnet CacheAnalyzerHelper.Tests/bin/Release/net10.0/CacheAnalyzerHelper.Tests.dll
dotnet pack CacheAnalyzer.Core/CacheAnalyzer.Core.csproj -c Release --no-build -o artifacts/packages
dotnet pack CacheAnalyzerHelper/CacheAnalyzerHelper.csproj -c Release --no-build -o artifacts/packages
dotnet pack CacheAnalyzer.AspNetCore/CacheAnalyzer.AspNetCore.csproj -c Release --no-build -o artifacts/packages
```

These local commands create packages without uploading them.

To simulate a generated CI version locally, pass the same `-p:Version=0.1.123` to restore, build, and every pack command. Use a separate output folder such as `artifacts/version-check` for those packages. This does not change the committed local version.

References: [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), [NuGet login action](https://github.com/NuGet/login), [GitHub deployment branch rules](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments).
