# CLI adapter

Design notes for `DotNetAgentSurface.CommandLine`, which turns catalog
descriptors into a generated CLI. See the
[development hub](../../DEVELOPMENT.md) for how this fits with the rest of
the project, and [core catalog and abstractions](core-catalog.md) for the
shared model it consumes.

## CLI adapter

The CLI adapter will use System.CommandLine to generate commands and options from the same descriptors. It should provide:

- predictable category and command naming;
- required options and defaults equivalent to MCP binding;
- discoverable `--help` output;
- machine-readable TOON output by default plus an explicit JSON mode;
- meaningful exit codes;
- non-interactive confirmation flags for destructive operations;
- cancellation propagation where practical.

Hosts that authenticate a caller out of band can pass an
`OperationInvocationContext` to `OperationCommandLineAdapter` or to an individual
`ExecuteAsync` call. The context is never read from operation arguments.

Human-friendly formatting can be layered on later, but automation must have a stable output mode from the start.

## Discovering commands

The root invocation (`<executable> --help`, or no arguments) lists uncategorized
operations and top-level categories. When categories are present, the generated
help also states the drill-down form:

```text
<executable> <category> [<sub-category> ...] --help
```

Run that command at each level to list its operations and nested categories, then
invoke a displayed operation path with its flags. For example,
`tasktracker-cli projects --help` explores the `projects` category and
`tasktracker-cli projects archived --help` explores a nested `archived` category.

`SkillGeneratorCommand` deliberately remains independent of
`OperationCommandLineAdapter`, so a host that opts into the `generate` and
`check` skill-reference verbs must advertise and dispatch them itself. The sample
CLI hosts use this pattern:

```csharp
var result = SkillGeneratorCommand.CanHandle(args)
    ? await SkillGeneratorCommand.ExecuteAsync(
        args,
        catalog,
        outputDirectoryDefault: "skill",
        generationOptions: skillOptions)
    : await adapter.ExecuteAsync(args);
```

When using that optional command surface, make its two verbs visible in the
host's own root help or usage banner (for example,
`generate [--output <directory>]` and `check [--output <directory>]`). Keeping
this host-level avoids claiming those verbs exist for hosts that don't wire
`SkillGeneratorCommand`.

## AXI and token efficiency

Generated CLIs should support the [Agent eXperience Interface (AXI)](https://github.com/kunchenguid/axi/blob/main/.agents/skills/axi/SKILL.md) conventions. This is an output-boundary concern: operation inputs, schemas, and normalized results remain JSON-compatible internally, while the CLI can render compact [TOON](https://toonformat.dev/) by default for agent-oriented use and retain an explicit JSON mode for interoperability and scripting.

The CLI adapter and operation metadata should make it possible to:

- emit minimal list projections by default and expose `--fields` for additional fields;
- truncate large values with their total size and an actionable `--full` escape hatch;
- include cheap aggregate counts or derived status when these prevent a likely follow-up call;
- represent successful empty results explicitly rather than emitting ambiguous blank output;
- return structured, actionable errors and meaningful exit codes without leaking dependency details;
- treat already-satisfied mutations as successful no-ops where the operation declares idempotent semantics;
- reject unknown commands, arguments, and options before invocation;
- complete every operation non-interactively through arguments and options;
- keep stdout structured and route progress and diagnostics to stderr;
- provide a compact no-argument discovery view with common commands and next steps.

AXI presentation policy must not be inferred solely from arbitrary DTO shapes. `OperationDescriptor` will need optional output metadata for summary fields, expandable or truncatable fields, aggregate information, and idempotency. Safe defaults should still work when implementers provide none.

Ambient session hooks described by AXI may be offered later as an explicit, idempotent installation feature. They are complementary to generated skills: hooks provide compact live context at a per-session token cost, while skills load on demand and carry no cost in unrelated sessions. Hook installation must never occur as a side effect of normal commands.

See also: the [`tasktracker-cli` and `legacy-desktop-cli` samples](../../samples/README.md), which exercise `OperationCommandLineAdapter`.
