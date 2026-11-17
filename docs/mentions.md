# @-mentions

Point the model at exactly what your question is about: a file, your changes, the current errors, a web page. Type `@`,
pick what to attach, and ask. This page shows each mention by what you want to attach, and what it says when there is
nothing to attach.

Type `@` in the prompt to open a typed context picker. The mention is resolved into real
context **the moment you pick it** — the token leaves the prompt and a chip holds what was read
then. A path typed after `@` in VS Code is read when you send.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/features/auto-chips-dark.png">
    <img src="assets/features/auto-chips-light.png" alt="The message box while typing 'Why does the cart total ignore the discounts': three dashed chips, CartController.cs, CartService.cs 38–61 and DiscountRules.cs 12–30, found in your code while you type and sent as excerpts" width="420">
  </picture>
  <br><sub>A chip is what goes with your question. These dashed ones were found while typing; a mention adds one you chose.</sub>
</p>

**Try it**

| Type | What happens |
|---|---|
| `@diff what could break in what I changed?` | The current `git diff` goes with the question |
| `@problems fix these` | The current errors go with it |
| `@file`, then pick a file | A chip holds the file, and it goes with your question |

> [!TIP]
> The `@`-mention popup and the `/`-command popup are mutually exclusive — typing `@` opens
> the mention picker, `/` opens the command list.

## Attach your code

| Mention | Attaches |
|---|---|
| `@file` | A file you pick |
| `@folder` | A folder: the list of its files, plus the text of the first of them |
| `@code` | A semantic search of the indexed codebase — type the query after the token |
| `@tree` | The solution's C# tree |

**`@folder` is bounded, and it says where.** It lists up to 200 files, up to 4 levels deep, and
includes the text of the first 30 of them within a 60 000-character budget. Whenever one of those
limits bites — or a file cannot be read — the attachment ends with a line saying which one, so a
file named in the list but absent from the text is never mistaken for a file that is simply empty.
Attach a subfolder to get the rest.

**A document is attached as its text**: a PDF; a Word `.docx` or `.doc`; an Excel workbook (`.xlsx`, `.xls`, `.xlsb`: each sheet under its name, a row per line, cells separated by tabs); a PowerPoint `.pptx` (each slide in order, then its notes); an OpenDocument `.odt`, `.ods` or `.odp` (LibreOffice); an RTF; a Jupyter notebook (its cells and their outputs, never its JSON or its images); an e-mail `.eml` or `.msg` (subject, sender, recipients, date, body, and the names of its attachments, which are not read). Inferpal reads it on your machine — nothing is sent
anywhere to convert it — with `@file`, a chip, Browse, a file pinned to every request, or an ACP attachment, and tells the
model the text was extracted. Like any attachment it is cut past the size of one, and its chip says so. What has no text
to read is said instead of attached: a scanned PDF or one made only of images, a file larger than 20 MB, or one that
takes more than 2 seconds to read. **A document protected by a password** (PDF, Word `.docx` and `.doc`, Excel `.xlsx` and `.xls`, PowerPoint, OpenDocument) asks you for it on a card in the chat (Open,
or Don't attach): the model gets the text, never the password, which is kept in memory for that file until the editor
closes — never saved, never sent. A Word 95 document protected by a password stays closed, and an ACP attachment of a
protected file is said protected. Pages without text in a PDF that has some are
counted for the model. Formatting, images and comments are left out. Other binary files (archives, executables) are not
attached, and say so.

**An image goes with the question as an image**, never as text: paste a screenshot in the prompt (Ctrl+V), drop one on
the chat, attach it with `@file`, Browse or Attach to Chat — or, from an ACP client, its image blocks. PNG, JPEG, WebP and
GIF are sent as they are; a BMP is converted to PNG; HEIC and TIFF are refused, with the advice to save the image as
a PNG; an SVG is text, attached as its markup. A large photo is turned upright by its EXIF orientation before it is scaled down. At most 4 images per question, 10 MB each. An image larger than 1920 × 1080 is scaled down to fit on your machine
(a model reads no more from a larger one), and said — small text may become hard to read, so crop the part that matters;
one that declares more than 40 million pixels is refused before anything decodes it. The chip shows the image's size; the
line under the chips names the model it goes to. **When the model that will answer does not see images** — as your
server publishes it: LM Studio's `vision`, Ollama's `vision` capability — the text written in the image goes instead, read
on your computer by Inferpal's OCR and said as such on the chip and under the question; an image with no text (a photo)
gets a card before sending: ask a model of your server that does see images, for this question only, or send without
the image. A server that does not say (another
OpenAI-compatible server) gets the image. An ACP client has no card: the person is told, and the model reads that the
images were left out. An image is sent with its question only: a later question does not send it again, and a saved
conversation names it under the question without keeping it.

**Visual Studio: drag a file or a folder onto the chat** — from Solution Explorer or Windows Explorer — and it becomes
the chip `@file` or `@folder` makes, with the same refusals; nothing is sent until you send your message. A project
node is not attached (it carries no file to attach): a notice says so — drop its files, or attach its folder with
`@folder`.

**VS Code: drop files from your file manager with Shift held.** VS Code hands a drop from outside it to the chat only
while Shift is held, and gives the file's bytes and name, not its path: Inferpal reads a copy — a document as its text,
an image as an image, a text file as text, each with the refusals of any attachment (20 MB at most). A folder dropped this
way cannot be attached, and a notice says so. VS Code hands no extension view a drag from its own Explorer or editor
tabs: right-click the file or folder there, then **Attach to Chat**. The **+** menu reminds both.

## Attach your changes

| Mention | Attaches |
|---|---|
| `@diff` | The current `git diff` |
| `@commit` | One commit's message and diff — pick one of the latest, or type a hash, a branch or a tag (alone, the last commit) |

A long diff is cut to the size of one attachment, and its chip says how many characters it kept. `@commit` leaves
out the files your ignore files exclude, and says how many.

## Attach what went wrong

| Mention | Attaches |
|---|---|
| `@problems` | The current errors: a build's diagnostics in Visual Studio, the Problems panel in VS Code |
| `@debugger` | The live debugger break state (via `get_debugger_state`) |
| `@terminal` | *VS Code:* the last command a terminal finished — the command, its folder, its output (its end, when it is long) and its exit code |
| `@output` | *Visual Studio:* the Output window's pane — the one shown, else Build — its end when it is long |

**`@terminal` reads what VS Code's shell integration reports** (on by default for bash, zsh, fish and PowerShell):
the active terminal's last finished command — another terminal's is never taken in its place.

- Inferpal follows a terminal's commands from the first time `@terminal` is used in it, so that first time it asks
  you to run the command again: while a command runs, VS Code keeps its whole output in memory for whoever reads it,
  and following every terminal would cost that to people who never use `@terminal`.
- A command still running is not attached: use `@terminal` again once it ends.
- Visual Studio offers `@output` instead: the text of the Output window's pane you are looking at (else the
  Build pane), its end when it is long; a pane Visual Studio does not give the text of (one not yet shown) is
  said, never attached empty.

