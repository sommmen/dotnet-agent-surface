# Target frameworks and dependencies

Compatibility baseline, dependency choices, and the framework-support
workarounds behind them. See the [development hub](../../DEVELOPMENT.md) for
how this fits with the rest of the project.

The intended compatibility baseline is .NET Framework 4.6.2 or later plus modern .NET. In practice this is implemented as `net10.0;netstandard2.0` multi-targeting for `DotNetAgentSurface.Core`, `DotNetAgentSurface.CommandLine`, and `DotNetAgentSurface.Mcp`: `netstandard2.0` is consumable from .NET Framework 4.6.1+ (a superset of the 4.6.2+ goal) and avoids juggling several raw `net46x` TFMs individually. `ModelContextProtocol` 2.2.0 was confirmed to already target `netstandard2.0` alongside `net8.0`/`net9.0`/`net10.0`, so it was not a blocker.

Dependencies actually used:

- `ModelContextProtocol` for the MCP adapter;
- `System.Text.Json` for JSON Schema generation and (de)serialization (referenced explicitly on `netstandard2.0`, where it isn't part of the shared framework; picked up transitively via `ModelContextProtocol` on `Mcp`);
- `PolySharp` (source-generator, build-time only) to polyfill C# language features (`init`, `record`, `CallerArgumentExpression`) on `netstandard2.0`.

The CLI adapter is hand-rolled directly on top of `System.CommandLine`-style parsing conventions rather than taking a package dependency; JSON Schema generation is done directly against `System.Text.Json.Nodes` rather than via `NJsonSchema`.

Two modern-only BCL APIs had no netstandard2.0 equivalent and were replaced with small hand-written, TFM-uniform helpers (no `#if`, so the exact same source compiles and behaves identically everywhere):

- `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace` → internal `Guard.ThrowIfNull` / `Guard.ThrowIfNullOrWhiteSpace` (using `CallerArgumentExpression` for the parameter name), in [`src/DotNetAgentSurface.Core/Guard.cs`](../../src/DotNetAgentSurface.Core/Guard.cs). The single call site living in the separate `Mcp` assembly was inlined instead of exposing `Guard` via `InternalsVisibleTo`.
- `NullabilityInfoContext` (unavailable on `netstandard2.0`) → internal `NullabilityReader.IsNullable(ParameterInfo | PropertyInfo)`, in [`src/DotNetAgentSurface.Core/NullabilityReader.cs`](../../src/DotNetAgentSurface.Core/NullabilityReader.cs), which reads the compiler-emitted `NullableAttribute`/`NullableContextAttribute` metadata directly via `CustomAttributeData`. This metadata is present in IL regardless of target framework, so the algorithm produces identical results on every TFM. Validated against real `NullabilityInfoContext` output across 12 hand-picked cases (value types, reference types, generics, oblivious code) before adoption, and indirectly covered in CI by the existing schema-generator tests (nullable/required property and parameter detection).

A couple of other modern-only members were swapped for their down-level equivalents in the same spirit: `ValueTask.FromResult(x)` → `new ValueTask<T>(x)`, and the `char`-overload of `string.Join('\n', ...)` → the `string`-overload `string.Join("\n", ...)`.

The core package should keep adapter dependencies isolated so consumers pay only for the surfaces they use. A likely package/project split is:

```text
DotNetAgentSurface.Core
DotNetAgentSurface.Mcp
DotNetAgentSurface.CommandLine
DotNetAgentSurface.Skills
```

Names remain provisional.
