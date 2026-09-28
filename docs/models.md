# Models

Inferpal works with any chat model served by Ollama, LM Studio or an OpenAI-compatible server. What differs from one
model to the next is not whether it can be used, but **how it writes its turn**: where its reasoning goes, what a tool
call looks like when the server leaves it as text, and what the chat template bundled with it accepts. This page says,
for each model Inferpal is measured with, what the model does, what Inferpal does with it, and how to set it up.

> [!TIP]
> In a hurry? **Qwen3.8 27B** and **Devstral Small 2** are the two models to start with for agent work; see
> [At a glance](#at-a-glance). Every figure on this page comes from Inferpal's own test battery — real tasks on real
> projects, judged on their outcome (the project's tests green), never on the model's wording.

## At a glance

| Model | Use it for | What Inferpal does for it | Server notes |
|---|---|---|---|
| [Qwen3.8 27B](#qwen35) (and Qwen3.6 27B) | ✅ **Agent** — recommended (14/14) | Reads its XML tool calls when the server leaves them as text | Reasoning separated by LM Studio |
| [Devstral Small 2](#devstral) | ✅ **Agent** — recommended, fastest (13/14) | Reads its `[TOOL_CALLS]` calls when left as text | Temperature **0.15** |
| [Muse Glimmer](#muse-glimmer) | ✅ **Agent** — recommended (14/14) | Splits its addressed messages (reasoning / answer) and reads its ATEM tool calls | Needs a recent llama.cpp runtime |
| [Bonsai 27B](#bonsai) (1-bit Qwen3.6) | ✅ **Agent** on small GPUs (13/14, slow) | Same as Qwen3.8 | 4.7 GB for 27B |
| [GLM 4.7 Flash](#glm47) | Agent — usable with the **Unsloth GGUF** (12/14) | Reads its `<arg_key>` tool calls when left as text | Older GGUFs (lmstudio-community) loop |
| [Qwen3 Coder 30B](#qwen3-coder) | Autocomplete (FIM); agent usable (9/14) | Uses its fill-in-the-middle tokens; reads its XML tool calls | Fast: 3B active parameters |
| [Qwen3 4B Thinking 2507](#qwen3) | Small machines | Reads its JSON tool calls when left as text | Long reasoning |
| [Gemma 4 31B](#gemma4) | Agent through a fallback — *to be measured* | Describes the tools in the system prompt when the template cannot render them | LM Studio's template fails on tools |
| [Qwen2.5 Coder 7B](#qwen-coder) | ✅ **Autocomplete (FIM)** — not the agent (3/14) | Uses its fill-in-the-middle tokens | Not trained for tool calling |

Embedding models (semantic search): **Qwen3 Embedding 0.6B** and **Nomic Embed Text v1.5** both work; set the one you
use in *Settings → RAG*.

## How Inferpal reads a model

**Tool calls.** When the server parses the model's calls, they arrive as structured `tool_calls` and nothing more is
needed. When it does not — a server without a parser for that model, or one that only parses when a call is forced —
the call arrives as text, and Inferpal reads it: JSON in `<tool_call>…</tool_call>` (Qwen3, Hermes style), Qwen's XML
(`<function=name><parameter=key>…`), GLM's `<arg_key>`/`<arg_value>` pairs, Mistral's `[TOOL_CALLS]name[ARGS]{…}`,
Gemma's `<|tool_call>call:name{…}<tool_call|>`, and Muse Glimmer's ATEM block. A call Inferpal reads this way runs
exactly like a structured one.

**Reasoning.** Most servers put the model's reasoning in a separate field (`reasoning_content`), and Inferpal shows it
as reasoning. When a server leaves it in the answer, Inferpal separates it: `<think>…</think>` tags (including the
lone `</think>` Qwen3 and GLM produce when their template opened the tag), Muse Glimmer's `to=self` messages, and
Gemma's thought channel. The answer you read, the history the model reads back, and every artifact made from a reply
(edits, tests, commit messages) carry the answer only.

**Chat templates that cannot write tools.** A model's chat template sometimes fails as soon as a request carries tool
definitions — the server then refuses every such request with *"Error rendering prompt with jinja template"*. Inferpal
recognises that refusal, asks again with the tools described in the system prompt and the tool calls and results
written as text, and keeps doing so for that model for the rest of the session. The model works as an agent; only the
transport changes.

**Constraints Inferpal respects.** The templates of Qwen3.8, Bonsai and Devstral refuse a system message anywhere but
first; Inferpal never sends one elsewhere. A response that loops (the same passage, or the same tool call, again and
again) is stopped and said to be incomplete, instead of running until the context window is full.

**Sampling.** For the families on this page, Inferpal sends the sampling settings their vendor recommends — only the
values the vendor publishes; the **Settings** line of each section says exactly which. A server applies its generic
defaults to a model it has no preset for, and those can hurt a model badly (GLM's vendor says to disable the repeat
penalty that llama.cpp applies by default). On LM Studio and Ollama every value is sent; on another OpenAI-compatible
server only temperature and top_p, the fields the OpenAI API defines. To use your server's per-model settings instead,
set `"useRecommendedSampling": false` in the configuration.

**The model is recognised by its name, and the name only chooses.** Inferpal recognises a family from the model id
(`qwen3.8`, `devstral`…) for two decisions: the model it picks on first run — a family measured as a good agent
first, in the order of the table above, then a model Inferpal does not know, and a family measured to fail as the
agent last — and the autocomplete prompt, which uses fill-in-the-middle tokens only for the families measured to
complete better with them ([below](#fim-models)). How a reply is *read* never depends on the name: every call form and every reasoning form
above is read for every model, so a renamed or fine-tuned model keeps working.

<a id="qwen35"></a>
## Qwen3.8 27B, Qwen3.6 27B

- **Model.** Alibaba Qwen, 27B, reasoning model (architecture `qwen35` in LM Studio), 262,144-token context.
- **Tool calls.** XML: `<tool_call><function=name><parameter=key>value</parameter></function></tool_call>`; tool
  results go back as `<tool_response>` blocks in a user turn.
- **Reasoning.** On by default; the template opens `<think>` itself, so a server that does not separate reasoning
  leaves a lone `</think>` in the answer. LM Studio (0.4.7 and later) separates it.
- **Inferpal.** Structured calls on LM Studio; the XML form is read when a call arrives as text. Autocomplete uses
  its fill-in-the-middle tokens: 27 right completions in 36 with them, 12 from the code before the cursor alone.
- **Settings.** Inferpal sends the thinking-mode values: temperature 1.0, top_p 0.95, top_k 20, min_p 0.0. Without
  thinking, Qwen recommends temperature 0.7, top_p 0.8, presence penalty 1.5.
- **Known issues.** Its template refuses a system message that is not the first one (HTTP 500 "System message must
  be at the beginning"). On LM Studio, the model can occasionally announce an action without emitting the call
  ([#2440](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2440)).
- **Sources.** [Model card](https://huggingface.co/Qwen/Qwen3.8-27B) ·
  [LM Studio](https://lmstudio.ai/models/qwen/qwen3.8-27b)

<a id="devstral"></a>
## Devstral Small 2 (2512)

- **Model.** Mistral AI, 24B, agentic coding model, not a reasoning model. Context 256K (the GGUF declares 393,216;
  Mistral recommends passing 262,144).
- **Tool calls.** `[TOOL_CALLS]name[ARGS]{json}`, several in a row for parallel calls; results go back in
  `[TOOL_RESULTS]` blocks, matched by position.
- **Inferpal.** Structured calls on LM Studio; the `[TOOL_CALLS]` form is read when a call arrives as text.
- **Settings.** Inferpal sends temperature 0.15 (the model card's value).
- **Known issues.** The template bundled with the lmstudio-community GGUF refuses a system message that is not the
  first one and enforces strict user/assistant alternation. Inside tool-call JSON the model sometimes drops a
  character — measured: the `$` of a C# interpolated string (`$"Total: {…}"`), in one call in five in isolation and
  in every attempt of one rename task, which then compiles but prints the braces.
- **Sources.** [Model card](https://huggingface.co/mistralai/Devstral-Small-2-24B-Instruct-2512) ·
  [LM Studio](https://lmstudio.ai/models/mistralai/devstral-small-2-2512)

<a id="bonsai"></a>
## Bonsai 27B

- **Model.** PrismML's 1-bit conversion of Qwen3.6-27B (one sign bit per weight plus a scale per 128 weights,
  about 1.125 bits per weight): 4.7 GB for 27B parameters, same architecture and chat template as Qwen3.6.
- **Tool calls and reasoning.** Same as [Qwen3.8](#qwen35): XML calls, `<think>` reasoning. Autocomplete, unlike
  Qwen3.8's, completes from the code before the cursor: with the fill-in-the-middle tokens its 1-bit weights got no
  completion right in 36, against 8 without.
- **Settings.** Inferpal sends temperature 0.7, top_p 0.95, top_k 20. PrismML advises capping the reasoning (for
  example 8,192 thinking tokens) rather than switching it off.
- **Measured.** 13 tasks in 14 — the best result for its size — but the slowest of the recommended models: 28 minutes
  over the battery, against 19 for Qwen3.8 and 5 for Devstral. It reasons at length before each call.
- **Known issues.** PrismML states that long-horizon agentic coding "is not yet a strong target of this release"; the
  one task it failed was an edit it repaired by removing a line the page needed.
- **Sources.** [Model card](https://huggingface.co/prism-ml/Bonsai-27B-gguf) ·
  [PrismML docs](https://docs.prismml.com/models/bonsai-27b)

<a id="muse-glimmer"></a>
## Muse Glimmer

- **Model.** Meta, 28–30B, agentic model; reasoning is always on (strength `low` to `xhigh`, default `high`).
  Context 131,072.
- **Turn format.** A turn is a sequence of addressed messages: `<|start|>assistant to=self<|message|>…<|eom|>` for
  its reasoning, `<|start|>assistant to=user<|message|>…` for the answer, `<|start|>assistant to=tool_name<|message|>`
  for a tool call.
- **Tool calls.** ATEM: `<atem:function_calls><atem:invoke name="…"><atem:parameter name="…">value</atem:parameter>
  </atem:invoke></atem:function_calls>`; one call per turn.
- **Inferpal.** LM Studio streams the addressed messages as plain text: Inferpal splits them (reasoning shown as
  reasoning, the answer as the answer) and reads the ATEM calls, which LM Studio only parses itself when a call is
  forced.
- **Settings.** Inferpal sends temperature 1.0, top_p 0.95, top_k 64 (not greedy).
- **Measured.** 14 tasks in 14.
- **Known issues.** Needs a recent llama.cpp runtime (LM Studio runtime 2.28.2 or later), or it fails to load with
  "unknown model architecture". Its template lists tool names without a dot oddly, which can lead the model to invent
  names such as `read.filePath` ([#60](https://huggingface.co/meta-models/Muse-Glimmer-30B/discussions/60)); an
  invented name is answered with the list of real tools.
- **Sources.** [Meta docs](https://dev.meta.ai/docs/muse-glimmer/prompting) ·
  [Model card](https://huggingface.co/meta-models/Muse-Glimmer-30B)

<a id="gemma4"></a>
## Gemma 4 31B (QAT)

- **Model.** Google, 31B, quantization-aware Q4_0, reasoning switchable (thought channel), 262,144-token context.
- **Tool calls.** `<|tool_call>call:name{key:<|"|>value<|"|>}<tool_call|>` — strings wrapped in `<|"|>`, numbers and
  booleans bare.
- **Inferpal.** On LM Studio, the bundled chat template fails whenever a request carries tools (see below): Inferpal
  describes the tools in the system prompt instead, reads the calls from the text, and stops the model where it opens
  the tool's response — Gemma's own format ends a call with `<tool_call|><|tool_response>`, and a model nobody stops
  there writes the result itself, then more calls and more invented results. Reasoning leaked as
  `<|channel>thought…<channel|>` is separated.
- **Settings.** Inferpal sends temperature 1.0, top_p 0.95, top_k 64.
- **Known issues (LM Studio).** Older lmstudio-community templates call a macro they never define,
  `format_type_argument` ([#2012](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2012)); newer ones use a
  Jinja test LM Studio does not support, `Unknown test: sequence`
  ([#2233](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2233)). Both refuse every request with tools.
  To get Gemma's native tool calling back, fix the template in *My Models → Gemma 4 → Prompt Template* (the fixes are
  in those issues); Inferpal's fallback works either way.
- **Measured.** Not yet over the whole battery.
- **Sources.** [Prompt format](https://ai.google.dev/gemma/docs/core/prompt-formatting-gemma4) ·
  [Function calling](https://ai.google.dev/gemma/docs/capabilities/text/function-calling-gemma4)

<a id="glm47"></a>
## GLM 4.7 Flash

- **Model.** Zhipu AI / Z.ai, 30B mixture of experts (3B active), reasoning on by default.
- **Tool calls.** `<tool_call>name<arg_key>key</arg_key><arg_value>value</arg_value></tool_call>`.
- **Inferpal.** Reads that form when a call arrives as text; a lone `</think>` is separated as reasoning.
- **Settings.** Inferpal sends Z.ai's values for tool calling and agentic coding — temperature 0.7, top_p 1.0 — with
  min_p 0.01 and repeat penalty 1.0 (no penalty), as Unsloth advises: llama.cpp's defaults (min_p 0.05, repeat
  penalty 1.1) degrade it. Z.ai's general-purpose values are temperature 1.0, top_p 0.95.
- **Known issues.** GGUFs made before the llama.cpp fix of January 21 set the model's expert scoring function to
  softmax instead of sigmoid: the model loops and its output degrades — measured here with the lmstudio-community GGUF:
  loops of several minutes, invented tool names, 0 tasks in 7. **Use the Unsloth GGUF** (`UD-Q4_K_XL`), re-uploaded
  with the fix — measured: 12 tasks in 14, fast (3B active parameters).
- **Sources.** [Model card](https://huggingface.co/zai-org/GLM-4.7-Flash) ·
  [Unsloth GGUF](https://huggingface.co/unsloth/GLM-4.7-Flash-GGUF)

<a id="qwen3"></a>
## Qwen3 4B Thinking 2507

- **Model.** Alibaba Qwen, 4B, reasoning only (no switch), 262,144-token context.
- **Tool calls.** JSON in `<tool_call>{"name": …, "arguments": …}</tool_call>`.
- **Inferpal.** A small reasoning model can loop on a tool call inside its reasoning: Inferpal stops at the repeat
  and runs the first call. Autocomplete completes from the code before the cursor: 15 right completions in 36,
  against 3 with the fill-in-the-middle tokens.
- **Settings.** Inferpal sends temperature 0.6, top_p 0.95, top_k 20, min_p 0.0.
- **Sources.** [Model card](https://huggingface.co/Qwen/Qwen3-4B-Thinking-2507)

<a id="qwen-coder"></a>
## Qwen2.5 Coder 7B

- **Model.** Alibaba Qwen, 7B code model, no reasoning, fill-in-the-middle tokens (`<|fim_prefix|>`…).
- **Use it for** inline completion (ghost text) and chat. It was not trained for tool calling: as the agent it
  often answers with a call written as text, or none — measured here: 3 tasks in 14.
- **Settings.** Inferpal sends temperature 0.7, top_p 0.8, top_k 20, repeat penalty 1.05 (the model's generation
  configuration).
- **Sources.** [Model card](https://huggingface.co/Qwen/Qwen2.5-Coder-7B-Instruct)

<a id="qwen3-coder"></a>
## Qwen3 Coder

- **Model.** Alibaba Qwen, code models (30B mixture of experts and larger), no reasoning, fill-in-the-middle tokens
  like Qwen2.5 Coder, trained for tool calling.
- **Tool calls.** The same XML as [Qwen3.8](#qwen35) — Qwen3 Coder introduced it.
- **Inferpal.** Autocomplete with its fill-in-the-middle tokens. As the agent, 9 tasks in 14 (30B): fast, but it
  often stops before the task is done — three renames left callers behind, one bug fix ended with the tests still
  failing.
- **Settings.** Inferpal sends temperature 0.7, top_p 0.8, top_k 20, repeat penalty 1.05 (the model card's values).
- **Sources.** [Qwen3-Coder](https://github.com/QwenLM/Qwen3-Coder)

<a id="fim-models"></a>
## Autocomplete (FIM) models

Inline completion (ghost text) asks the model to fill the gap between the code before and after the cursor. For the
families below, Inferpal builds the prompt with their fill-in-the-middle tokens when the server does not (LM Studio;
Ollama applies the model's template itself). Any other model completes from the code before the cursor only — it
still works, without seeing what follows.

Having the tokens in its vocabulary does not make a model good at the task: every Qwen model has them, and measured on
what reaches the editor (12 gaps to fill, 3 attempts each), Qwen3.8 fills 27 in 36 with them and 12 without, Qwen2.5
Coder 31 against 13 — but Qwen3 4B Thinking 3 against 15, and Bonsai, a 1-bit Qwen3.6, 0 against 8. Each family gets
the form measured best. Qwen2.5 Coder, the best at autocomplete, is not an agent model: it is not trained for tool
calling (3 tasks in 14).

| Family | Tokens |
|---|---|
| [Qwen2.5 Coder](#qwen-coder), [Qwen3 Coder](#qwen3-coder), [Qwen3.8 / Qwen3.6](#qwen35) | `<\|fim_prefix\|>…<\|fim_suffix\|>…<\|fim_middle\|>` |
| <a id="codegemma"></a>CodeGemma | the same `<\|fim_…\|>` tokens |
| <a id="deepseek-coder"></a>DeepSeek Coder | `<｜fim▁begin｜>…<｜fim▁hole｜>…<｜fim▁end｜>` |
| <a id="starcoder"></a>StarCoder, StarCoder2 | `<fim_prefix>…<fim_suffix>…<fim_middle>` |
| <a id="codellama"></a>Code Llama | `<PRE> … <SUF>… <MID>` |

## Measured results

Inferpal's battery runs real tasks on real projects with each model — fix a bug so the tests pass, add a class,
rename a method everywhere, answer a question about the code, run a command — in C#/.NET, JavaScript (jest) and
Python (pytest), and judges the outcome only. LM Studio, 32K context, the Linux host built from source.

| Model | Result | Measured |
|---|---|---|
| Qwen3.8 27B | 14/14 | 2026-09-28 |
| Muse Glimmer | 14/14 | 2026-09-28 |
| Devstral Small 2 | 13/14 | 2026-09-28 |
| Bonsai 27B | 13/14 | 2026-09-28 |
| Qwen3.6 27B | 13/14 | 2026-09-27 |
| GLM 4.7 Flash (Unsloth GGUF) | 12/14 | 2026-09-28 |
| Qwen3 Coder 30B | 9/14 | 2026-09-28 |
| gpt-oss 20B | 8/14 | 2026-09-28 |
| Codestral 22B | 7/14 | 2026-09-28 |
| Qwen3 4B Thinking 2507 | 5/8 (C# only) | 2026-09-27 |
| Llama 3.1 8B Instruct | 4/14 | 2026-09-28 |
| Qwen2.5 Coder 7B | 3/14 | 2026-09-28 |
| GLM 4.7 Flash (lmstudio-community GGUF) | 1/8 (C# only) | 2026-09-27 |
| Gemma 4 31B | to be measured — the test server lacked the memory to run it next to the embedding model | — |
