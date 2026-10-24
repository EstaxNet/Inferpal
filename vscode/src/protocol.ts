// Wire DTOs of the editor ⇄ Inferpal.Host JSON-RPC protocol.
// Mirror of Inferpal.Host/HostProtocol.cs — camelCase on the wire both ways.

export interface InitializeParams {
  rootDir: string;
  locale?: string;
  clientName?: string;
  /**
   * This adapter serves the reverse `debug/*` requests. Declared rather than assumed: without it
   * the host would register two debugger tools whose every call answers "method not found", and
   * the model would pay for them on every turn.
   */
  debug?: boolean;
  /**
   * The workspace's other folders (multi-root). The tools serve `rootDir` only: the folders it does not hold are
   * stated to the model, which otherwise looks for their code under the root and concludes it does not exist.
   */
  otherFolders?: string[];
  /**
   * This adapter serves the reverse `env/openExternal` request: a sign-in page opens in the browser of the machine the
   * person sits at (VS Code Remote included), its redirect forwarded to the host.
   */
  openExternal?: boolean;
  /**
   * This adapter draws the card an MCP server's form is filled on (`input/form`) and the line it leaves
   * (`chat/inputReceipt`): the host's calls then declare forms to the servers.
   */
  inputCards?: boolean;
}

export interface InitializeResult {
  hostVersion: string;
  provider: string;
  defaultModel: string;
  modelManagement: boolean;
  vramMonitoring: boolean;
  fim: boolean;
  keepAlive: boolean;
}

/** `fim/settings` — Inferpal's inline-completion switch and the debounce of its mode. */
export interface FimSettingsResult {
  enabled: boolean;
  debounceMs: number;
  /** The completion server is on the chat's machine: it steps aside while a chat answers. Absent = it does. */
  yieldsToChat?: boolean;
  /** Only the key asks (`inlineCompletionTrigger`): a pause in typing asks for nothing. Absent = it does not. */
  onDemand?: boolean;
}

/** `fim/ask` — a completion asked for by its key: the suggestion, or the sentence the status bar shows instead. */
export interface FimAskResult {
  text: string | null;
  notice: string | null;
}

/** One diagnostic a quick fix was asked on (`ProblemEntry` in the Core). */
export interface ProblemEntry {
  /** Absolute; null when the diagnostic names no file. */
  file: string | null;
  /** 1-based; 0 when unknown. */
  line: number;
  column: number;
  severity: 'error' | 'warning' | 'info';
  code: string | null;
  text: string;
  source?: string | null;
}

/** `problems/fix`, `debug/analyze` — the question a gesture sends, the attachment's label, and the attachment (the
 *  problems or the debugger's stop, and the code around them). */
export interface GestureQuestion {
  question: string;
  chip: string;
  content: string;
  /** Why nothing is sent — no problem in a file the agent can edit, nothing paused; absent otherwise. */
  refusal?: string | null;
}

/** `git/proposeCommitMessage`: the message for the commit box, or why there is none. */
export interface CommitMessageProposal {
  /** Why nothing is proposed — nothing to commit, git refused, the model failed. */
  refusal?: string | null;
  /** Said beside the proposal: nothing staged, untracked files not described, the diff or the reply cut. */
  notice?: string | null;
  message?: string | null;
}

export interface ChatSendParams {
  prompt: string;
  model?: string;
  agentMode?: boolean;
  /** Workspace-relative paths of files inlined as attachments — the host's RAG auto-context
   * skips their chunks instead of injecting them a second time (parity with VS). */
  attachedPaths?: string[];
  /** A read-only code action (/explain, /review): the host answers with the code-actions model and no tools,
   * whatever the agent-mode switch says — as the Visual Studio window does. */
  codeAction?: boolean;
  /** The question as the user wrote it, before the attached files were appended: what the per-turn auto-context
   * searches for (Visual Studio searches the typed text). */
  query?: string;
  /** Regenerate: the notes the question's first answer read — asked again, it carries them, as the history kept it. */
  notes?: string[];
}

/** A note typed while a turn runs (`chat/steer`), and — read — after how many tool calls the model read it. */
export interface TurnNote {
  id: string;
  text: string;
  afterCalls?: number;
}

/** The answer to `chat/steer`: `reason` says why a note was not taken — `noTurn`, `noBoundary` (no step to read it
 *  before), `closed` (the run is ending). Then the note is sent as the next message. */
export interface ChatSteerResult {
  accepted: boolean;
  reason?: string | null;
}

