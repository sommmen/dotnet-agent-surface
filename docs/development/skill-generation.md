# Skill and reference generation

Design notes for the planned skill/reference generator (a future
`DotNetAgentSurface.Skills` package) that renders `SKILL.md` and its
supporting references from the catalog. See the
[development hub](../../DEVELOPMENT.md) for how this fits with the rest of
the project.

## Skill and reference generation

Skill generation should be deterministic and template-driven. A generated skill directory is expected to contain:

```text
skills/
  <skill-name>/
    SKILL.md
    references/
      commands.md
      schemas.json
```

`SKILL.md` should include:

- YAML frontmatter;
- a concise statement of when to use the skill;
- the CLI executable name;
- command-discovery instructions;
- a small set of representative examples;
- a relative link to the generated command reference.

`commands.md` should document every generated command, parameter, default, safety classification, and example. `schemas.json` should contain the machine-readable schemas used by the catalog. Files should have stable ordering and line endings, and generation should avoid rewriting unchanged output.
