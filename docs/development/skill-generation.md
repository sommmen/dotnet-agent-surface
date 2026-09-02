# Skill and reference generation

Design notes for the skill/reference generator (`SkillReferenceGenerator` in
`DotNetAgentSurface.Core`, wrapped by `SkillGeneratorCommand` in
`DotNetAgentSurface.CommandLine`) that renders `SKILL.md` and its supporting
references from the catalog. See the [development hub](../../DEVELOPMENT.md)
for how this fits with the rest of the project.

## Current state (v1)

The initial generator (tracked as milestone "Skill generator" /
"Explicit skill generator command" in [tracking.md](tracking.md), both
Completed) writes three flat files into an output directory:

```text
<output>/
  SKILL.md
  commands.md
  schemas.json
```

This satisfies determinism and stale-file checking, but it does **not**
satisfy the "keep `SKILL.md` small, use references" goal: `RenderSkill`
currently writes every operation's full name and description directly into
`SKILL.md`, so the file grows linearly with the catalog. There is also no
`references/` subfolder (contrary to the structure this document originally
sketched), no YAML frontmatter, no skill name/description independent of the
catalog, and no sharding strategy for large catalogs. The plan below (v2)
closes these gaps.

## Problem statement

A `SKILL.md` is meant to be the *first* thing an agent loads to decide
whether and how to use a skill — analogous to how this session's own
`<available_skills>` block only shows a name and one-line description, with
full instructions loaded on demand. If `SKILL.md` inlines every operation's
full description, it stops being cheap to load and its size becomes
proportional to catalog size instead of constant. For a catalog with
dozens or hundreds of `[AgentOperation]` methods (the expected long-term
case for generated CLIs over real services), that defeats the purpose of
having a lightweight entry point at all.

The fix is not just "add a `references/` folder" — it is a size discipline
that has to hold as the catalog grows:

- `SKILL.md` must stay roughly constant-size regardless of operation count.
- The command reference must itself avoid becoming one unbounded file once
  the catalog is large; it needs a sharding strategy.
- Whatever sharding strategy is used must stay deterministic and must not
  break the existing stale-file `check` mode — in fact, sharding introduces
  a new failure mode (orphaned reference files left behind after a category
  is renamed or removed) that `check` must detect.

## Design goals

- `SKILL.md` stays small: short YAML frontmatter, a concise "when to use"
  paragraph, the CLI executable name, discovery instructions (`--help`),
  2-3 representative examples, and a compact index that links out — never a
  full per-operation listing.
- Detailed content (full parameter tables, defaults, safety classification,
  per-operation examples, schemas) lives under `references/` and is loaded
  by the agent only when needed.
- Large catalogs shard the command reference by category instead of
  producing one ever-growing `commands.md`.
- Generation remains fully deterministic (stable ordering, stable line
  endings, no timestamps) so the existing byte-for-byte `IsCurrent`/`check`
  contract keeps working, and is extended to catch orphaned files once the
  file set becomes dynamic.
- No behavior change to the underlying catalog, invocation pipeline, or CLI
  adapter — this is purely about what the skill generator renders.

### Non-goals

- Generating natural-language skill content with an LLM at build time
  (already an explicit non-goal for the whole project — see
  [core catalog and abstractions](core-catalog.md)). Skill/operation
  descriptions remain whatever the developer wrote in
  `[AgentOperation(...)]`; the generator only rearranges and formats them.
- Runtime/dynamic skill loading. This generator produces static files; how
  an agent host discovers and loads a `skills/<skill-name>/` directory is
  out of scope here.

## Target structure (v2)

```text
skills/
  <skill-name>/
    SKILL.md                    # small, constant-size entry point
    references/
      commands.md                # index only once sharded; full reference when small
      commands/
        <category-slug>.md       # one file per top-level category, once thresholds are exceeded
      schemas.json                # machine-readable input schemas for every operation
```