export interface ChatSendResult {
  /** The turn was long and the setting, read in the file now, says to announce its end (`ChatTurnPolicy.AnnouncesEnd`). */
  announceEnd?: boolean;
  text: string;
  cancelled: boolean;
  tokensUsed: number;
  promptTokens: number;
  error?: string | null;
  /** Why the run stopped, when it is not because the model was done. The answer stays in `text`;
   *  this is added after it, never in its place. */
  endNotice?: string | null;
  /** The end notice offers Continue: the turn stopped at the iteration limit or its answer at the length limit — never
   *  after a loop or a repetition (ChatTurnPolicy.OffersContinue, the reader of both editors). */
  offersContinue?: boolean;
  /** The window this turn was measured against — the loaded one when the server reports a smaller one
   *  than configured. The context gauge shows this, not the setting. 0 = unknown. */
  contextWindow?: number;
  /** What the next question will send (conversation + tool definitions) — the gauge's fill, the X-Ray's
   *  figure. Not `promptTokens`, which measured this turn's last request, the run's own transcript included. */
  nextTurnTokens?: number;
  /** The turn's run — its steps, the files it changed, its last check; absent when no tool ran. */
  run?: RunSummary | null;
  /** The run of changes the turn made, whatever way it ended: saved with its last message. */
  runId?: string | null;
  /** How long the turn took ("21 s"), localized by the host. */
  duration?: string | null;
  /** The model that answered. */
  model?: string | null;
  /** False when the turn stopped before its question entered the host's history (a Stop during the context build). */
  questionKept?: boolean;
  /** The notes typed during the turn that the model read — now part of the question in the host's history. */
  appliedNotes?: TurnNote[] | null;
  /** The notes no step read (typed during the final answer, or the turn stopped first): sent as the next message after
   *  a turn that ended normally, given back to the composer otherwise. */
  unappliedNotes?: TurnNote[] | null;
}

export interface ToolNotice {
  name: string;
  input: string;
  output: string;
  hasErrors: boolean;
  /** The change a writing call made, in the approval card's lines (`+ - …` prefixes); absent when it wrote nothing. */
  diff?: string | null;
}

export interface PlanNotice {
  goal: string;
  steps: string[];
}

export interface StepUpdateNotice {
  index: number;
  status: string;
}

export interface TextNote {
  text: string;
}

/** `chat/thinking` — the throttled reasoning tail, or no text at all when the host only means
 *  "the model is thinking" (plain chat). */
export interface ThinkingNote {
  text?: string | null;
}

export interface DocumentParams {
  path: string;
  text?: string;
  /** Whether the buffer has unsaved changes: only then does the host prefer it over the disk. */
  dirty?: boolean;
}

export interface ActiveDocumentDto {
  path: string | null;
  text: string | null;
}

export interface EditResultDto {
  path: string | null;
  replacedSelection: boolean;
}

export interface IndexStatusResult {
  isIndexing: boolean;
  chunkCount: number;
  rootDir: string;
}

/** `backend/status` answer — connection badge + compact VRAM line ("model · X.X GB"). */
export interface BackendStatusResult {
  connected: boolean;
  vramBadge: string;
  /** The line to put IN THE THREAD when this heartbeat just crossed an edge — went unreachable, or
   *  came back — and null the rest of the time. A dot that changes colour says "this is how it is
   *  now"; it does not say "it just dropped". The Core decides which edge, and whether the very
   *  first check is silent. */
  edgeNotice?: string | null;
  /** The configured backend, as the chat header names it ("LM Studio"). */
  server?: string | null;
  /** Not connected, but the server REFUSED the check (a wrong API key: 401): the badge text, already localized.
   *  A server that answers is running — "unreachable" sends the user to start it. */
  refused?: string | null;
}

/** `host/runningWork` — what the host runs beyond a turn, as the notice to show once it is stopped. */
export interface RunningWorkResult {
  notice?: string | null;
  /** The same work, worded for a host that crashed — kept by the adapter as last seen. */
  ifItCrashes?: string | null;
}

/** `models/adoptDefault` — the model now used in place of a default nobody chose and the backend lacks, with the
 *  sentence that says so (already localized); both null when nothing changed. */
export interface ModelsAdoptResult {
  model: string | null;
  notice: string | null;
}

/** `connection/check` — what the Test button found AT THE URL IT WAS GIVEN.
 *  `provider` is the detected backend code (`ollama` | `lmstudio` | `openai-compatible`), so the
 *  panel can pre-select it the way the Visual Studio window does; null when nothing answered. */
export interface ConnectionCheckResult {
  ok: boolean;
  provider: string | null;
  /** Nothing detected, but a server refused the probe (a wrong API key: 401): the line to show instead of
   *  "unreachable", already in the user's language. */
  refused?: string | null;
}