## Attach something from outside your code

| Mention | Attaches |
|---|---|
| `@clipboard` | The clipboard contents |
| `@url` | A web page's text — type or paste the address after the token |

**`@url` fetches the address you typed, without asking.** It goes the way `fetch_url` goes — a private or local address
is refused, and so is a redirect to one; `fetch_url` turned off or a deny rule that matches the address refuses it too —
but there is no approval card: you typed the address yourself.

## When nothing is attached

**A mention with nothing to attach says why instead of leaving a chip.** These
answer with a short notice — a chip always means something joined your question:

- `@folder` on a path that does not exist, or on a file (use `@file`)
- `@code` while there is no index to search or when its search finds nothing
- `@tree` with no solution or project folder to map
- `@diff` outside a git repository or with no uncommitted change
- `@commit` on a name no commit has or on a range (`a..b`: `/check` reviews a range)
- `@url` on a page that cannot be fetched, is binary or has no text

**A file your repository keeps away from AI tools is never attached.** `@file`, a chip, a path typed after `@` or an ACP
attachment of a file that `.aiignore`, `.cursorignore` or another ignore file excludes answers with a notice naming the
ignore file, the line and the pattern; `@folder` leaves such files out of its list and says how many
(see [Features → Safety](features.md#safety)).

## Mentions in VS Code

**VS Code:** typed mentions work there too (since 1.2.0) — the eleven categories from
`@file` to `@terminal` are offered in a two-level popup, and resolved mentions appear as
context chips in the composer, alongside a "+" attach menu (active file, selection, file
from disk, and **pin the active file** — pinned files show above the input box, ✕ unpins one).

A path typed after `@` (`@src/app.ts`) attaches the file directly: up to 5 files per question, each up to 40,000
characters (a chip, 60,000). A file sent in part, or left out, is named under your question with the counts, and
the model is told the same, with `read_file` to read the rest.

## Related

- [Tools → `get_debugger_state`](tools.md#built-in-tools) backs `@debugger`.
- [Slash Commands](slash-commands.md) for the `/` command list.

**Next:** [Search & Indexing](search-and-indexing.md) — how `@code` and the chips find the right code.