`<skill-name>` and the output root move from being implicit (current code
just writes into whatever `outputDirectory` the host passes) to an explicit
`SkillGenerationOptions` the host supplies — see
[Generator API changes](#generator-api-changes) below.

### `SKILL.md` contents and size budget

`SKILL.md` must include, in this order:

1. YAML frontmatter with (at minimum) `name` and `description` — matching
   the field names real skill hosts (including this one) already expect,
   so a generated skill directory can be dropped into a skills folder
   as-is.
2. A one-paragraph "when to use this skill" statement, derived from the
   frontmatter description (not duplicated verbatim from every operation).
3. The CLI executable name and the exact command to list all operations
   (e.g. `<exe> --help`), so the agent can always fall back to live
   discovery instead of trusting a possibly-stale doc.
4. A **compact index**: one line per category (category name, operation
   count, one-line purpose) linking to its reference file, or — for
   uncategorized/small catalogs — one line per operation (name + one-line
   description) with no parameters, defaults, or examples inlined.
5. 2-3 representative examples, hand-picked from `OperationDescriptor.Examples`
   for a small, fixed subset of operations (never all of them).
6. A relative link to `references/commands.md`.

Target budget: `SKILL.md` should stay under roughly 150 lines / ~4 KB for
any catalog size. This is a soft budget enforced by a test (see
[Testing strategy](#testing-strategy)), not a hard truncation rule — the
mechanism that keeps it true is that `SKILL.md` only ever holds an index,
never operation detail.

### Reference sharding strategy

`references/commands.md` documents every generated command's parameters,
defaults, safety classification (`SafetyLevel`, `IsIdempotent`), aliases,
and examples — the detail intentionally left out of `SKILL.md`.

- **Small/uncategorized catalogs** (no distinct `Category` values, or total
  operation count at or below a threshold — proposed default: 20
  operations): `references/commands.md` contains the full reference
  directly, as today.
- **Large or categorized catalogs** (more than one distinct `Category`, or
  operation count above the threshold): `references/commands.md` becomes an
  index (category name, operation count, link), and the full per-operation
  detail moves to `references/commands/<category-slug>.md`, one file per
  top-level category (using the same category-segment convention as
  `OperationCommandLineAdapter`/`OperationCatalog.GetCategorySegments`).
  Uncategorized operations in an otherwise-categorized catalog fall into a
  reserved `references/commands/_root.md`.
- The threshold is a constant with a documented rationale, not user
  configuration, to keep output shape predictable — but see
  [Generator API changes](#generator-api-changes) for an escape hatch.

`references/schemas.json` stays a single file for v2: schemas are
machine-readable and only fetched on demand, so a single JSON file is less
harmful to token budgets than an oversized Markdown file an agent might
read in full. Splitting schemas per category is called out as a future
extension point if catalogs grow large enough for this to matter in
practice.

## Generator API changes

`SkillReferenceGenerator.Generate`/`IsCurrent` currently take just
`(OperationCatalog catalog, string outputDirectory)`. Since `SKILL.md`
frontmatter needs a skill name and description that are not part of the
catalog (a catalog has no notion of its own name), both methods gain an
options parameter:

```csharp
public sealed record SkillGenerationOptions(
    string SkillName,
    string SkillDescription,
    string ExecutableName,
    int CategoryShardThreshold = 20);

void Generate(OperationCatalog catalog, string outputDirectory, SkillGenerationOptions options);
bool IsCurrent(OperationCatalog catalog, string outputDirectory, SkillGenerationOptions options);
```

`SkillGeneratorCommand` gains `--name`, `--description`, and
`--executable` options (or reads them from host-supplied defaults, similar
to how `outputDirectoryDefault` already works) so hosts do not need custom
wiring to set them.

## Determinism and stale-file detection

The existing `IsCurrent` check only verifies that three fixed filenames
exist and match expected content byte-for-byte. Sharding introduces a
dynamic file set (one file per category, which can appear, disappear, or be
renamed as the catalog changes), so `check` mode needs a second class of
failure it does not currently detect: **orphaned reference files** — files
under `references/commands/` that no longer correspond to any current
category.

Plan:

- Compute the deterministic set of expected relative file paths for a given
  catalog + options (this is already implicit in the rendering logic; make
  it explicit so both `Generate` and `IsCurrent` share it).
- `IsCurrent`/`check` fails if any expected file is missing or has
  different content (existing behavior), **and** if any file exists under
  `references/commands/` that is not in the expected set (new behavior).
- `Generate` always deletes stale files under `references/commands/` before
  writing the current set, so regenerating never leaves orphans on disk.
- All ordering (categories, operations within a category, parameters within
  an operation) continues to follow the catalog's already-deterministic
  ordering; no new sort logic is introduced, only new grouping.

## Migration / rollout phases

1. **Fix the core defect first, no sharding yet.** Add `SkillGenerationOptions`
   (name/description/executable), stop inlining full operation descriptions
   into `SKILL.md`, add YAML frontmatter, and move output under
   `skill/references/` instead of a flat directory. Update
   `SkillReferenceGeneratorTests` and `SkillGeneratorCommand` accordingly.
   This alone fixes the "SKILL.md becomes huge" problem for typical catalog
   sizes.
2. **Add category-based sharding** once the threshold is exceeded, plus the
   orphaned-file detection described above. Add tests with a synthetic
   catalog large enough to cross the threshold.
3. **Optional/stretch:** a `--clean` flag on `SkillGeneratorCommand` to
   explicitly report/remove stale files outside the normal `generate` flow,
   and revisit whether `schemas.json` needs per-category splitting once a
   real catalog is large enough to make that worthwhile.

## Testing strategy

- Keep the existing determinism test (two generations of the same catalog
  produce byte-identical output) and the existing stale-file detection
  test, extended to cover nested `references/commands/*.md` paths.
- Add a size-budget test: generate a synthetic catalog with enough
  operations/categories to have blown up the old flat `SKILL.md`, and
  assert the generated `SKILL.md` stays under the documented line/byte
  budget while `references/` grows instead.
- Add a threshold-boundary test (catalog exactly at, and one above, the
  sharding threshold).
- Add an orphaned-file test: generate once, rename/remove a category, then
  assert `IsCurrent`/`check` reports the stale file and `Generate` removes
  it.
- Add a frontmatter test asserting `SKILL.md` starts with valid YAML
  frontmatter containing non-empty `name` and `description` fields.

Files should continue to have stable ordering and line endings, and
generation should continue to avoid rewriting unchanged output.