/** One bubble, flattened for `chat/export`. `name` is the model (assistant) or tool name. */
export interface ChatExportMessage {
  role: string;
  name?: string;
  content: string;
  timestamp?: string;
  /** A notice (a slash command shown as typed): exported, not counted as a turn. */
  notice?: boolean;
}

/** `chat/export` — the adapter sends what it SHOWS, the Core renders the document. The whole
 *  point is that there is one exporter: the stats header and the .txt/.md layouts are the Core's,
 *  not something each editor writes again (and loses). */
export interface ChatExportParams {
  asPlainText: boolean;
  messages: ChatExportMessage[];
  sessionTokens?: number;
  durationSeconds?: number;
  /** The model that answers, when it is not the configured default; VS Code's window model is the host's default. */
  model?: string;
}

/** `command/list` entry — one slash command for the autocomplete popup. */
export interface SlashCommandInfo {
  command: string;
  hint: string;
}

/** `mention/categories` entry — one typed @mention category. */
export interface MentionCategory {
  token: string;
  description: string;
  queryBased: boolean;
}

/** One `mention/search` hit (file/folder sub-search). */
export interface MentionItem {
  label: string;
  detail: string;
  value: string;
}

/** `mention/resolve` answer: chip label + content (nulls = nothing to attach).
 * `notice` is a localized sentence to show INSTEAD of a chip — "nothing is paused", not a
 * failure. Without it an empty answer and a broken one both came out as silence. */
export interface MentionResolveResult {
  name: string | null;
  content: string | null;
  notice?: string | null;
}

/** One editor-side effect a handled slash command asks the adapter to apply.
 * Kinds: setPrompt | sendAsPrompt | attachChip (name = chip label) | copyToClipboard |
 * clearTranscript | stateChange (name = key) | openFile | exportRequest | configSaved | openSettings | openSetup.
 * Unknown kinds must be ignored (forward compatibility). */
export interface SlashEffect {
  kind: string;
  value?: string | null;
  name?: string | null;
}

/** One line of the `/import` preview: an item the user can tick (`number` is what `apply 1,3` names). */
export interface ImportCardItem {
  number: number;
  target: string;
  label: string;
  checked: boolean;
  notes: string[];
}

/** Parts of the source left out for one reason. */
export interface ImportCardGroup {
  reason: string;
  paths: string[];
}

/** The `/import` preview, every text already localized by the host (mirror of the Core's `ImportCard`). */
export interface ImportCard {
  /** The command's keyword (`continue`, `roo`): applying runs `/import <source> <file> apply …`. */
  source: string;
  file: string;
  title: string;
  summary: string;
  items: ImportCardItem[];
  /** The fold's title with its count — the summary's number, shown as is (a part can sit in two groups). */
  skippedTitle: string;
  skipped: ImportCardGroup[];
  applyLabel: string;
  applyHint: string;
  /** What the card shows, hashed: Apply sends it with the numbers, and an import whose plan moved since writes nothing. */
  fingerprint: string;
}

/** `command/slash` answer: `handled: false` ⇒ send the text as a normal chat prompt. */
export interface SlashCommandResult {
  handled: boolean;
  markdown?: string | null;
  effects?: SlashEffect[] | null;
  /** `/import` without `apply`: drawn as a card with a box per item, in place of `markdown`. */
  importCard?: ImportCard | null;
}

/** `rag/typingChips` — one chip of the draft being typed (Visual Studio's 🔮 chips). */
export interface TypingChip {
  /** The file, absolute. */
  path: string;
  name: string;
  /** The lines its excerpts span (`38–61`); empty when they cover the whole file. */
  lines: string;
  content: string;
}

/** `tests/generate`: the source file, the code to test (the selection, or the whole file), what was typed after /test. */
export interface TestsGenerateParams {
  path: string;
  code: string;
  request?: string;
}

/** `tests/generate` answer: the sentence to show; with `testPath`, the test file's new content, written by the editor
 *  (an existing file replaced in its buffer when `extended`, a new one created otherwise). */
export interface TestsGenerateResult {
  message?: string | null;
  testPath?: string | null;
  content?: string | null;
  extended?: boolean;
  cancelled?: boolean;
}

/** `codeAction/run` — headless in-place code action over the active document. */
export interface CodeActionParams {
  /** `edit` = Edit with AI: the instruction is `request`, and the code is the selection or the caret's line. */
  kind: 'fix' | 'refactor' | 'doc' | 'edit';
  text: string;
  selStart: number;
  selEnd: number;
  model?: string;
  /** The document's path. `/doc` needs it for the semantic block (namespace, hierarchy,
   *  overrides, interface contracts) the Visual Studio window has always had: the extractor
   *  dispatches on the extension and reads neighbouring files to find the contracts. */
  path?: string;
  /** What the user typed after the command (`/fix the off-by-one in the loop`): given to the model first. */
  request?: string;
}

