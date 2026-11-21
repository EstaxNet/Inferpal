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
| Windows event log | `.evtx` | Each event as a log entry — its time in your machine's zone, its level, its source and id, then its data as written (a .NET crash: the exception and its stack). Filtered by level and time like any log. A log too long keeps its newest events and says how many older ones are left out. Read on Windows, Linux and macOS |
| MSBuild binary log | `.binlog` | Replayed by the .NET SDK on your machine: its errors first, its warnings, the projects it built, then the slowest projects, targets and tasks. Without the .NET SDK, it says so |
| API specification | OpenAPI or Swagger (`.yaml`, `.yml`, `.json`) | Read by the agent first as its operations — method, path, parameters, body and answers — then its schemas, so it calls the API right; asked for lines, its text |
| Static analysis report | SARIF (`.sarif`, `.sarif.json`, or a `.json` that declares it) — CodeQL, the Roslyn analyzers and `dotnet build -p:ErrorLog`, ESLint, Semgrep | Read by the agent first as what it found: the tool, then the results by severity, errors first, each at its file, line and column with its rule and message, then the rules met with their description and link; a result suppressed in the code or by a baseline is counted, never listed, so it is not "fixed" against the team's choice; asked for lines, its JSON |
| Terraform plan | The JSON of a plan (`terraform show -json plan.out > plan.json`), Terraform or OpenTofu | Read by the agent first as what it will do: destroyed, then replaced, created, changed in place and read, each resource with its reason and the attributes that change — those forcing replacement marked, values known only after apply said so — then the outputs, and the resources changed outside Terraform; every value the plan marks sensitive is written *(sensitive)*, though the JSON holds it in clear; a state file stays its JSON; asked for lines, its JSON |
| Crash report | Apple `.ips` and `.crash` (macOS, iOS), Windows Error Reporting `.wer`, Android `tombstone_NN` and ANR `traces.txt` / `anr_*` | Read by the agent first as what crashed: the exception and its code named, what the program or its runtime said, the faulting thread's stack as written (Apple's last exception backtrace first; for an ANR the main thread, then each thread waiting on a lock and who holds it), then the threads and modules counted; a Windows report gives its signature as written, the faulting module's path, and says it holds no stack (a dump beside it is not read); recognised by its name, so a log that quotes a crash stays a log; asked for lines, its text |
| Query plan | SQL Server `.sqlplan` (estimated or actual), PostgreSQL `EXPLAIN (FORMAT JSON)` with or without `ANALYZE`, MySQL and MariaDB `EXPLAIN FORMAT=JSON` (and MariaDB's `ANALYZE FORMAT=JSON`) saved as `.json` | Read by the agent first as where its time goes: each operator by its own share (its total less its children's), by measured time when the plan holds it, else by the planner's cost; then full scans of large tables, estimates ten times off what was read, SQL Server's warnings and the indexes it suggests; then the plan as a tree; the same reading for a plan `query_database` returns; asked for lines, its text |
| Network capture | `.har` | Read by the agent first as its requests, the failed ones first — method, address, status, time and the start of each body; headers and cookies are not part of it and what looks like a credential is masked as far as recognised — a summary, not a protection: the file holds everything as captured; asked for lines, its JSON |
| Diagram | `.drawio`, `.vsdx` | Page by page, its shapes (their text) and its links (`A → B: label`); a compressed draw.io page too. Positions, colours and styles are left out |

A notebook, an RTF file and an `.eml` are text files: attached to a question they give their text as above, while the
agent's `read_file` reads them as they are written, so it can edit them.

A **test report** — VSTest's `.trx`, or JUnit XML from pytest, Maven, Gradle, jest-junit or go-junit-report — reads first
as its result when the agent opens it: the counts, then each failed test with its message and its stack, without running
the suite again. It stays a text file: `start_line` reads its XML. A **draw.io** diagram is read the same way — its
shapes and links first, its XML with `start_line`, so the agent can still edit it. In an archive (a CI artifact), a test
report or a draw.io diagram reads as it does on disk; a binary document inside an archive is read once extracted.

A document protected by a **password** (PDF, Office, OpenDocument) asks you for it on a card in Visual Studio and
VS Code; the model gets the text, never the password. At most 20 MB per document, 64 MB for an event log.

## Images

| Format | Extensions | What happens |
|---|---|---|
| PNG, JPEG, GIF | `.png`, `.jpg`, `.jpeg`, `.gif` | Sent with your question to a model that sees images, reduced to 1920 × 1080 when larger |
| WebP | `.webp` | Sent as it is, whatever its size: the server reduces a large one, and the chat says so |
| BMP | `.bmp` | Converted to PNG on your machine, then sent the same way |
| TIFF | `.tif`, `.tiff` | Drawn on your machine and sent as a PNG (uncompressed, LZW, Deflate or PackBits; gray, colour, palette, 1 to 16 bits). A file of several pages sends its first and says so; a TIFF compressed in JPEG or as a fax is not attached, and the chat says why |
| Video | `.mp4`, `.mov`, `.m4v`, `.webm`, `.mkv`, `.avi`, `.wmv`, and a GIF that moves | A screen recording of a bug, up to 100 MB: eight frames at even times on one picture, each with its time written above it, so the model can say *when* ("at 0:30"). Decoded by your system: on Windows its own media stack (an `.mp4` as recorded by Windows; a `.webm` needs the free *VP9 Video Extensions*, HEVC the *HEVC Video Extensions*), on macOS and Linux ffmpeg (`brew install ffmpeg`, `sudo apt install ffmpeg`); a GIF by Inferpal itself. Without a decoder, the chat names what to install |
| HEIC | `.heic`, `.heif` | An iPhone photo, converted by your system's own decoder and sent as a JPEG: Windows needs the free *HEIF Image Extensions* and *HEVC Video Extensions* from the Microsoft Store, macOS has it built in, Linux needs libheif (`libheif-examples` or `libheif-tools`). Without one, the chat names what to install |

A model that does **not** see images gets the **text written in the image** instead, read by Inferpal's OCR, in its
ten languages. The agent's own `ocr` tool reads `.png`, `.jpg`, `.jpeg`, `.bmp`, `.gif`, `.tif` and `.heic` (not `.webp`). At
most 4 images per question, 10 MB each.

An **SVG** attached goes **drawn** to a model that sees images — by the browser `view_page` uses (Edge, Chrome, Firefox, or
macOS' WebKit), with no network, its scripts not run — and as its **markup** to one that does not, or when no browser is
there to draw it; its chip says which. The agent's `read_file` reads an SVG as the text it is.

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
