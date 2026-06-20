# @-mentions

Type `@` in the prompt to open a typed context picker. The mention is resolved into real
context **the moment you send** — the trailing `@…` token is removed from the prompt and
replaced with the corresponding attachment.

| Mention | Attaches |
|---|---|
| `@file` | A file you pick |
| `@folder` | A folder (its tree) |
| `@code` | The active editor selection |
| `@diff` | The current `git diff` |
| `@problems` | The current build errors |
| `@debugger` | The live Visual Studio break state (via `get_debugger_state`) |
| `@clipboard` | The clipboard contents |
| `@tree` | The solution tree |
| `@token` | A token counter |

> [!TIP]
> The `@`-mention popup and the `/`-command popup are mutually exclusive — typing `@` opens
> the mention picker, `/` opens the command list.

## Related

- [Tools → `get_debugger_state`](tools.md#built-in-tools) backs `@debugger`.
- [Slash Commands](slash-commands.md) for the `/` command list.