/** One independently acceptable hunk: replace [start, end) of the submitted text. */
export interface CodeActionEdit {
  index: number;
  start: number;
  end: number;
  newText: string;
}

/** `codeAction/run` answer; `newText` is the full rewritten document when edited,
 * `failureDetail` the underlying error message when failed. */
/** `refused`: the document is excluded by the workspace's ignore files — nothing reached the model, `failureDetail`
 *  says why. */
export interface CodeActionResult {
  outcome: 'edited' | 'noChange' | 'failed' | 'cancelled' | 'refused';
  edits: CodeActionEdit[];
  newText?: string | null;
  failureDetail?: string | null;
}

/** `code/excerpt` answer: what a read-only action (/explain, /review) puts in the prompt — the code, or
 *  its first lines and a marker naming the count — and the label shown under the question. */
export interface CodeExcerptResult {
  text: string;
  label: string;
  truncated: boolean;
  /** The file is excluded by the workspace's ignore files: the sentence that says so, and nothing to send. */
  refusal?: string | null;
}

/** `exclusions/check` answer, one entry per path: null = not excluded, else the sentence for the person — and the
 *  same for the model, which is told what was asked and not sent. */
export interface ExclusionsCheckResult {
  notices: (string | null)[];
  forModel: (string | null)[];
}

/** One prompt layer of the Context X-Ray panel (`xray/panel` / `xray/toggle`). */
export interface XRaySection {
  id: string;
  label: string;
  tokens: number;
  percent: number;
  content: string;
  enabled: boolean;
  canToggle: boolean;
}

/** Full X-Ray panel model, ready to render in the webview. */
export interface XRayPanel {
  sections: XRaySection[];
  totalTokens: number;
  historyTokens: number;
  contextWindow: number;
  fillPercent: number;
  overheadWarning: boolean;
  rawPrompt: string;
  /** Tool definitions the next turn carries — counted in fillPercent. */
  toolTokens: number;
  /** The MCP tools the next turn leaves out until tool_search loads them, said in the user's language; absent when
   *  every tool is declared, and from an older host. */
  toolsOnDemand?: string | null;
  /** The tools turned off, which the next turn does not send, said in the user's language; absent when none is. */
  toolsTurnedOff?: string | null;
}

export interface ApprovalNote {
  message: string;
  /** The card the chat draws; absent from an older host, which only sends the message. */
  card?: ApprovalCard | null;
}

/** One line of an approval card's preview. */
export interface ApprovalPreviewLine {
  kind: 'add' | 'del' | 'ctx' | 'gap';
  text: string;
}

/** An approval as a card (`ApprovalCard` in the Core): what will happen, to what, the start of the change. */
export interface ApprovalCard {
  title: string;
  subject: string;
  meta: string;
  preview: ApprovalPreviewLine[];
  more: string;
  alwaysTooltip: string;
  message: string;
  /** The narrow rule the card offers ("Always: commands that start with `dotnet test`"); empty or absent when none. */
  ruleLabel?: string;
  /** The rule's lines as the settings will hold them. */
  ruleTooltip?: string;
}

/** One file a run changed. */
export interface RunFileLine {
  path: string;
  name: string;
  added: number;
  removed: number;
  created: boolean;
  gone: boolean;
}

/** A turn's run as the chat shows it under the answer (`RunSummary` in the Core). */
export interface RunSummary {
  steps: number;
  title: string;
  detail: string;
  files: RunFileLine[];
  check: 'none' | 'buildPassed' | 'buildFailed' | 'testsPassed' | 'testsFailed';
  checkText: string;
  runId: string;
}

/** One diff line of a run's review: `+`, `-`, ` ` or `…` (a cut, its text saying how much). */
export interface RunReviewLine {
  prefix: string;
  text: string;
}

/** One file of a run's review card (`RunReviewDto` in the host). */
export interface RunReviewFile {
  path: string;
  name: string;
  added: number;
  removed: number;
  created: boolean;
  gone: boolean;
  state: 'pending' | 'kept' | 'undone' | 'changedSince' | 'noCopy';
  stateLabel: string;
  canRedo: boolean;
  diff: RunReviewLine[];
}

/** `run/review`: each file a run changed against the state before it, its words in the person's language. */
export interface RunReview {
  runId: string;
  title: string;
  subtitle: string;
  note: string;
  changedWhy: string;
  files: RunReviewFile[];
  keep: string;
  undo: string;
  delete: string;
  redo: string;
  new: string;
}

/** `run/file`: the sentence to show, and the card as it stands after (null: the run is no longer kept). */
export interface RunFileResult {
  message: string;
  review: RunReview | null;
}

