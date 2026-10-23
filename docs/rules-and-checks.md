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

- **Frontmatter keys**: `description` (→ the rule's name, else the filename), `globs` (glob
  patterns, written as a comma-separated list, an inline array `["**/*.cs", "**/*.ts"]` or a YAML
  block list of `- pattern` lines), `alwaysApply` (bool). Everything after the frontmatter is the
  rule body.
- **Scoping**: a rule is injected when its glob matches the **active editor file**, or when
  `alwaysApply: true` / no `globs`. Rules that always apply are in the system prompt under a
  `## Rules` section; rules a glob selects ride with the question, in the `## Active file` block sent when the active
  file changes — so opening another file never changes the start of the request, which a server's prompt cache keeps.
- **Glob dialect** (the same one `indexExclude` uses): `*` matches within one path segment, `?` one
  character in a segment, `**/` any number of whole segments — including none, so `**/Program.cs`
  matches `Program.cs` and `src/Program.cs` but **not** `src/MyProgram.cs` — and a trailing `**`
  matches everything below; `{md,mdx}` matches one of the alternatives. A pattern with no `/` is
  matched against the file name at any depth.
- **Re-scoping**: the system prompt is rebuilt before every question, so rules are re-evaluated
  against the file active at that moment, and an edited rule applies from the next question.

Commands: `/rules` lists them; `/rules init` scaffolds an example (never overwrites).

## AI Checks — `.inferpal/checks/*.md`

Each markdown file describes review criteria. `/check` reviews your current **git diff** for
any bug, regression or behaviour change it introduces, **and** against your checks, **100% locally**.
With all your checks, they add to the review and never narrow it: a defect none of them names is still
reported. With no check in the repository, `/check` makes the general review and says so.

- The diff is gathered staged → unstaged (plus status), capped to keep the prompt bounded. With nothing staged,
  files git does not track yet are reviewed too, as the diffs they will be once added; a new file that is not
  read (past the size limit, binary or unreadable) is named above the findings.
- The model reviews the whole change, applies every check on top, and reports `file:line`, a severity
  (**blocker** / **warning** / **nit**), and a concrete fix — instructed not to invent
  issues. It also says, in one sentence, which checks pass. Its sentences come in your interface language.
- **Every location is then checked against the diff itself**, and the answer says which of
  three it is: the line is inside a changed hunk; the line was off and got snapped to the
  nearest changed line (the reported one is shown); or the file is not in the diff at all,
  in which case the finding is kept but explicitly marked as an unverified location. A model
  asked for `file:line` always produces one — only the diff can say whether it points at
  anything, and a wrong anchor is worse than no anchor.
- Findings are grouped by file, blockers first, so the reading order is the fixing order.

Commands: `/check` reviews the change with all checks; `/check <name>` reviews it against that one
check **only** (the narrow review you ask for); `/check init` scaffolds an example.

**A file or a folder.** `/check <path>` reviews the uncommitted changes under that file or folder of the repository
(new files included) — what **Review Changes** sends from Visual Studio's Solution Explorer (where a project is its
folder) and VS Code's file explorer. The answer names the path above the findings, and says when what it reviewed is
the staged part; nothing changed under it is said. A check's name, then a revision, keep their meaning when the same
word is also a path, and the answer then names the spelling that reviews the path (`/check ./docs`).

**A branch, a range, a commit.** `/check main..HEAD` reviews a branch before its pull request: the commits
`HEAD` has and `main` has not, diffed from where the two parted — what a pull request shows, never the changes
`main` received since (`A...B` is the same). `/check feature` reviews what `feature` brings to the branch you are
on; `/check 1a2b3c4` (or a tag, `HEAD~2`) one commit, against its first parent (a merge: what it brought to its
branch). The answer starts by naming what was reviewed, and a branch that brings nothing says so. A check's name
wins over a branch of the same name; the answer then gives the spelling that reviews the branch (`refs/heads/…`).
A name a branch and a tag share, or a hash prefix several objects share, is refused naming them rather than
guessed; so is a commit at the edge of a shallow clone, whose parent was not fetched. **Review a Commit…** picks a
recent commit — searchable by its message — or takes a branch, a tag or a range typed in the box: in VS Code,
**Inferpal: Review a Commit…** (Command Palette); in Visual Studio, *Tools › Inferpal › Review a Commit…*, whose list
holds the 50 most recent commits and searches the last 500.

**Without any check**, `/check` makes the general review — any bug, regression or behaviour change — and the answer
says it was made without checks; `/checks init` writes one when you want your own criteria on top.

`/checks` lists the available checks; `/checks init` scaffolds one. Available in
**both front-ends** (VS and VS Code).

> [!NOTE]
> Nothing leaves your machine — the review runs on your configured local model.

## Related

- [Architecture → System prompt layering](architecture.md#system-prompt-layering) — where
  matching rules are injected.
- [Configuration](configuration.md) — `customSystemPrompt`, pinned files, and other prompt
  inputs.
