# XML documentation comments as operation metadata

Design notes for sourcing operation and parameter descriptions from C# XML
documentation comments (`/// <summary>`) instead of requiring every operation
to repeat that text inside `[AgentOperation(...)]`. See the
[development hub](../../DEVELOPMENT.md) for how this fits with the rest of the
project, [core catalog and abstractions](core-catalog.md) for the metadata
model this extends, and [skill and reference generation](skill-generation.md)
for the generator that consumes the resulting text.

## Status: proposed plan

Not implemented. Tracked as milestone
"XML doc comments as operation metadata" in [tracking.md](tracking.md).

## Current state

`AgentOperationAttribute` takes the description as a **required positional
constructor argument**:

```csharp
public AgentOperationAttribute(string name, string description)
```

`OperationCatalog.CreateDescriptor(...)` additionally **rejects** a null or
whitespace-only attribute description with
`"Operation '<name>' has an empty description."`, and `OperationDescriptor`
copies the value verbatim (`Description = operation.Description`). Every surface
renders that one string:

- `SkillReferenceGenerator` writes it under each operation heading in
  `references/commands.md`, and again after the em dash in each `SKILL.md`
  command-index line.
- The CLI adapter uses it for command help text.
- The MCP adapter uses it as the tool description.

`OperationParameterDescriptor` exposes `Name`, `ParameterType`, `IsOptional`,
`DefaultValue`, `IsCancellationToken`, and `IsNullable` — there is **no
parameter description at all** today, so generated parameter tables are
type-and-default only.

Discovery satellites that cannot rely on the attribute synthesize placeholder
text instead. For example `DotNetAgentSurface.AspNetCore` produces:

```csharp
$"Invokes ASP.NET Core {description.HttpMethod} /{description.RelativePath}."
```

## Problem statement

The description a developer would naturally write already exists in the code:

```csharp
/// <summary>
/// Marks the task with the supplied identifier as complete.
/// </summary>
/// <param name="taskId">Identifier of the task to complete.</param>
[AgentOperation("complete-task", "Marks the task with the supplied identifier as complete.")]
public Task CompleteTaskAsync(int taskId, CancellationToken cancellationToken) { ... }
```

The text is duplicated, and the duplicate immediately starts to drift: IDE
tooltips, generated API docs, and the generated `SKILL.md` disagree about what
the operation does. It also makes annotating an existing codebase more
expensive than it should be — every method needs a hand-written string even
when it is already documented — and there is no mechanism at all for
per-parameter help.

## Prior art: OpenAPI

Both major .NET OpenAPI stacks already solve exactly this problem from exactly
this input, and they bracket the two implementation strategies available here.

**Swashbuckle (runtime).** `SwaggerGenOptions.IncludeXmlComments(...)` is given
the path to the compiler-produced `.xml` file, parses it at runtime with
`System.Xml`, and maps `<summary>` to the operation summary, `<remarks>` to the
description, `<param>` to parameter descriptions, and `<response code="...">`
to response descriptions. The consumer is responsible for enabling
`GenerateDocumentationFile` and for locating the file, conventionally:

```csharp
var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
options.IncludeXmlComments(xmlPath);
```

**`Microsoft.AspNetCore.OpenApi` in .NET 10 (compile time).** The built-in
OpenAPI document generator reads XML documentation with **no app code changes
at all**: if the project sets `GenerateDocumentationFile`, a Roslyn source
generator processes the XML at compile time, caches the result into generated
code, and the app assembly plus any referenced assemblies with XML
documentation enabled contribute automatically. It supports `<summary>`,
`<remarks>`, `<param>`, `<returns>`, `<response>`, `<example>`,
`<deprecated>`, and `<inheritdoc>`, and it flattens inline tags — `<c>`,
`<code>`, `<list>`, `<para>`, `<paramref>`, `<typeparamref>`, `<see>`,
`<seealso>` — into plain text, replacing `<see cref="SomeType"/>` with a
readable name. Because the work happens at build time there is no runtime
parsing cost and no dependency on shipping a loose `.xml` file.

Both confirm the user-visible contract we should copy: **the consumer sets one
MSBuild property, writes normal doc comments, and the tooling picks them up.**

## Design goals

- An operation can be annotated with a name only; its description comes from
  the method's `<summary>`.
- Explicitly supplied text always wins, so existing catalogs keep byte-for-byte
  identical output and nothing regresses.
- Add parameter descriptions (`<param>`) as new metadata that every surface can
  render, since the catalog currently has none.
- Extraction stays deterministic: identical source produces identical text on
  every machine and OS, preserving the `SkillReferenceGenerator`
  `IsCurrent`/`check` byte-for-byte contract.
- Missing XML documentation is a degraded experience, never a crash.
- No change to binding, invocation, policy evaluation, or schema generation.
- Works on both target frameworks (`net10.0;netstandard2.0`); the runtime
  reader uses only `System.Xml.Linq`, which is available on both.

