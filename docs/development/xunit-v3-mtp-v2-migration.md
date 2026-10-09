# Plan: migrate tests to `xunit.v3.mtp-v2` 4.0.1

Migrates all six test projects from xUnit v2 on VSTest to xUnit v3 on Microsoft
Testing Platform v2, aligning this repository with the estate-wide test standard.

The shared standard, target recipe and CI command reference live in the `stallions`
repository at `docs/guides/Testing/xunit-v3-mtp-v2-standard.md`.

This migration has a bonus: xUnit v3 has **built-in runtime skip**, so the
`Xunit.SkippableFact` dependency can be removed outright rather than carried forward.

## 1. Current state

Six test projects under `tests/`:

| Project | Target frameworks | Extra packages |
|---|---|---|
| `DotNetAgentSurface.Core.Tests` | `net10.0` | `coverlet.collector` |
| `DotNetAgentSurface.CommandLine.Tests` | **`net10.0;net8.0`** | `coverlet.collector` |
| `DotNetAgentSurface.AspNetCore.Tests` | `net10.0` | `FrameworkReference Microsoft.AspNetCore.App` |
| `DotNetAgentSurface.SignalR.Tests` | `net10.0` | — |
| `DotNetAgentSurface.Hangfire.Tests` | `net10.0` | — |
| `DotNetAgentSurface.Hangfire.SqlServer.Tests` | `net10.0` | `Testcontainers.MsSql`, `Xunit.SkippableFact`, `Hangfire.SqlServer`, `Microsoft.Data.SqlClient`, `Newtonsoft.Json` |

Central versions in `Directory.Packages.props`:

| Package | Version | Fate |
|---|---|---|
| `xunit` | 2.9.3 | → `xunit.v3.mtp-v2` 4.0.1 |
| `xunit.runner.visualstudio` | 4.0.0 | remove (VSTest adapter) |
| `Microsoft.NET.Test.Sdk` | 18.10.0 | remove (VSTest host) |
| `coverlet.collector` | 10.0.1 | remove (VSTest collector) |
| `Xunit.SkippableFact` | 1.5.85 | **remove — superseded by v3 built-in skip (§3)** |

Solution: `DotNetAgentSurface.slnx`. CI: `.github/workflows/ci.yml` and
`.github/workflows/publish.yml`.

### Migration surface, measured

| Construct | Count | Impact |
|---|---:|---|
| `[Fact]` | 197 | none |
| `[Theory]` | 7 | none |
| `[SkippableFact]` | 5 | **replace — §3** |
| `IAsyncLifetime` | 1 | **breaking — §2** |
| `[CollectionDefinition]` / `[Collection]` | 1 / 2 | none |
| `IClassFixture` | 0 | none |
| `async void` | 0 | none |
| `ITestOutputHelper` | 0 | none |

23 test source files.

## 2. `SqlServerCompatibilityFixture` — `IAsyncLifetime` returns `ValueTask`

`tests/DotNetAgentSurface.Hangfire.SqlServer.Tests/SqlServerCompatibilityFixture.cs:16`
implements `IAsyncLifetime` with v2 signatures:

```csharp
public async Task InitializeAsync() { ... }
public async Task DisposeAsync() { ... }
```

v3 requires `ValueTask` on both, because the interface now inherits `IAsyncDisposable`:

```csharp
public async ValueTask InitializeAsync() { ... }
public async ValueTask DisposeAsync() { ... }
```

The bodies are unchanged. This is a compile error (`CS0738`), so it cannot be missed.

The fixture provisions a throwaway SQL Server container via `Testcontainers.MsSql` and
is shared through `SqlServerCompatibilityCollection : ICollectionFixture<...>`.
`ICollectionFixture` semantics are unchanged in v3. Verify after migration that
`DisposeAsync` still runs — a leaked SQL Server container is expensive and will not
announce itself as a test failure.

## 3. Replace `Xunit.SkippableFact` with built-in v3 skip

The project file explains the dependency:

> xunit v2 has no built-in runtime skip; SkippableFact lets tests self-report "skipped"
> (instead of failing) when the opt-in environment variable is unset or Docker is
> unavailable.

**That premise no longer holds in v3.** xUnit v3 provides `Assert.Skip(reason)` and
`Assert.SkipWhen(condition, reason)` natively — verified on `xunit.v3.mtp-v2` 4.0.1,
where both correctly reported tests as `skipped` rather than passed or failed.

Changes in `tests/DotNetAgentSurface.Hangfire.SqlServer.Tests/`:

| Before | After |
|---|---|
| `<PackageReference Include="Xunit.SkippableFact" />` | *(delete)* | 
| `[SkippableFact]` × 5 | `[Fact]` |
| `Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason)` | `Assert.SkipWhen(_fixture.SkipReason is not null, _fixture.SkipReason)` |

The five `[SkippableFact]` sites are in `HangfireSqlServerCompatibilityTests.cs` at
lines 26, 50, 72, 90 and 114; the single `Skip.If` helper is `SkipUnlessAvailable()` at
line 135.

Also remove `<PackageVersion Include="Xunit.SkippableFact" Version="1.5.85" />` from
`Directory.Packages.props`. Update the explanatory comment in the project file, since
its stated reason disappears with this change.

Verify explicitly that these tests still report **skipped** — not passed — when the
opt-in environment variable is unset. A `Skip.If` silently becoming a no-op would turn
five container tests into vacuous passes.

