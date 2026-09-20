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
> **VS Code:** typed mentions work there too (since 1.2.0) — the eight categories from
> `@file` to `@tree` are offered in a two-level popup, and resolved mentions appear as
> context chips in the composer, alongside a "+" attach menu (active file, selection, file
> from disk, and **pin the active file** — pinned files show above the input box, ✕ unpins one).

## Related

- [Tools → `get_debugger_state`](tools.md#built-in-tools) backs `@debugger`.
- [Slash Commands](slash-commands.md) for the `/` command list.
