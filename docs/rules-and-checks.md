# Rules & AI Checks

Two fully-local, **repo-versioned** governance features modelled on Continue's
`.continue/rules` and `.continue/checks`. Both are managed by slash commands — there is no
Settings section — and their file contents are never translated (they are your content).

## Rules — `.inferpal/rules/*.md`

Each markdown file is a rule with optional YAML frontmatter:

```markdown
---
description: C# naming conventions
globs: **/*.cs
alwaysApply: false
---
- Use PascalCase for public members, camelCase for locals.
- Prefix private fields with an underscore.
```

- **Frontmatter keys**: `description` (→ the rule's name, else the filename), `globs` (CSV of
  glob patterns), `alwaysApply` (bool). Everything after the frontmatter is the rule body.
- **Scoping**: a rule is injected when its glob matches the **active editor file**, or when
  `alwaysApply: true` / no `globs`. Injected rules appear in the system prompt under a
  `## Rules` section.
- **Re-scoping**: when you switch the active file, the system prompt is rebuilt and rules are
  re-evaluated automatically.

Commands: `/rules` lists them; `/rules init` scaffolds an example (never overwrites).

## AI Checks — `.inferpal/checks/*.md`

Each markdown file describes review criteria. `/check` reviews your current **git diff**
against them, **100% locally**:

- The diff is gathered staged → unstaged (plus status), capped to keep the prompt bounded.
- The model reviews it against every check and reports `file:line`, a severity
  (**blocker** / **warning** / **nit**), and a concrete fix — instructed not to invent
  issues.

Commands: `/check` runs all checks; `/check <name>` runs one; `/check init` scaffolds an
example. `/checks` lists the available checks; `/checks init` scaffolds one.

> [!NOTE]
> Nothing leaves your machine — the review runs on your configured local model.

## Related

- [Architecture → System prompt layering](architecture.md#system-prompt-layering) — where
  matching rules are injected.
- [Configuration](configuration.md) — `customSystemPrompt`, pinned files, and other prompt
  inputs.