export interface SavedMessage {
  role: string;
  content: string;
  toolName?: string | null;
  timestamp?: string | null;
  /** The run of changes the turn this message closes made (the Core's SavedMessage.Run). */
  run?: string | null;
}

/** One file of a question's "Go back here" card. */
export interface RewindLine {
  path: string;
  name: string;
  kind: 'putBack' | 'deleted' | 'kept';
  label: string;
}

/** A question's "Go back here" card (`RewindCardDto` in the host), its words in the person's language. */
export interface RewindCard {
  messageIndex: number;
  question: string;
  title: string;
  body: string;
  files: RewindLine[];
  note: string;
  /** Nothing can be undone: the card offers the conversation only, or the runs' reviews. */
  refused: boolean;
  runIds: string[];
  go: string;
  cancel: string;
  conversationOnly: string;
  conversationOnlyHint: string;
  reviewRuns: string;
  reviewRunsHint: string;
}

/** `chat/rewind`: refused (`card`), stopped half-way (`message` only), or back at the question (`branch`). */
export interface RewindResult {
  card?: RewindCard | null;
  branch?: SessionBranchResult | null;
  question?: string | null;
  message?: string | null;
}

export interface SessionSummary {
  name: string;
  savedAt: string;
  messageCount: number;
  preview: string;
  /** Set on branches (`/branch`): the session this one was forked from, and where. */
  parent?: string | null;
  forkTurn?: number | null;
}

/** `session/list` answer: the sessions that could be read, and the files that could not. */
export interface SessionListResult {
  sessions: SessionSummary[];
  /** Session files present but unreadable: absent from `sessions`, their names still TAKEN. */
  unreadable: string[];
  /** Localized sentence naming them, or null when every file was read. */
  notice?: string | null;
}

export interface SessionLoadResult {
  name: string;
  messages: SavedMessage[];
  /** What the next question will send: the context gauge's fill for the conversation just loaded. */
  nextTurnTokens?: number;
}

/** session/branch answer: the new branch, its parent and the truncated transcript to render. */
export interface SessionBranchResult {
  name: string;
  parent: string;
  forkTurn: number;
  messages: SavedMessage[];
  /** Localized confirmation bubble, built host-side from the shared .resx. */
  message: string;
  /** What the next question will send: the context gauge's fill for the branch. */
  nextTurnTokens?: number;
}

/**
 * session/branchCommand answer: `/branch [args]` decided on the adapter's transcript. Exactly one
 * outcome; for a switch, `message` is the confirmation to show once the load succeeded.
 */
export interface SessionBranchCommandResult {
  message?: string | null;
  forkTurn?: number | null;
  switchTo?: string | null;
}

/** session/title answer: the bare LLM title plus the timestamped save file name. */
export interface SessionTitleResult {
  title: string;
  fileName: string;
}

/** session/archive answer: the name the archive is under until its title renames it. */
export interface SessionArchiveResult {
  name: string;
}

/** input/request (host → editor): a value a repository's MCP server asks for. */
export interface InputRequest {
  prompt: string;
  /** Hide what is typed (VS Code's `password` input). */
  password: boolean;
  /** Pre-filled value, if the server declares one. */
  value?: string | null;
}

/** One choice of an input card's field (`InputOptionModel` in the Core). */
export interface InputCardOption {
  value: string;
  label: string;
  selected: boolean;
}

/** One field of an input card (`InputFieldModel` in the Core), already in the user's language. */
export interface InputCardField {
  key: string;
  /** `text`, `number`, `boolean`, `choice` (one) or `choices` (several). */
  kind: 'text' | 'number' | 'boolean' | 'choice' | 'choices';
  /** For a text or number field: `text`, `email`, `url`, `date`, `datetime` or `number`. */
  inputType: string;
  label: string;
  hint: string;
  required: boolean;
  /** The field reads like a secret: the hint says a server must not ask for one. */
  warns: boolean;
  /** A `choice` field drawn as buttons side by side. */
  buttons: boolean;
  text: string;
  flag: boolean;
  options: InputCardOption[];
  /** What to fix before sending; empty when nothing is. */
  error: string;
  min?: number | null;
  max?: number | null;
}

/** `input/form` (host → editor): a form an MCP server asks the user to fill (`InputCardModel` in the Core). The same
 *  `id` comes back with what to fix: that card replaces the one drawn. */
export interface InputCard {
  id: string;
  server: string;
  title: string;
  subtitle: string;
  /** The server's own words: drawn as plain text, never Markdown, never a link. */
  message: string;
  fields: InputCardField[];
  errorSummary: string;
  requiredNote: string;
  reminder: string;
  sendLabel: string;
  declineLabel: string;
  declineTooltip: string;
  cancelLabel: string;
  cancelTooltip: string;
}

