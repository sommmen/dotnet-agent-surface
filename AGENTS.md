# Repository guidance

## Overview

.NET Agent Surface is a small library and framework for exposing an
application's existing capabilities to people and AI agents through three
synchronized surfaces: an [MCP](https://modelcontextprotocol.io/) server, a
command-line interface, and generated agent skill documentation. Operations
are implemented once, annotated explicitly, and every surface is generated
from the same `OperationCatalog`. This project is a preview: the public API
is implemented and tested, but still pre-1.0 and may change between preview
releases.

## Related repositories

These repositories consume this library as the `DotNetAgentSurface.*` NuGet
packages, so they are the real-world callers of the public API and useful for
judging the impact of a breaking change. Each is usually cloned as a sibling
folder next to this repository's checkout; otherwise read them on GitHub.

- **Anvilboard** — local-first, single-process issue tracker and dashboard
  backed by one SQLite file. <https://github.com/sommmen/Anvilboard>
- **dotnet-efcore-mcp** — MCP server that queries an existing EF Core
  application's database by reflecting over its `DbContext`.
  <https://github.com/sommmen/dotnet-efcore-mcp>

## Project structure

- `src/` — six libraries: `DotNetAgentSurface.Core`, `.CommandLine`, `.Mcp`,
  `.AspNetCore`, `.Hangfire` and `.SignalR`.
- `tests/` — one test project per library (xUnit v3 on Microsoft.Testing.Platform).
- `samples/` — host applications demonstrating each surface.
- `docs/development/` — design slices and `tracking.md`; `features/` — feature plans.
- `DotNetAgentSurface.slnx` — the solution. See
  [DEVELOPMENT.md](DEVELOPMENT.md#repository-layout) for the full layout.

Build with `dotnet build DotNetAgentSurface.slnx` and test with
`dotnet test DotNetAgentSurface.slnx`.

## Commit conventions

Use Conventional Commits for every commit message and pull request title:

```
<type>(<scope>): <description>
```

- `type` — one of `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `build`,
  `ci`, `perf`, `style`
- `scope` — the module, package, or area the change touches
- `description` — a short, imperative summary of the change

Examples:

- `feat(auth): add refresh token rotation`
- `fix(api): handle null response from upstream service`
- `chore(deps): bump dependency versions`

<!-- comment-policy:begin - managed by the stallions comment-policy skill, edit it there -->

## Code comments

Default to writing no comment. A comment has to earn its place; most of the
time its absence is the correct outcome. Well-named identifiers and small
functions already carry the *what*, so a comment is only worth its maintenance
cost when it carries a *why* that the code genuinely cannot.

Write a comment only when the reason behind the code is non-obvious and a
competent reader could otherwise break it:

- A hidden external constraint, such as an API that rejects a correctly-cased
  header.
- A subtle ordering requirement or invariant that nothing in the signature
  implies.
- A deliberate workaround for a specific bug, platform quirk, or spec defect.
  Name the thing being worked around.
- Behaviour that would read as a mistake to someone who does not know the
  constraint.

Keep it to one line, two at the absolute most. Never a paragraph, never a
multi-line banner.

Do not write:

- Restatements of what the code already says (`// increment the counter`).
- Change narration or history (`// added for bug 123`, `// was previously
  Foo`). That belongs in the commit message and rots in the source.
- Section headers inside a function body (`// --- validation ---`). A function
  that needs signposting needs splitting.
- Restatements of a type annotation or signature.
- `TODO` or `FIXME` markers with no tracked issue and no owner. Fix it or file
  it.
- Commented-out code. Delete it; version control remembers.

Doc comments on public API surfaces (XML docs, JSDoc, docstrings) are a
separate concern, and this rule does not suppress them where the language or
project convention expects them. The same discipline applies to their bodies.

When editing existing code, redundant comments adjacent to the change are fair
game to delete. Do not turn that into a comment-stripping sweep of files the
change does not touch.

An explicit request for more comments overrides all of the above.

<!-- comment-policy:end -->
