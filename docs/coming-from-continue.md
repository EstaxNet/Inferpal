# Coming from Continue or Roo Code

Continue and Roo Code have both stopped: Continue's repository is read-only and its team joined Cursor in June 2026,
and Roo Code shut down on May 15, 2026. If you ran either against a local model, Inferpal can take your configuration
over in one command, in Visual Studio and in VS Code alike.

**Try it**

```text
/import continue          reads ~/.continue/config.yaml (or the older config.json)
/import roo               reads the file Roo Code's settings Export wrote (roo-code-settings.json)
/import roo <file>        any other path (a relative one is read from the folder you have open)
```

Nothing is written at first. A card lists, line by line, what Inferpal can take — each with a box — and, folded
underneath, every other part of the file with the reason it stays. Untick what you do not want, then **Apply**.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/features/import-card-dark.png">
    <img src="assets/features/import-card-light.png" alt="The /import card for C:\Users\you\.continue\config.yaml: 9 items to write, 16 parts left out with the reason, config.json copied aside before anything is written. Each line has a ticked box: chat model qwen2.5-coder:14b, completion model qwen2.5-coder:1.5b, embedding model nomic-embed-text, context window 16 384, a Continue rule for the custom system prompt with its scope written in the text, the commands /review-continue, renamed because /review is taken, and /changelog, the MCP server filesystem, and the @Docs source FastAPI" width="420">
  </picture>
  <br><sub>What will be written, each with its box. Nothing changes until you click Apply.</sub>
</p>

From the agent protocol (Zed, JetBrains IDEs…), where there is no card, type `/import continue apply` (what is
ticked), `apply all`, or `apply 1,3,5`.

> [!NOTE]
> Before anything changes, Inferpal copies its settings file aside
> (`config.json.before-import-<date>.json`, beside it) and names the copy. An import never overwrites one of your own
> commands, MCP servers or documentation sources: a name already taken is said, or the command is renamed.

## After the import

- The commands are in this machine's prompts folder (`%AppData%\Inferpal\prompts` on Windows,
  `~/.config/Inferpal/prompts` on Linux, `~/Library/Application Support/Inferpal/prompts` on macOS): type `/` to see them, `/prompts` to list where each comes from.
- A new model server takes effect at the next question in Visual Studio; VS Code restarts its assistant for it.
- `/permissions` shows the rules in force; `/docs reindex` indexes the imported documentation sites.
- To undo, put the copy of `config.json` back (its path is in the answer), and delete the command files you do not want.

## What carries over

| In Continue / Roo Code | In Inferpal |
|---|---|
| The server of your first local chat model (Ollama, LM Studio, llama.cpp, vLLM, any OpenAI-compatible endpoint) | The model server (provider, address, API key) — the next question uses it in Visual Studio, VS Code restarts its assistant; a key you had set for another server is removed, never sent to the new one |
| Chat, autocomplete, edit, summarize and embed roles | The chat, completion (FIM), inline edit, utility and embedding models |
| The chat model's context length | The context window |
| Rules, a model's system message, Roo's custom instructions | Sections added to the custom system prompt; a rule scoped to files keeps its scope, written in the text the model reads |
| Prompts and custom commands; Roo's additions to its built-in modes | Commands in this machine's prompts folder, usable in every workspace; `{{{ input }}}` becomes what you type after the command |
| Roo's custom modes | [Modes](configuration.md#modes) of your own (`%AppData%\Inferpal\modes\`, one file per mode): their instructions, their tool groups with the files their edits are limited to (`fileRegex`), and their model when a server you run serves it. A mode named like one of yours stays yours |
| MCP servers | MCP servers (and MCP is turned on); one left unnamed is named after the package it runs (`npx -y @scope/server-x` → `server-x`) |
| Documentation sites | @Docs sources — index them with `/docs reindex` |
| Roo's allowed and denied commands | Permission rules for `run_command` — allowed ones are **unticked**: they let a command run without asking, so the choice is yours. Denied ones go first, so no allow rule can override them |
| Roo's interface language, when Inferpal is translated into it | The interface language |

## What stays, and why

- **A hosted provider** (Anthropic, OpenAI's own API, OpenRouter, Gemini, Mistral…): Inferpal only talks to a model
  server you run. This is a choice of the product, not a missing feature. A company gateway that speaks the OpenAI API
  is a server you run, and is imported.
- **A second server.** Inferpal uses one model server; models served elsewhere are named.
- **Sampling** (temperature, top-p…) and `maxTokens`: Inferpal applies what the model's publisher recommends and never
  cuts an answer at a fixed length.
- **Context providers** (Continue's code, diff, terminal… providers): Inferpal's @-mentions are built in.
- **Roo's automatic approval** (`alwaysAllowWrite`…): an import never turns off Inferpal's approval prompt. Roo's
  allowed commands are offered as rules only when Roo's automatic approval of commands was on — otherwise the list did
  nothing, and importing it would grant more than Roo did.
- **A Roo mode's `browser` group, and `whenToUse`** (Roo's automatic choice of mode): no equivalent, named on the card.
  A mode's model on a hosted provider is not written either — the mode answers with the chat's model.
- **Hub blocks** (`uses: owner/slug`): Continue's Hub is closed, they cannot be read.
- **A secret** written `${{ secrets.NAME }}` is looked for in `~/.continue/.env` and in the environment; one that is not
  found is named, to type in the settings.

Every part left out is listed under the card with its reason — nothing is dropped without a word.

**Next:** [Features](features.md) — what Inferpal does, with something to try in each section.
