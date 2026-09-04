# .NET Agent Surface

.NET Agent Surface is a small library and framework for exposing an application's existing capabilities to people and AI agents through three synchronized surfaces:

- an [MCP](https://modelcontextprotocol.io/) server;
- a command-line interface;
- generated agent skill documentation.

Implement an operation once, annotate it explicitly, and generate every surface from the same catalog.

> This project is a preview: the public API described here is implemented and tested, but still pre-1.0 and may change between preview releases. See [Status](#status) below.

## Motivation

Applications often implement MCP tools, CLI commands, and agent instructions independently. That duplicates metadata, invocation logic, validation, security policy, examples, and documentation—and allows those surfaces to drift apart.

.NET Agent Surface will use one `OperationCatalog` as their source of truth:

```text
Existing application services
          |
 [AgentOperation] methods
          |
     OperationCatalog
      /      |       \
    MCP     CLI    SKILL.md
```

The catalog will discover only explicitly annotated operations and describe their:

- stable name and human-readable description;
- invocation target (`MethodInfo` or delegate);
- parameters, required values, and defaults;
- input JSON Schema and return type;
- category and safety level;
- examples and documentation.

Adapters will use that metadata to expose equivalent behavior over MCP and the CLI and to render deterministic skill files. The generated CLI will also target the [Agent eXperience Interface (AXI)](https://github.com/kunchenguid/axi/blob/main/.agents/skills/axi/SKILL.md) conventions so agents can discover and consume operations with fewer calls and fewer tokens.

## Proposed usage

```csharp
public class CustomerOperations
{
    private readonly ICustomerService customerService;

    public CustomerOperations(ICustomerService customerService)
    {
        this.customerService = customerService;
    }

    [AgentOperation(
        "find-customer",
        "Find a customer by email address",
        Category = "customers")]
    public Customer FindCustomer(string email)
    {
        return customerService.FindByEmail(email);
    }
}
```

The same operation could then be available as:

```text
myapp-cli customers find-customer --email customer@example.com
```

The category is the command group and the operation name is the leaf command. Nested categories extend the chain from left to right (for example, `projects archived list`), while an operation without a category stays at the root (`myapp-cli find-customer ...`). Category and operation lookup is case-insensitive, but help output is sorted using ordinal rules so the result is deterministic. Two operations that normalize to the same full command path are rejected during catalog construction; the CLI reports the collision as a structured error rather than selecting one based on discovery order.

CLI operation output defaults to token-efficient TOON. Use `--output json` for compact JSON (or `--output toon` explicitly). For object lists, `--fields name,id` selects output fields; `--full` disables the default truncation of long string values.

It is also exposed as an MCP tool named `find-customer` and an entry in generated skill documentation. The category-chain behavior is the current routing decision; the fluent registration, diagnostics, projection, and output-rendering work needed to complete it are tracked in [docs/development/testing-and-open-decisions.md](docs/development/testing-and-open-decisions.md#planned-work-items).

## Planned outputs

A consuming application should be able to produce separate host processes and generated documentation from one shared automation assembly:

```text
MyApp.Automation.dll       Shared catalog and invocation logic
MyApp.McpServer.exe        MCP stdio server
myapp-cli.exe              Human and agent CLI
skills/
  customer-management/
    SKILL.md
    references/
      commands.md
      schemas.json
```

Keeping the MCP server outside a WinForms or WPF process prevents protocol traffic from being mixed with GUI output. A Windows service can either host the shared services directly or expose them to separate hosts through named pipes or HTTP.

## Compatibility and dependencies

`Core`, `CommandLine`, and `Mcp` multi-target `net10.0;netstandard2.0`, which covers .NET Framework 4.6.1+ (the `samples/DotNetAgentSurface.Samples.LegacyDesktop` sample validates the downlevel path against a real `net472` host). Building blocks:

- [MCP C# SDK](https://www.nuget.org/packages/ModelContextProtocol/) for MCP hosting and tool adaptation;
- [System.CommandLine](https://www.nuget.org/packages/System.CommandLine) for generated CLI commands;
- [NJsonSchema](https://www.nuget.org/packages/NJsonSchema/) for DTO and parameter schemas.

## Design principles

- **Explicit exposure:** only annotated methods enter the catalog.
- **One source of truth:** MCP, CLI, schemas, and skill files derive from the same metadata.
- **Shared policy enforcement:** authentication, authorization, validation, and confirmation belong in the common invocation layer.
- **Safe protocol hosting:** MCP stdout is reserved for protocol messages; diagnostics go to stderr.
- **Agent-friendly contracts:** operations prefer simple DTOs and JSON-compatible values.
- **Token-efficient CLI output:** generated CLIs follow AXI conventions, including compact [TOON](https://toonformat.dev/) output, minimal default projections, explicit truncation, and actionable next-step guidance while retaining a stable JSON mode for interoperability.
- **Visible risk:** destructive operations are marked and require an appropriate confirmation policy.
- **Deterministic generation:** identical catalog input produces identical documentation and schemas.

## Development

The planned architecture, milestones, and open design decisions are documented in [DEVELOPMENT.md](DEVELOPMENT.md).

## Status

The core catalog, invocation pipeline, CLI/MCP adapters, skill generator, and the Hangfire/ASP.NET Core/native-MCP discovery satellites are implemented and tested; the next milestone is a trusted invocation-context contract so protected ASP.NET Core endpoints can be safely invoked instead of only cataloged. See [docs/development/tracking.md](docs/development/tracking.md) for the full milestone list and current status.

### Testing prerelease packages

The `publish` workflow automatically pushes `.nupkg` files to GitHub Packages on every push to `main`, producing git-versioned prerelease packages such as `0.1.8-preview.g<commit>`. It can also be run manually (`workflow_dispatch`) from any branch. The workflow only publishes to NuGet.org for a stable GitHub Release, or when its explicit `publish_nuget` input is selected on a manual run.

To consume those packages from another repository, add the GitHub Packages feed. Use a GitHub personal access token with `read:packages` if the package is private:

```powershell
dotnet nuget add source https://nuget.pkg.github.com/sommmen/index.json `
  --name github-dotnet-agent-surface `
  --username YOUR_GITHUB_USERNAME `
  --password YOUR_GITHUB_TOKEN `
  --store-password-in-clear-text
```

Then reference the exact prerelease version in the consuming project:

```xml
<PackageReference Include="DotNetAgentSurface.Core" Version="0.1.8-preview.g<commit>" />
```

### Local package workflow

To try an unreleased change (or iterate on this repository against a real consumer) without waiting on CI or GitHub Packages, pack straight to a local folder and point the consuming project's restore at that folder instead. No GitHub account, personal access token, or network access is required.

1. **Pack the libraries you need** from this repository into a local folder (any empty folder works as a NuGet feed):

   ```powershell
   dotnet pack DotNetAgentSurface.slnx -c Release -o C:\local-nuget-feed
   ```

   Pack a single project instead if you only changed one library, for example `dotnet pack src\DotNetAgentSurface.Hangfire\DotNetAgentSurface.Hangfire.csproj -c Release -o C:\local-nuget-feed`. Each run produces version-stamped `.nupkg`/`.snupkg` files named `DotNetAgentSurface.<Project>.<version>.nupkg`; re-running `dotnet pack` after further edits overwrites files with the same version, so bump the commit (any new commit changes the Nerdbank.GitVersioning-computed height/hash) or pass `-p:VersionSuffix=...` if NuGet's local cache serves a stale copy (see Troubleshooting below).

2. **Point the consuming project at that folder.** Either add it as a NuGet source, or (recommended for a scratch/throwaway consumer) add a `nuget.config` next to the consuming project's solution:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <packageSources>
       <clear />
       <add key="local-dotnet-agent-surface" value="C:\local-nuget-feed" />
       <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
     </packageSources>
     <!-- Restrict the local feed to this package family so it cannot shadow unrelated
          packages that also resolve from nuget.org. Remove if you also use the local
          feed for other experimental packages. -->
     <packageSourceMapping>
       <packageSource key="local-dotnet-agent-surface">
         <package pattern="DotNetAgentSurface.*" />
       </packageSource>
       <packageSource key="nuget.org">
         <package pattern="*" />
       </packageSource>
     </packageSourceMapping>
   </configuration>
   ```

   Alternatively, without a `nuget.config` file, register the folder as a machine/user-level source:

   ```powershell
   dotnet nuget add source C:\local-nuget-feed --name local-dotnet-agent-surface
   ```

3. **Discover the version to pin** by listing the folder or reading the pack output — `dotnet pack` prints the exact `.nupkg` filename, and [`version.json`](version.json) shows the Nerdbank.GitVersioning configuration (major.minor plus a `-preview` prerelease tag) that determines the computed prerelease identifier for the current commit:

   ```powershell
   Get-ChildItem C:\local-nuget-feed\DotNetAgentSurface.Core.*.nupkg
   ```

4. **Reference the exact local version** in the consuming project, matching whatever `dotnet pack` produced (for example `0.1.14-preview.g<commit>`, or a plain `0.1.14-preview` if built from a tagged commit):

   ```xml
   <PackageReference Include="DotNetAgentSurface.Core" Version="0.1.14-preview.g<commit>" />
   ```

5. **Restore** as usual (`dotnet restore` or an IDE restore). NuGet resolves `DotNetAgentSurface.*` from the local folder feed per the source mapping above, and everything else from `nuget.org`.

**Cleanup and troubleshooting:**

- Delete the local feed folder (`Remove-Item -Recurse C:\local-nuget-feed`) and remove the registered source (`dotnet nuget remove source local-dotnet-agent-surface`) once you are done experimenting; neither step touches this repository or any published feed.
- If a restore keeps resolving an older package despite a newer local pack, clear NuGet's global package cache for the affected package/version: `dotnet nuget locals http-cache --clear` and `dotnet nuget locals global-packages --list` (delete the specific `dotnetagentsurface.*` subfolder under the reported path, or bump the version so it no longer collides).
- If restore reports the package cannot be found, confirm the folder path in `nuget.config`/`dotnet nuget list source` is correct and that the `.nupkg` file actually exists there (a relative path is resolved relative to the `nuget.config` file, not the current directory).
- If package source mapping rejects the local feed for a `DotNetAgentSurface.*` package, double-check the `<package pattern="DotNetAgentSurface.*" />` entry — package source mapping is strict once any mapping exists in scope, so every source that should serve a given package needs an explicit pattern.

## License

[MIT](LICENSE).