### Non-goals

- Generating natural-language descriptions with an LLM. This remains an
  explicit project-wide non-goal (see
  [skill generation non-goals](skill-generation.md#non-goals)); we only relay
  text the developer already wrote.
- Turning XML documentation into a *substitute* for opting in. A method still
  becomes an operation only through `[AgentOperation]` or an explicit
  registration — XML comments never make a method agent-visible on their own.
  Attribute-less convention discovery is a separate, security-sensitive
  question and is out of scope here.
- Full DocFX-grade rendering of `<list>`, tables, or nested markup. Inline tags
  are flattened to plain text.
- Changing how safety level, category, aliases, examples, or idempotency are
  declared. Those stay on the attribute; only human-readable text is sourced
  from comments.

## Consumer requirements

The plan must document, in `README.md` and the sample hosts, that a consuming
project needs:

```xml
<PropertyGroup>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <!-- Optional: doc comments are opt-in per member; silence "missing XML comment". -->
  <NoWarn>$(NoWarn);CS1591</NoWarn>
</PropertyGroup>
```

`GenerateDocumentationFile` emits `<AssemblyName>.xml` next to the assembly in
the output directory and, for packable projects, into the NuGet `lib/` folder,
so both `ProjectReference` and `PackageReference` consumers get the file
alongside the DLL by default.

This repository already sets both properties for every packable `src/` project
in [`src/Directory.Build.props`](../../src/Directory.Build.props), so the
framework's own operations and the samples work as soon as the feature lands.

Deployment caveats that must be called out explicitly:

- **Single-file publish** does not embed the `.xml`; it stays a loose file
  beside the executable unless `IncludeAllContentForSelfExtract` or an explicit
  copy is configured.
- **Trimming and Native AOT** do not remove the file, but a trimmed app may not
  keep the reflection metadata needed to resolve members; the compile-time
  strategy (phase 3) is the trim-safe answer.
- **Containers** that copy only the published DLLs must also copy the `.xml`.
- A host that never ships the `.xml` simply falls back to today's behavior.

## Design

### 1. Optional description on `AgentOperationAttribute`

Add a name-only constructor and relax the property to nullable:

```csharp
public AgentOperationAttribute(string name) : this(name, null) { }

public AgentOperationAttribute(string name, string? description) { ... }

public string? Description { get; }
```

`OperationDescriptor.Description` stays non-nullable `string`; the descriptor
constructor is where the fallback chain resolves. This keeps every existing
`[AgentOperation("name", "text")]` call site compiling and behaving
identically, and every consumer of `OperationDescriptor.Description`
unchanged. It is a source-compatible, pre-1.0-acceptable change; the nullable
annotation on the attribute property is the only API-surface break, and it is
noted in `CHANGELOG.md`.

**The existing empty-description guard has to move at the same time.**
`OperationCatalog.CreateDescriptor(...)` currently throws
`OperationCatalogException` when `attribute.Description` is null or whitespace,
which would reject a name-only `[AgentOperation("name")]` during discovery —
before any documentation source could supply a fallback. Relaxing the attribute
without relaxing that check would make the new constructor unusable, so the
validation must be re-pointed at the **resolved** description produced by
[resolution precedence](#4-resolution-precedence): a still-empty result becomes
a diagnostic by default, and a hard failure only under the opt-in strict mode
described in [diagnostics](#7-diagnostics-and-failure-modes). Explicitly passing
`[AgentOperation("name", "   ")]` should keep failing, because that is a typo
rather than an opt-in to inference.

### 2. A documentation-source seam in Core

```csharp
public interface IOperationDocumentationSource
{
    OperationDocumentation? GetDocumentation(MethodInfo method);
}

public sealed record OperationDocumentation(
    string? Summary,
    string? Remarks,
    IReadOnlyDictionary<string, string> Parameters,
    string? Returns);
```

Implementations:

- `XmlOperationDocumentationSource` — loads one or more `.xml` files (explicit
  paths, or probed as `Path.Combine(AppContext.BaseDirectory, assemblyName + ".xml")`
  for each assembly it is asked about), indexes members by documentation
  comment ID, and caches per assembly. Lazy: no file is opened until the first
  lookup for that assembly.
- `NullOperationDocumentationSource` — the default; returns `null` for
  everything, giving exactly today's behavior.
- `CompositeOperationDocumentationSource` — ordered fallback, so a host can
  add its own source (a source-generated table, an embedded resource, a
  localization store) ahead of the file reader.

Wiring: an optional parameter/property on the discovery entry points rather
than a static global, so tests stay isolated and hosts stay explicit.

```csharp
OperationCatalog.Discover(IOperationDocumentationSource? documentation, params Type[] serviceTypes);
OperationCatalogBuilder.UseDocumentation(IOperationDocumentationSource source);
```

`OperationCatalogBuilder.UseDocumentation(...)` applies to every subsequent
`AddFromType`/`Add(...)` call on that builder, so satellite registrations get
the same treatment as attributed methods. A convenience
`UseXmlDocumentation()` overload probes `AppContext.BaseDirectory` for the
declaring assemblies actually registered, matching the Swashbuckle ergonomics
without making the consumer compute paths.

### 3. Documentation comment ID generation

The compiler keys `<member name="...">` on the documentation comment ID (ECMA-334
Annex D). Generating it from a `MethodInfo` is the fiddliest part of the
runtime strategy and needs its own focused tests:

- `M:` prefix for methods; the declaring type is written with `.` for
  namespaces and `+` replaced by `.` for nested types.
- Generic types use `` `n `` arity (`` M:Ns.Repo`1.Get ``), generic methods use
  ``` ``n ``` (``` M:Ns.Repo.Get``1(``0) ```).
- Parameter list is emitted only when the method has parameters, using **fully
  qualified parameter type names**, with generic parameters written positionally
  (`` `0 `` for type parameters, ``` ``0 ``` for method parameters).
- Arrays are `[]` / `[0:,0:]` for multi-dimensional, pointers `*`, and `ref`,
  `out`, and `in` parameters get a trailing `@`.
- Conversion operators append `~ReturnType`.

Operations are ordinary methods, so operators and indexers are out of scope,
but the generator should be written to fail closed: if it cannot produce an ID
it returns `null` and the lookup is simply a miss.

### 4. Resolution precedence

For an operation description:

1. Non-empty `AgentOperationAttribute.Description` (or the explicit description
   passed to `OperationCatalogBuilder.Add(...)`).
2. `<summary>` from the documentation source, normalized.
3. `<summary>` plus `<remarks>` when
   `OperationDocumentationOptions.IncludeRemarks` is set (off by default —
   `SKILL.md` size is a design constraint).
4. The satellite-synthesized description, where one exists.
5. Empty string, plus a diagnostic (see below).

For a parameter description:

1. An explicitly registered description, once explicit-metadata registration
   lands (see
   [explicit-metadata registration](testing-and-open-decisions.md#explicit-metadata-registration-and-source-generation--work-item)).
2. `<param name="...">` from the documentation source, normalized.
3. `null` — parameter tables render as they do today.

The rule is one sentence: **explicit text always beats inferred text**, so
adopting the feature can never change an existing catalog's rendered output.

### 5. Text normalization (determinism)

`SkillReferenceGenerator.IsCurrent` is a byte-for-byte comparison, so extracted
text must be normalized to a canonical form before it reaches a descriptor:

- Strip the leading per-line indentation the compiler preserves from the
  `///` prefix.
- Collapse runs of whitespace (including newlines) to a single space, and trim.
- Flatten inline tags the way .NET 10's OpenAPI generator does:
  `<see cref="T:Ns.Type"/>` and `<paramref name="x"/>` become their readable
  short name; `<c>`/`<code>` become their inner text; `<para>` becomes a
  paragraph break; `<list>` items become a space-separated sentence run.
- Decode XML entities exactly once.
- Normalize line endings to `\n` and never emit a trailing newline inside a
  description value.
- Never emit `\r`, tab, or `|` unescaped into a Markdown table cell.

Because normalization is pure and total, the same source tree produces the same
`SKILL.md` on Windows and Linux, which is the property CI depends on.

### 6. `<inheritdoc/>`

Phase 1 resolves `<inheritdoc/>` only for the trivial, unambiguous case: the
method overrides or implements exactly one base/interface member whose
documentation is present in an already-loaded source. Anything else is treated
as "no documentation". Full `cref`/`path` resolution is deferred.

### 7. Diagnostics and failure modes

Silence is the wrong default for a feature whose whole purpose is to remove a
required argument. Behavior:

- The `.xml` file is missing or unreadable → fall back silently, but record it
  in a report; `XmlOperationDocumentationSource` never throws for I/O or
  malformed XML.
- An operation ends up with an **empty** description → surfaced through a
  diagnostics report in the same shape the Hangfire satellite already uses for
  registration/skip/warning reports, and reported as a warning by the
  `skill generate`/`check` CLI commands. This replaces today's unconditional
  `OperationCatalogException` on an empty *attribute* description; the throw
  survives only for an explicitly supplied blank string and under strict mode.
- Add an opt-in strict mode (`OperationDocumentationOptions.RequireDescription`)
  that makes an empty resolved description a catalog-build failure, so teams
  can enforce "every operation is documented" in CI.

### 8. Satellites

Each satellite gets the same seam, in priority order after any explicitly
supplied text:

- **ASP.NET Core** — controller actions and Minimal API handlers have
  `MethodInfo`s, so their `<summary>` replaces
  `"Invokes ASP.NET Core GET /orders/{id}."`. This is the direct analogue of the
  OpenAPI behavior and the most visible win.
- **Hangfire** — class-based job discovery (`AddHangfireJobTypes(...)`) resolves
  the job method's `<summary>`; storage-discovered recurring jobs have no
  `MethodInfo` and keep their synthesized text.
- **MCP-native** — `DescriptionAttribute` remains the explicit source and keeps
  winning; `<summary>` fills the gap when it is absent.

## Alternative considered: source generator

A Roslyn source generator that bakes descriptions into compiled metadata at
build time — the approach `Microsoft.AspNetCore.OpenApi` took in .NET 10 — is
strictly better on the axes that matter for AOT:

- No runtime XML parsing, no per-assembly cache, no startup cost.
- No dependency on the `.xml` file surviving publish, containerization,
  trimming, or single-file packaging.
- Descriptions are visible to trimmed and Native AOT apps.

It is not the right *first* step:

- It only sees the compilation it runs in, so operations from referenced
  assemblies and from third-party types registered through delegates are
  invisible to it — exactly the cases the ASP.NET Core and Hangfire satellites
  serve. The runtime reader handles them because `.xml` files ship next to
  referenced assemblies.
- It requires `netstandard2.0` analyzer packaging and a second distribution
  channel, which is a meaningful increment on a preview library.
- The repository has already concluded that composition is only fully known at
  runtime for discovery satellites (see
  [discovery satellites](../../features/discovery-satellites.md#source-generator--no)).

The two are complementary, and the `IOperationDocumentationSource` seam is
designed so a generated source can be registered ahead of the file reader with
no change to the catalog, the descriptor, or any surface. The generator is
therefore planned as phase 3, folded into the existing
[explicit-metadata registration and source generation work item](testing-and-open-decisions.md#explicit-metadata-registration-and-source-generation--work-item).

## Rollout phases

**Phase 1 — Core reader and optional description.**
`IOperationDocumentationSource`, `XmlOperationDocumentationSource`,
documentation comment ID generation, text normalization, the name-only
`AgentOperationAttribute` constructor, and the resolution precedence in
`OperationDescriptor`. Default behavior unchanged.

**Phase 2 — Parameter descriptions and surfaces.**
`OperationParameterDescriptor.Description`, rendered in `SKILL.md` reference
parameter tables, CLI option help, and MCP input-schema `description` fields
(`OperationSchemaGenerator` emits per-property `description`). Diagnostics
report and `RequireDescription` strict mode.

**Phase 3 — Satellites, then the optional source generator.**
ASP.NET Core, Hangfire class-based jobs, and MCP-native ingestion consume the
seam. Afterwards, evaluate a source-generated documentation source for
trimmed/AOT hosts.

## Testing strategy

- **ID generation** — table-driven tests over simple, generic, nested-type,
  array, `ref`/`out`/`in`, and generic-method-parameter signatures, asserting
  the exact `M:` string against IDs taken from a real compiler-produced `.xml`
  for the test assembly (the test project enables `GenerateDocumentationFile`,
  so this is self-verifying rather than hand-transcribed).
- **Normalization** — multi-line summaries, inline `<see cref>`/`<c>`/
  `<paramref>`, entity escapes, and CRLF vs LF inputs all collapse to the same
  canonical single-line string.
- **Precedence** — attribute text wins over `<summary>`; `<summary>` wins over
  synthesized; empty everywhere yields the documented fallback and a diagnostic.
- **Determinism** — regenerate a catalog's skill output twice, and once with
  CRLF-normalized XML input, asserting `IsCurrent` stays true; extend the
  existing `SkillReferenceGeneratorTests` size-budget test with an
  XML-sourced catalog.
- **Missing file** — a catalog built with `UseXmlDocumentation()` against an
  assembly with no `.xml` builds successfully and matches the
  no-documentation baseline.
- **Satellites** — ASP.NET Core controller `<summary>` replaces the synthesized
  `"Invokes ASP.NET Core ..."` string; Hangfire class-based jobs pick up job
  method summaries.
- **Samples** — the task-tracker sample drops the duplicated description
  strings and regenerates its committed `SKILL.md` snapshot, proving the
  end-to-end path and giving reviewers a readable diff of the behavior change.

## Documentation updates

- `README.md` — a short "descriptions from XML comments" section with the
  `GenerateDocumentationFile` snippet and the deployment caveats.
- `docs/development/core-catalog.md` — the optional description and the
  documentation seam.
- `docs/development/skill-generation.md` — where descriptions come from, and
  the note that determinism now depends on normalization.
- `CHANGELOG.md` — the nullable `AgentOperationAttribute.Description` change
  and the new opt-in APIs.