/** The answer to `input/form`: what the user did, and the fields' values (a string, a boolean, or the choices ticked). */
export interface InputFormAnswer {
  action: 'accept' | 'decline' | 'cancel' | 'setAside';
  values: Record<string, string | boolean | string[]>;
}

/** `chat/inputReceipt` (host → editor): the line an answered card leaves where it was. `saved` is what the conversation
 *  keeps under the role `input`. */
export interface InputReceiptNote {
  id: string;
  title: string;
  detail: string;
  body: string;
  saved: string;
}

/** approval/request answer: 0 = deny, 1 = allow once, 2 = always allow (session), 3 = the card's narrow rule, written to
 *  the machine's rules (any other number denies). */
export const enum ApprovalAnswer {
  Deny = 0,
  Once = 1,
  Always = 2,
  Rule = 3,
}

/** One choice of a select field (texts are product names — never localized). */
export interface SettingsOption {
  value: string;
  text: string;
  /** The sentence under the option's name when it is drawn as a card. */
  description?: string | null;
}

/** `models/listByKind`: the listed models, split by what each can be picked for. */
export interface ModelsByKind {
  /** The chat and every task model: the models a question can be asked of. */
  chat: string[];
  /** Code search: the models recognised as embedding models. */
  embedding: string[];
}

/** An editable setting. `label`/`hint` are resource names resolved via `settings/strings`. */
export interface SettingsField {
  key: string;
  kind: 'text' | 'password' | 'bool' | 'int' | 'float' | 'model' | 'select' | 'textarea';
  label: string;
  hint?: string | null;
  unit?: string | null;
  button?: string | null;
  options?: SettingsOption[] | null;
  /** Factory value of a numeric field (clearing the box restores it, as in the VS window), and of a
   *  field that opens the advanced fold ("true"/"false" for a boolean). */
  defaultValue?: string | null;
  /** A check box showing the opposite of the stored boolean. */
  inverted?: boolean;
  /** A value other than `defaultValue` opens the page with its advanced sections shown. */
  opensFold?: boolean;
  /** Resource name of the hint shown instead when the selected server is not Ollama. */
  hintNotOllama?: string | null;
  /** The structured editor drawn over a list setting's text. */
  editor?: 'pinnedFiles' | 'mcpServers' | 'approvalRules' | 'nameValue' | 'cards' | 'segmented' | null;
  /** A numeric box whose 0 means "not set": shown empty, and empty saves 0. */
  zeroIsEmpty?: boolean;
  /** Resource name of what an optional model list shows for its empty value ("Same as chat"). */
  emptyChoice?: string | null;
  /** A model box that offers the embedding models; every other one offers the chat models. */
  embeddingModels?: boolean;
  /** The field shows only while another field has a value: `key=value`. Hidden, it keeps its value. */
  showWhen?: string | null;
  /** The role whose server the field belongs to (inlineCompletion, ragEmbedding): its Test button and its model list
   *  use that server, never the chat's. */
  server?: string | null;
  /** Resource names of a nameValue table's column headers. */
  columns?: string[] | null;
  /** Bounds of a numeric box (declared once in the Core schema): a value outside them is named, never saved. */
  min?: number | null;
  max?: number | null;
}

/** `settings/indexCard`: the code index as the Code search page shows it. */
export interface IndexCard {
  /** `saved`: the settings window shows the index saved for its folder; `loading`: it is reading it. */
  state: 'noWorkspace' | 'indexing' | 'loading' | 'failed' | 'stopped' | 'notBuilt' | 'ready' | 'saved';
  title: string;
  detail: string;
  notes: string[];
  oversizeNote: string;
  oversizeFiles: string[];
  modelLine: string;
  buttonLabel: string;
  canRebuild: boolean;
}

/** `settings/loadedModels` and `settings/unloadModels`: what the server holds in memory, what Inferpal uses each model
 *  for, and what the last unload did. */
export interface LoadedModels {
  summary: string;
  rows: { name: string; uses: string; details: string }[];
  canUnload: boolean;
  message?: string | null;
}

/** `settings/suggestModels`: the best installed models, proposed into the form — never saved by the host. */
export interface ModelSuggestion {
  fields: { key: string; value: string; reason: string; changed: boolean }[];
  notes: string[];
  refusal?: string | null;
  summary: string;
}

/** `settings/exclusions`: the patterns of .inferpal/project.json the index applies. */
export interface SettingsExclusions {
  patterns: string[];
  file: string;
  exists: boolean;
}

/** One @Docs site of the Code search page. */
export interface DocsSiteRow {
  id: string;
  title: string;
  address: string;
  state: 'indexing' | 'notIndexed' | 'partial' | 'indexed';
  status: string;
  holeNote: string;
  busy: boolean;
  removeLabel: string;
}

