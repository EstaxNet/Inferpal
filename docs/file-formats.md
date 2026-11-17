# File formats

Every file type Inferpal reads, and what it does with each. Everything listed here is read **on your machine**: nothing
is uploaded, nothing is installed for it. A file it cannot read says why instead of sending noise.

Any **text file** — source code in any language, configuration, Markdown, JSON, YAML, XML, SQL, a log — is read as it
is, in its own encoding (UTF-8, UTF-16, or the legacy code page it was written in). The sections below are the formats
that need more than that.

## Documents

Attached to a question (`@file`, Browse, a drop on the chat) or opened by the agent (`read_file`, `search_in_files`),
these give the model their **text**.

| Format | Extensions | What the model gets |
|---|---|---|
| PDF | `.pdf` | Its text, page by page. A **scanned** PDF (images, no text) is read by OCR, up to 20 pages, tables rebuilt row by row |
| Word | `.docx`, `.doc` | The text, headings and tables |
| Excel | `.xlsx`, `.xlsm`, `.xlsb`, `.xls` | Each sheet under its name, a row per line |
| PowerPoint | `.pptx` | Each slide in order, its speaker notes after it |
| OpenDocument | `.odt`, `.ods`, `.odp` | As their Office counterparts |
| Rich text | `.rtf` | Its text |
| Jupyter notebook | `.ipynb` | Attached: its cells and their outputs, not its JSON. The agent also edits one cell at a time and runs the notebook ([Tools](tools.md#logs-data-files-and-notebooks)) |
| E-mail | `.eml`, `.msg` | The headers and the body, its attachments named |

A notebook, an RTF file and an `.eml` are text files: attached to a question they give their text as above, while the
agent's `read_file` reads them as they are written, so it can edit them.

A document protected by a **password** (PDF, Office, OpenDocument) asks you for it on a card in Visual Studio and
VS Code; the model gets the text, never the password. At most 20 MB per document.

## Images

| Format | Extensions | What happens |
|---|---|---|
| PNG, JPEG, GIF | `.png`, `.jpg`, `.jpeg`, `.gif` | Sent with your question to a model that sees images, reduced to 1920 × 1080 when larger |
| WebP | `.webp` | Sent as it is, whatever its size: the server reduces a large one, and the chat says so |
| BMP | `.bmp` | Converted to PNG on your machine, then sent the same way |
| HEIC, TIFF | `.heic`, `.heif`, `.tif`, `.tiff` | Offered by the attach pickers, then not attached: the chat asks you to save the image as PNG or JPEG |

A model that does **not** see images gets the **text written in the image** instead, read by Inferpal's OCR, in its
ten languages. The agent's own `ocr` tool reads `.png`, `.jpg`, `.jpeg`, `.bmp` and `.gif` (not `.webp`). At most 4 images per
question, 10 MB each. An `.svg` is text: it is attached as its markup.

## Archives

The agent lists an archive and reads an entry **in place**, without extracting anything: `logs.zip!/app/log.txt`.

| Kind | Extensions |
|---|---|
| Zip and the formats built on it | `.zip`, `.nupkg`, `.snupkg`, `.vsix`, `.jar`, `.war`, `.apk`, `.whl` |
| 7z and RAR | `.7z`, `.rar` (RAR4 and RAR5) |
| Tar | `.tar`, `.tgz`, `.tar.gz`, `.tbz2`, `.tar.bz2`, `.txz`, `.tar.xz` |
| One compressed file | `.gz`, `.bz2`, `.xz` (seen as an archive of one entry: `app.log.gz!/app.log`) |

A password-protected zip (ZipCrypto or AES), 7z or RAR asks you for its password on a card; when even the names of a
7z or a RAR are protected, it is listed once you have typed it. An archive inside an archive is named, not opened, and
an archive split into volumes (`.part1.rar`, `.7z.001`) is not read. At most 8 MB read out per entry.

## Data files

`query_data` answers one read-only SQL query (SQLite dialect) on:

| Kind | Extensions |
|---|---|
| SQLite database | `.db`, `.sqlite`, `.sqlite3` |
| Delimited text, loaded as the table `data` | `.csv` (comma or semicolon), `.tsv` |
| JSON, loaded as the table `data` (columns from the keys) | `.json` (an array of objects), `.jsonl`, `.ndjson` (one object per line) |
| Excel workbook, one table per sheet named after it | `.xlsx`, `.xlsm`, `.xlsb`, `.xls` |

In a sheet, a title above the header is left out, a merged cell gives its value to its whole range, and a date reads
as `yyyy-MM-dd` text, which SQLite's date functions take. A nested JSON object stays JSON text, read with `json_extract`.
A password-protected workbook and an OpenDocument sheet (`.ods`) are read with `read_file`, not loaded for SQL.

## Logs

`read_file` filters a log by level and time (`level`, `since`, `until`), each entry kept with the lines under it (a
stack trace). The formats recognised: **Serilog**, **NLog**, **log4net**, Go's `log`, the .NET **`ILogger` console**,
ISO 8601 times and **syslog** times. Any other log is still read as text.

## Web pages and the app you build

| What | Formats |
|---|---|
| A page the agent looks at (`view_page`) | `.html`, `.htm`, `.xhtml`, `.svg` in the workspace, or a page your app serves on this machine |
| A page the agent fetches (`fetch_url`) | HTML (converted to text), plain text, JSON, XML; a binary download is named, not read |

## Code

| What | Languages and files |
|---|---|
| Semantic search (the index) | C#, F#, TypeScript and `.tsx`, JavaScript and `.jsx`, Python, Go, Java, C and C++ (`.c`, `.h`, `.cpp`, `.hpp`), Rust, Razor, Vue |
| Exact C# analysis (callers, impact, rename) | C# through the Roslyn compiler; renames also follow the symbol into XAML (`.xaml`, `.axaml`), Razor and `.cshtml` |
| Syntax checked before an edit is written | C#, Razor (`.razor`, `.cshtml`), Python, JavaScript modules (`.mjs`, `.cjs`) |
| Build check after each edit (Smart Fix) | .NET, TypeScript, Rust, Go; others through `.inferpal/validators.json` |
| Tests (`run_tests`, `/tdd`) | `dotnet test`, pytest, npm (Jest, Vitest, Mocha…), `cargo test`, `go test` |
| Solutions and projects | `.sln`, `.slnx`, `.csproj` |
| Ghost-text completions | Every language the editor opens |

**Next:** [Mentions](mentions.md) — how to attach each of these to a question.
