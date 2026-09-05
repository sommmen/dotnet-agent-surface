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