/** `settings/docsSites` and `settings/docsAction`: the sites, and what the command said. */
export interface SettingsDocs {
  sites: DocsSiteRow[];
  message?: string | null;
}

/** One tool of the Tools page (the Core's `ToolListRow`): the rows `/tools` prints. */
export interface ToolListRow {
  /** The name it is turned off and on by: its own, or `server/tool` for an MCP tool. */
  key: string;
  name: string;
  weight: string;
  /** The colour of its row, never read back from its label. */
  state: 'sent' | 'onDemand' | 'off' | 'offByEntry' | 'notOffered';
  stateLabel: string;
  off: boolean;
  /** A button is offered: never for tool_search, nor for a tool only its server's entry turns off. */
  canToggle: boolean;
  actionLabel: string;
}

/** The tools of one source: the built-in ones, the user's own, or one MCP server's. */
export interface ToolListGroup {
  title: string;
  rows: ToolListRow[];
}

/** `settings/tools` and `settings/toolSet`: every tool, grouped, and what turning one off or on said. */
export interface SettingsTools {
  list: { summary: string; groups: ToolListGroup[]; howTo: string };
  message?: string | null;
}

/** One share of the conversation's window. */
export interface ContextUsagePart {
  key: 'instructions' | 'tools' | 'conversation';
  label: string;
  amount: string;
  percent: number;
}

/** `settings/contextUsage`: how full the conversation's window is, and with what. */
export interface ContextUsage {
  summary: string;
  parts: ContextUsagePart[];
}

/** One of the project's files the prompt reads (`settings/projectFiles`). */
export interface ProjectFileRow {
  name: string;
  description: string;
  fullPath: string;
  exists: boolean;
}

/** One instruction file the repository gives coding agents, and what the next question sends of it
 * (`settings/repoInstructions` — the rows `/instructions` prints). */
export interface RepoInstructionRow {
  family: string;
  file: string;
  fullPath: string;
  scope: string;
  sent: string;
  state: string;
  isSent: boolean;
  /** Tool, scope, what goes and state on one line, as the page shows them under the file. */
  detail: string;
}

/** What one pinned file costs the prompt (`settings/pinSizes`). */
export interface PinnedFileSize {
  path: string;
  size: string;
  missing: boolean;
}

/** One MCP server card (`mcp/cards`, `mcp/retry`, `mcp/authorize`). */
export interface McpCard {
  name: string;
  transport: string;
  target: string;
  enabled: boolean;
  state: 'connected' | 'signIn' | 'failed' | 'off' | 'notStarted';
  statusText: string;
  cause?: string | null;
  toolCount: number;
  /** Declared by the repository or passed by the editor: shown and signed in to, never edited from the list. */
  outsideTheList: boolean;
}

export interface McpCardsResult {
  summary: string;
  cards: McpCard[];
  /** Why a sign-in failed. */
  error?: string | null;
}

/** One row of the approval rules table (`permissions/table`). */
export interface ApprovalRuleRow {
  source: 'team' | 'machine';
  allow?: boolean | null;
  tool: string;
  pattern: string;
  status: 'inForce' | 'ignoredAllow' | 'unreadable';
  effectText: string;
  fromText: string;
  noteText?: string | null;
  /** Index of the line in the machine setting; -1 for a team rule. */
  machineLine: number;
}

export interface ApprovalRuleTable {
  teamUnusable: boolean;
  rows: ApprovalRuleRow[];
}

export interface SettingsSection {
  /** Resource name of the heading; empty = no heading. */
  title: string;
  fields: SettingsField[];
  description?: string | null;
  /** `advanced`: shown only when the page's "Show advanced settings" box is checked. */
  gate?: string | null;
  /** Rendered folded under its title. */
  collapsible?: boolean;
  /** Resource name of a sentence under the fields. */
  note?: string | null;
  /** Fields in two columns. */
  grid?: boolean;
  /** A live summary under the fields: `approvalRules`. */
  widget?: string | null;
}

/** One page of the settings window, reached from the side navigation. */
export interface SettingsTab {
  key: string;
  title: string;
  description: string;
  sections: SettingsSection[];
  /** Resource name of the page's "Show advanced settings" box. */
  advancedToggle?: string | null;
}

/** `settings/schema` answer: the form the webview renders, declared once in the Core. */
export interface SettingsSchema {
  tabs: SettingsTab[];
}

// ── Reverse `debug/*` DTOs (host → editor) ───────────────────────────────────
// Mirror of Inferpal.Host/HostProtocol.cs. Deliberately not a rendering of the Debug Adapter
// Protocol: only what the Core's IDebugSession port needs crosses this wire, and values stay
// opaque strings because the two editors' debuggers render them differently on purpose.

