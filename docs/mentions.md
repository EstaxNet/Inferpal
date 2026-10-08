# @-mentions

Type `@` in the prompt to open a typed context picker. The mention is resolved into real
context **the moment you send** — the trailing `@…` token is removed from the prompt and
replaced with the corresponding attachment.

| Mention | Attaches |
|---|---|
| `@file` | A file you pick |
| `@folder` | A folder: the list of its files, plus the text of the first of them |
| `@code` | A semantic search of the indexed codebase — type the query after the token |
| `@diff` | The current `git diff` |
| `@problems` | The current build errors |
| `@debugger` | The live debugger break state (via `get_debugger_state`) |
| `@clipboard` | The clipboard contents |
| `@tree` | The solution tree |

> [!TIP]
> The `@`-mention popup and the `/`-command popup are mutually exclusive — typing `@` opens
> the mention picker, `/` opens the command list.

> [!NOTE]
> **`@folder` is bounded, and it says where.** It lists up to 200 files, up to 4 levels deep, and
> includes the text of the first 30 of them within a 60 000-character budget. Whenever one of those
> limits bites — or a file cannot be read — the attachment ends with a line saying which one, so a
> file named in the list but absent from the text is never mistaken for a file that is simply empty.
> Attach a subfolder to get the rest.

> [!NOTE]
> **A mention with nothing to attach says why instead of leaving a chip.** `@folder` on a path that does not exist, or
> on a file (use `@file`), `@code` while there is no index to search, and `@diff` outside a git repository or with no
> uncommitted change, answer with a short notice — a chip always means something joined your question. A long diff is
> cut to the size of one attachment, and its chip says how many characters it kept.

> [!NOTE]
> **VS Code:** typed mentions work there too (since 1.2.0) — the eight categories from
> `@file` to `@tree` are offered in a two-level popup, and resolved mentions appear as
> context chips in the composer, alongside a "+" attach menu (active file, selection, file
> from disk, and **pin the active file** — pinned files show above the input box, ✕ unpins one).
> A path typed after `@` (`@src/app.ts`) attaches the file directly: up to 5 files per question, each up to 40,000
> characters (a chip, 60,000). A file sent in part, or left out, is named under your question with the counts, and
> the model is told the same, with `read_file` to read the rest.

## Related

- [Tools → `get_debugger_state`](tools.md#built-in-tools) backs `@debugger`.
- [Slash Commands](slash-commands.md) for the `/` command list.