## 4. The multi-targeted test project

`DotNetAgentSurface.CommandLine.Tests` targets `net10.0;net8.0` deliberately: `net8.0`
resolves `DotNetAgentSurface.CommandLine` to its `netstandard2.0` asset, exercising that
compatibility path at test time.

`xunit.v3.mtp-v2` 4.0.1 supports `net8.0` (and `net472`), so this multi-targeting
survives the migration. Two things to confirm:

1. Both target frameworks still build and run — MTP produces one executable per target
   framework, so expect two test runs from this project, not one.
2. The reported test count is **double** the single-framework count for this project.
   Account for that when comparing against the pre-migration baseline, which has the
   same property under VSTest.

## 5. Changes

### 5.1 `Directory.Packages.props`

Remove `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`,
`coverlet.collector` and `Xunit.SkippableFact`. Add:

```xml
<PackageVersion Include="xunit.v3.mtp-v2" Version="4.0.1" />
<PackageVersion Include="Microsoft.Testing.Extensions.GitHubActionsReport" Version="2.4.1" />
```

### 5.2 Each of the six test project files

Add to the `PropertyGroup`:

```xml
<OutputType>Exe</OutputType>
<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
```

Replace the removed package references with:

```xml
<PackageReference Include="xunit.v3.mtp-v2" />
<PackageReference Include="Microsoft.Testing.Extensions.GitHubActionsReport" />
```

Keep `<Using Include="Xunit" />`, all `ProjectReference`s, the `FrameworkReference` in
`AspNetCore.Tests`, and every non-test package in `Hangfire.SqlServer.Tests`
(`Testcontainers.MsSql`, `Hangfire.SqlServer`, `Microsoft.Data.SqlClient`,
`Newtonsoft.Json` — the last two carry security-pin comments that must survive).

There is no `tests/Directory.Build.props` here and `src/Directory.Build.props` only
covers `src/`, so set the two properties per project rather than inventing a shared
file for six projects.

### 5.3 New `global.json`

At the repository root, next to `DotNetAgentSurface.slnx`:

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

### 5.4 CI

`ci.yml` and `publish.yml` both run
`dotnet test DotNetAgentSurface.slnx --no-build --no-restore --configuration Release --verbosity normal`,
which keeps working under MTP. Optionally add `--report-gh`.

`publish.yml` also runs `dotnet pack`. The test projects set `IsPackable=false`
individually, and `OutputType=Exe` does not change that, but confirm the published
package set is unchanged.

### 5.5 Downstream coupling with Anvilboard

`sommmen/Anvilboard` checks this repository out as a sibling and references
`DotNetAgentSurface.CommandLine` and `.Mcp` by relative `ProjectReference`. Those are
`src/` libraries, untouched by this plan, so the two migrations are independent and can
land in either order. No coordination needed — recorded so it is not re-discovered.

## 6. Execution order

| # | Step |
|---|---|
| 1 | `Directory.Packages.props` + `DotNetAgentSurface.Core.Tests` — proves the recipe |
| 2 | `SignalR.Tests`, `Hangfire.Tests`, `AspNetCore.Tests` — plain projects |
| 3 | `CommandLine.Tests` — confirm both target frameworks run (§4) |
| 4 | `Hangfire.SqlServer.Tests` — `IAsyncLifetime` + `SkippableFact` removal (§2, §3) |
| 5 | `global.json` and the CI flag |

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| `Skip.If` → `Assert.SkipWhen` silently no-ops | Medium | Run with the opt-in variable unset and confirm 5 tests report **skipped**, not passed (§3) |
| `IAsyncLifetime` signatures | Certain | Compile error; §2 |
| Multi-target count confusion | Medium | `CommandLine.Tests` contributes two runs; §4 |
| Leaked SQL Server container | Low | Verify `DisposeAsync` runs; §2 |
| Zero discovery | Medium | Missing `OutputType=Exe`; compare per-project counts |
| `dotnet pack` output changes | Low | `IsPackable=false` per project; §5.4 |
| `xUnit1051` warning volume | Certain | Advisory here; see below |

### `xUnit1051`

`xunit.v3.mtp-v2` 4.0.1 brings `xunit.analyzers` 2.1.0, which fires `xUnit1051`
("calls to methods which accept `CancellationToken` should use
`TestContext.Current.CancellationToken`"). A trial migration of a comparable repository
(546 tests) produced **150 of these warnings** and no other new rule.

Neither `src/Directory.Build.props` nor the test projects set `TreatWarningsAsErrors`,
so the build still succeeds and these are advisory. Land the migration first and fix
the sites as follow-up work.

## 8. Verification

```powershell
dotnet restore DotNetAgentSurface.slnx
dotnet build DotNetAgentSurface.slnx --configuration Release
dotnet test DotNetAgentSurface.slnx --no-build --configuration Release --verbosity normal
dotnet pack DotNetAgentSurface.slnx --configuration Release
```

Definition of done:

1. No `xunit` (v2), `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`,
   `coverlet.collector` or `Xunit.SkippableFact` entry remains.
2. All six projects reference `xunit.v3.mtp-v2` 4.0.1.
3. The test count matches the pre-migration baseline per project, with
   `CommandLine.Tests` still contributing both target frameworks.
4. The five SQL Server compatibility tests report **skipped** when the opt-in
   environment variable is unset, and still pass when it is set with Docker available.
5. `ci` green on the pull request and `dotnet pack` output unchanged.