export interface DebugBreakpointDto {
  file: string;
  line: number;
  enabled: boolean;
}

export interface DebugBreakpointParams {
  file: string;
  line: number;
}

export interface DebugStepParams {
  /** `over` | `into` | `out`. */
  kind: string;
}

export interface DebugEvaluateParams {
  expression: string;
  /** Null = the top frame of the current stop. */
  frameId?: number | null;
}

export interface DebugFrameDto {
  id: number;
  function: string;
  file: string | null;
  line: number | null;
}

export interface DebugVariableDto {
  name: string;
  type: string;
  /** Rendered by the debug adapter. Present it; never parse it. */
  value: string;
}

export interface DebugStopStateDto {
  reason: string;
  threadId: number;
  frames: DebugFrameDto[];
  locals: DebugVariableDto[];
  exception?: string | null;
  /**
   * Id of the frame `locals` were read from. The ordinary capture reads the top frame, the `/tdd`
   * capture reads the first frame under the workspace root, and the renderer hides frames outside
   * it — so the frame printed first is often not this one, and the reader must be told which.
   */
  localsFrameId?: number | null;
}

/**
 * `debug/start` answer — four outcomes: a stop, a completed run (all null), a failure, or a program
 * still running with no stop within the budget (`stillRunning`) — which is neither of the other two.
 */
export interface DebugStartDto {
  state: DebugStopStateDto | null;
  failure: string | null;
  stillRunning?: boolean;
}

/**
 * `debug/continue` and `debug/step` answer: the stop, or why there is none. `outcome` is `ended` (the run finished:
 * the assistant's breakpoints go with the session), `running` (still running past the budget: they stay) or
 * `not-paused`; `failure` is the editor's own words when the resume itself failed. The outcome strings are the host's
 * (`DebugOps.Resumed`).
 */
export interface DebugResumeDto {
  state: DebugStopStateDto | null;
  outcome: 'ended' | 'running' | 'not-paused' | null;
  failure?: string;
}

/** `debug/captureTest`: launch the repro runner under coreclr and capture the failure. */
export interface DebugCaptureTestParams {
  program: string;
  args: string[];
  cwd: string;
  projectRoot: string;
}

/** `config/update` answer: what the save could not use. */
/** `pins/list|add|remove` — the pinned files after the call, and what the host had to say. */
export interface PinsResult {
  pins: string[];
  notice?: string | null;
}

export interface ConfigUpdateResult {
  /** Permission rules the product could not read — the field is saved, these lines are inert. */
  permissionRulesIgnored: number;
}

// ── First run (`setup/*`): the search for a server, the proposal, the download ───────────────────

/** Why the first-run card is shown (`setup/needed`); null when it is not. */
export type SetupReason = 'unreachable' | 'noChatModel';

/** One place the first run looked at, with its name and what it answered, in the editor's language. */
export interface SetupPlace {
  url: string;
  expected?: string | null;
  outcome: 'answers' | 'nothingListening' | 'notAModelServer' | 'needsKey' | 'checking';
  provider?: string | null;
  detail?: string | null;
  chatModels: number;
  searchModels: number;
  name: string;
  status: string;
  alsoAnswering?: string | null;
}

/** A model the card offers to download, with its line, its button and its progress title. */
export interface SetupDownload {
  name: string;
  tag: string;
  downloadGb: number;
  cardGb: number;
  measured: string;
  button: string;
  downloading: string;
  /** The download's size, in the editor's language. */
  size: string;
}

/** `setup/plan`: what is proposed for a server (chatModel null: it has none — downloads, or where to get one). */
export interface SetupPlan {
  chatModel?: string | null;
  completionModel?: string | null;
  searchModel?: string | null;
  chatCount: number;
  budgetGb: number;
  neededGb: number;
  fits: boolean;
  downloads: SetupDownload[];
  listed: string[];
  lead: string;
  legend: string;
  fitText?: string | null;
  itsModels?: string | null;
  noDownload?: string | null;
  /** The graphics memories to choose from, when the server's was not found (or was chosen here, to change it). */
  cardChoices: { gb: number; label: string }[];
  cardQuestion?: string | null;
}

export interface SetupApplyParams {
  url: string;
  provider: string;
  chatModel: string;
  completionModel?: string | null;
  searchModel?: string | null;
  apiKey?: string | null;
  /** The graphics memory chosen on the card, saved with the server. */
  cardGb?: number | null;
}

/** `setup/pullProgress` notification. */
export interface SetupPullProgress {
  tag: string;
  status: string;
  percent?: number | null;
}
