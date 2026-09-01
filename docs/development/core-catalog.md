# Core catalog and abstractions

Design notes for `DotNetAgentSurface.Core`: the shared catalog, invocation
pipeline, and type support that the CLI and MCP adapters build on. See the
[development hub](../../DEVELOPMENT.md) for how this fits with the rest of
the project.

## Goals

The framework should let an implementer describe an application operation once and obtain:

1. an MCP tool;
2. a CLI command;
3. a generated agent skill and command reference.

All three surfaces must share discovery, binding, schema, invocation, policy, and documentation metadata so they cannot silently drift.

## Non-goals for the initial release

- Exposing unannotated public methods automatically.
- Hosting an MCP stdio loop inside a desktop GUI process.
- Replacing application-level authentication or authorization systems.
- Designing a general remote procedure call transport.
- Generating arbitrary natural-language skill content with an LLM at build time.

## Proposed architecture

```text
Application services
       |
Annotated methods or registered delegates
       |
Operation discovery and validation
       |
OperationCatalog
       |
Shared binder, policy pipeline, and invoker
       |
       +-- MCP adapter
       +-- CLI adapter
       +-- Skill/reference generator
```

## Core abstractions

### `AgentOperationAttribute`

Marks methods that are intentionally available to agents and command-line users. Initial metadata should include:

- name;
- description;
- category;
- safety level;
- optional examples.

Names should be stable identifiers suitable for both MCP tools and CLI commands. Discovery must reject duplicate names and invalid signatures with actionable diagnostics.

### `OperationDescriptor`

An immutable description of one operation. It should contain:

- operation name, description, and category;
- `MethodInfo` or invocation delegate;
- declaring/service type when applicable;
- parameter descriptors, including nullability, defaults, and required state;
- input JSON Schema;
- declared and effective return types;
- safety metadata and examples.

Framework-specific MCP and CLI types should not leak into this core model.

### `OperationCatalog`

Discovers or receives operations, validates them, and exposes a stable ordered collection of descriptors. Ordering must be deterministic so generated artifacts are reproducible.

Reflection scanning should only include explicitly annotated methods. Registration by delegate may be added for applications that cannot annotate their service classes.

### Binding and invocation

A common invocation layer should:

1. accept named, JSON-compatible inputs;
2. bind and validate values against operation parameters;
3. resolve the operation target through an abstraction compatible with dependency injection;
4. execute synchronous or asynchronous methods;
5. normalize results and errors;
6. enforce authorization, safety, and confirmation policies.

Adapters should translate transport-specific input and output only. They must not implement separate business validation or authorization rules.

## Type and method support

The first implementation should prefer a deliberately small, testable surface:

- primitives, enums, nullable values, arrays, and simple DTOs;
- JSON-compatible parameters and results;
- instance methods resolved from a service provider;
- synchronous methods;
- `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>`;
- cancellation tokens treated as infrastructure parameters rather than user input.

Unsupported patterns should fail during catalog construction, not during the first live invocation. Candidate later features include streams, progress reporting, polymorphic DTOs, and richer file inputs.

See also: [CLI adapter](cli-adapter.md), [MCP adapter](mcp-adapter.md), [skill generation](skill-generation.md), [safety and security](safety-and-security.md).
