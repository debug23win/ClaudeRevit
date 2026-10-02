# Claude Revit

MCP agents: choose **Claude Code · MCP** or **Codex · MCP**, then select a model
and reasoning effort. Choices are saved independently. Codex reads its model
catalog from the installed CLI. See [agent selection and setup](docs/openai-codex.md).

**v3.7.1:** the installer detects Revit on custom drives using registered paths and the executable version; its version selection text adapts to display scaling.

**v3.7:** family nesting analysis and flex tests, native parametric forms and complex
reinforcement, committed rollback previews, a catalog of 63 BIMStarter commands,
ADSK 2019/2021 FOP references and a Samolet EIR profile. The benchmark defaults to
Codex/ChatGPT subscription; modeller and judge models are selected independently.
[New tools and limits](docs/advanced-bim.md) · [EIR analysis](docs/samolet-eir.md).

**English** | [Русский](README.ru.md)

Claude AI in Autodesk Revit — a dockable chat pane with **213 tools** that let Claude inspect and modify your model directly. Ask it to create walls, generate schedules, place families, dimension grids, reinforce structural elements, author parametric families, draft sketches, and more. Runs on **Revit 2025, 2026 and 2027**.

Run it on the pay-per-token **Anthropic API**, on your **Claude Pro/Max subscription** (via a built-in MCP server + the Claude Code CLI — zero API cost), or on any **OpenAI-compatible** provider (DeepSeek, Gemini, OpenRouter, Groq, local Ollama…).

---

## Features

- **Complex families** — recursive analysis, rollback flex tests for types/dimensions/options, five native form kinds, void combinations, external shared definitions and nested instance associations.
- **Native reinforcement** — line/arc paths, stirrups/hooks, layouts, explicit free-form bars, actual constraint candidates, couplers and PNG schedule sketches.
- **Precise operations** — stable-reference dimensions, complete deletion cascade preview, paged material quantities with paint separated, beam systems and parameter colors with legend data.
- **BIMStarter** — all 63 source commands cataloged with explicit coverage, independent native workflows and posting buttons in a compatible installed plugin. Dialog/cloud commands require the plugin and user interaction.

- **Dockable chat pane** in Revit, with streaming responses
- **Separate project conversations** — chat history and Claude Code/Codex sessions follow the active document. Concurrent Revit instances reserve separate history slots. Switching documents manually stops the current task before loading the other conversation.
- **Independent MCP clients** — model reports, settings directives and cancellation belong to each connection. Settings let you select a connected client. Stop cancels this pane's queued work and waits for a running Revit operation to settle.
- **BIMStarter, ADSK and EIR context** — live GUIDs, bindings, units, templates and schedules via `get_project_standards`; 313 BIMStarter GUIDs and 323 ADSK GUIDs (566 unique combined; 1120 edition/translation rows) via `get_shared_parameter_catalog`. `get_standard_workflows` explains counting schemes and profile differences; `validate_project_standard` performs a partial Samolet EIR audit. Workbook group recommendations do not prove live bindings.
- **213 tools** spanning modeling, views, sheets, annotation, schedules, filters, families, the Family Editor, and reinforcement
- **Multiple AI providers** — Claude (Opus 5 / Fable 5.1 / Sonnet 5 / Opus 4.8 / Fable 5 / Haiku 4.5, + legacy Sonnet 4.6 / Opus 4.7) **or** any OpenAI-compatible endpoint: OpenAI (presets for GPT-6 Astra and GPT-5.6 Sol/Terra/Luna), DeepSeek, Google Gemini, Qwen, OpenRouter, Groq, and local **Ollama** / **LM Studio**. Pick "Alt" in the model dropdown; free and local models need no Anthropic key.
- **OpenAI models can drive Revit too (via MCP)** — the local **Codex CLI** connects to the same MCP server, so GPT-6 Astra / GPT-5.6 Sol-Terra-Luna edit the model through the Revit tools. Cloud ChatGPT can't reach a `127.0.0.1` server, and exposing one publicly would put model editing behind nothing but a token — Codex runs on your machine, so no tunnel is needed. Follow-up messages continue the same Codex conversation, and the session survives a Revit restart.
- **See who is driving** — replies keep their agent names. Settings show an external MCP client from its handshake and its self-reported model, which may be missing or inaccurate.
- **Auto (cost-optimized) mode** — the default: a cheap model (Sonnet 5) runs every turn and consults a stronger advisor (Opus 4.8, or Fable 5) mid-turn *only when it needs a plan*, via Anthropic's advisor tool. The cheap model's prompt cache stays warm all session; the advisor is billed only for the short consult. A legacy whole-turn model-switch is available in Settings.
- **Subscription agents (MCP)** — choose Claude Code · MCP or Codex · MCP, then a model and reasoning effort. Official CLIs drive the local Revit tools; agent settings and conversations are saved separately.
- **Model benchmark (📊)** — defaults to Codex/ChatGPT subscription for modeller and judge. Select each backend, available model and reasoning effort independently; API billing is explicit. Subscription login checks exclude inherited API keys. The no-tools judge grades objective before/after probes; unavailable grading is marked ungraded. Cleanup previews deletion cascades and protects baseline objects; it does not restore edits/deletions of existing elements. [Checks and limits](docs/advanced-bim.md#benchmark).
- **Lazy-loaded toolset** — only a core set of tools rides in each request; specialised groups (rebar, MEP, schedules, sheets, annotation, sections, family editing, export) are revealed on demand via `find_tools`, cutting the per-request tool-schema cost by ~⅔ (a big saving on non-caching alt models). `run_batch` repeats one tool over many items in one turn, each item in its own sub-transaction, so an item that fails rolls back on its own instead of leaving half its edits behind (it declines to wrap tools you've put behind the confirmation prompt — those are still asked one by one). In subscription (MCP) mode the handshake also hands the model a full tool index so it can call the right tool without a discovery round-trip.
- **Smart element filter** — `filter_elements` answers "all walls taller than 3 m on Level 2, and total their length" in one call: unit-aware predicates (mm/m²/m³ pseudo-parameters computed from geometry), AND/OR logic, level/active-view scoping, and an optional count/sum/avg/min/max aggregate.
- **Version & update check** — the pane shows a clickable "update available" link (and the Settings → About tab a "Check for updates" button) when a newer GitHub release exists; notify-only, since a loaded add-in can't replace its own DLL while Revit runs.
- **Tabbed settings** — General · Models · Subscription (MCP) · Tools · About.
- **Documentation & QA workflows** — the chores that normally need a one-off script: `autonumber_elements` (numbering in drawing reading order, with tolerance-banded rows so a ragged grid still reads left-to-right), `derive_parameters` (fill a parameter from geometry/identity or a `{placeholder}` template), `auto_join_geometry` (find intersecting pairs and join them — the fix for double-counted concrete), `calculate_weight` (mass from real volume × material density; rebar weighed from length × nominal diameter), `get_element_hosts`, `diagnose_model` (warnings, in-place families, exploded CAD imports, oversized groups — each with a recommendation), `clean_model` (removes them; **dry-run by default**), `assign_worksets`, `generate_sheet_set`, `batch_export_sheets`, `create_assembly`, `export_element_coordinates` (setting-out points in **shared/site** coordinates) and `query_linked_elements` (reads linked models, applying the link transform so coordinates land in host space).
- **Bar bending data** — `get_rebar_shape_sketch` returns ordered leg lengths and bend angles per bar, groups identical bars into schedule positions, and can emit an SVG sketch of each distinct shape.
- **Works with non-Claude MCP clients** — the MCP server detects the connecting client and emits a portable tool schema for it. OpenAI's function-calling validator rejects the constraint keywords used across these tools (`minimum`/`maximum`, `minItems`, `oneOf`) and refuses such a tool list *wholesale*, so those constraints are folded into each parameter's description instead of dropped. Claude clients keep the richer schemas. *(If you connect an OpenAI model to this server yourself rather than through Codex, note that MCP is reached via the Responses API, not chat completions.)*
- **Grouped undo for ordinary API operations** — committed previews, family inspection/document changes and self-managed native tools close the current group and may create separate undo steps. MCP commands have their own transactions.
- **Selection awareness** — green pill shows what's selected; Claude knows what "this" means
- **Markdown rendering** + **selectable text** in messages
- **Clickable element IDs** — click any id in a tool result, Revit selects and zooms to that element
- **Cost & balance telemetry** with prompt caching (1h TTL on system prompt + tools ⇒ ~5–7× cheaper for long sessions); enter your credit balance and the pane shows the remaining estimate
- **Full context persistence** — the entire API conversation (tool calls, results, element IDs) survives Revit restarts, so Claude remembers what it built
- **Automatic compaction** — when the conversation outgrows the model's context budget, older turns are summarized instead of overflowing the window
- **Tool-result aging** — old tool results are truncated in place (and archived) to save tokens in long sessions; `get_full_result` retrieves an archived one on demand
- **Local learning layer** — `save_memory` persists your preferences and project standards; every script run is journaled with the model delta it produced, and patterns are injected into the system prompt ranked by how often they actually *worked* (a pattern that mostly fails is dropped rather than offered as proven practice) and **survive clearing the chat**, and a diagnostic report of recurring scripts is written when Revit closes (or on demand via `generate_diagnostic_report`) so they can be promoted into dedicated tools
- **Full Revit API escape hatch (opt-in)** — for anything no built-in tool covers, Claude can run scripts against the full Revit API: `execute_csharp` (the default — compiled C#, runs in a managed transaction), `run_python` (in-process Python through pyRevit's or RevitPythonShell's IronPython engine — no Dynamo boot, so it starts instantly), or `run_dynamo_python` (Dynamo's Python engine, for proven Dynamo-community snippets). **Off by default**: enable it with a checkbox in settings.
- **Keeps Revit responsive** — a streamed answer is re-rendered on a short coalescing timer instead of on every token (rebuilding the whole document per token is quadratic in answer length, and it ran on Revit's UI thread), and the straightforward tool calls of one round are dispatched together rather than one Idling round-trip each. Cancelling a turn now also drops the tools that were still queued, so nothing keeps editing the model after you press stop.
- **Configurable tool-round limit** — cap how many tool-call rounds Claude may take per message (default 24), raise it in Settings for long automated jobs
- **Optional confirmation for destructive / code ops** — off by default (every turn is one undo step); turn on an Allow/Deny dialog in settings
- **In-pane API key entry** — gear icon; keys are stored encrypted with Windows DPAPI (no plain-text env var)

---

## Install (for users)

You need:

- **Autodesk Revit 2025, 2026 or 2027** — the installer detects which of these you have and lets you tick the ones to install for
- **Windows** (Revit is Windows-only)
- **Subscription login** in Codex/ChatGPT or Claude Code (where permitted by the account), or an **API provider**. For Anthropic API, get a key at [console.anthropic.com](https://console.anthropic.com/settings/keys) and add credits in **Billing** — *or* a free/alternative provider (DeepSeek, Gemini, OpenRouter, Groq, local Ollama / LM Studio…), configured in Settings

Pick whichever install path you prefer:

### Option A — Installer .exe (easiest)

Download **`ClaudeRevit-Setup-vX.Y.exe`** from the [latest release](https://github.com/debug23win/ClaudeRevit/releases/latest), double-click, tick the Revit versions you want → Install. Done.

> Windows SmartScreen may say "Windows protected your PC" the first time (the installer isn't code-signed yet). Click **More info → Run anyway**.

### Option B — PowerShell one-liner

Open PowerShell and run:

```powershell
iwr https://raw.githubusercontent.com/debug23win/ClaudeRevit/main/install.ps1 | iex
```

Either way: launch Revit, open any project, look for the **Claude** tab in the ribbon. Click **Chat** → the pane opens on the right. First time? Click the **⚙** icon in the pane and paste your API key (or configure an alternative provider).

To update later, re-run the installer or the one-liner — both pick up the latest release.

---

## Running on your Claude subscription (MCP)

Instead of paying per token on the Anthropic API, you can drive Revit with your **Claude
Pro/Max subscription**. The plugin runs a local **MCP server** that exposes the Revit tools,
and the **Claude Code CLI** connects to it and does the work — the cost lands on your
subscription, not the API.

1. **Install the Claude Code CLI** (a terminal program, separate from the Claude Desktop app).
   In PowerShell:
   ```powershell
   irm https://claude.ai/install.ps1 | iex
   ```
   Then run `claude` once and log in with your subscription. (A VPN is required if `claude.ai`
   is blocked in your region — for install *and* for every run.)
2. **Enable the MCP server** in Settings → MCP (set a port if you like; the plugin locates
   `claude` automatically, or enter its full path).
3. **In the chat**, choose **Claude Code · MCP**, then a model and reasoning effort. Automatic uses the CLI default.

You can also point the **Claude Desktop app** (or any MCP client) at the server — a
ready-to-paste config is shown in Settings → MCP.

> Note: the Auto advisor / haiku→opus escalation is an API-loop feature and does **not** apply
> in subscription mode — Claude Code runs its own agent loop with the one chosen model.

---

## Driving Revit with OpenAI models (Codex)

Install Codex and sign in with ChatGPT. Choose **Codex · MCP**, refresh the model
list, then select a model and reasoning effort. The pane configures the local MCP
connection automatically for each process; no API key or manual server registration
is needed. Codex choices are saved separately from Claude Code.

Release packages include `revit-mcp-bridge.ps1` and `revit-mcp-bridge.cs` for external
Codex clients; keep both files in the same folder when copying the bridge.
[Setup and validation details](docs/openai-codex.md).

---

## Build from source (for developers)

You need:

- **Visual Studio 2026 Community** (or Rider, or VS Code with C# Dev Kit)
- **.NET 8 SDK** and **.NET 10 SDK** — 2025/2026 target .NET 8, 2027 targets .NET 10
- **Autodesk Revit** installed locally (only required for F5 debugging — compile works without it)

Steps:

1. Clone:
   ```powershell
   git clone https://github.com/debug23win/ClaudeRevit.git
   cd ClaudeRevit
   ```
2. Open `ClaudeRevit.sln` in Visual Studio.
3. Right-click the project → **Restore NuGet Packages**.
4. Set `ClaudeRevit` as the startup project.
5. Press **F5**.

The post-build target copies the DLL + addin manifest to `%AppData%\Autodesk\Revit\Addins\<year>\` automatically. F5 launches Revit and attaches the debugger.

### How the build works

- Revit API references come from **Nice3point.Revit.Api.RevitAPI** and **Nice3point.Revit.Api.RevitAPIUI** NuGet packages — no local Revit install required to compile.
- The post-build `DeployToRevit` target copies to `%AppData%\Autodesk\Revit\Addins\<year>\`. To skip the local deploy (e.g. on CI), pass `-p:SkipDeploy=true`.
- A separate `PackageRelease` target stages all release artifacts under `bin\Release\release\` — used by the GitHub Actions workflow.

---

## Revit versions

The plugin supports **Revit 2025, 2026 and 2027** and ships a separate build for each,
because the versions run on different .NET runtimes (2025/2026 → .NET 8, 2027 → .NET 10).
At install time, detected versions are pre-checked; tick any you want and each gets the
matching build in its own `%AppData%\Autodesk\Revit\Addins\<year>\` folder.

To build locally for a specific version, pass `RevitVersion`:

```powershell
dotnet build ClaudeRevit\ClaudeRevit.csproj -c Release -p:RevitVersion=2026
```

The Nice3point API package (`$(RevitVersion).0.*`), the target framework, and the deploy
folder all follow that one value. Version-specific API differences are handled with
`REVIT2025` / `REVIT2026` / `REVIT2027` compile symbols. Revit 2024 and earlier run
.NET Framework 4.8 and are out of scope for the current build.

---

## Releasing a new version

The `.github/workflows/release.yml` workflow builds all three Revit versions and publishes
a release. Trigger it from the Actions tab (**Run workflow** → enter a version like `v1.30`),
or push a `v*` tag. GitHub Actions then:

1. Builds each Revit version (2025/2026/2027) in Release mode with `SkipDeploy=true`
2. Produces a per-version zip (`ClaudeRevit-vX.Y-Revit<year>.zip`)
3. Compiles the Inno Setup installer bundling all three, with a version-picker wizard page
4. Creates a GitHub Release with the installer + zips and auto-generated notes

Users get the new version with the same installer / `install.ps1` one-liner.

---

## Tools

The plugin exposes **213 tools** to Claude across these categories:

- **Inspection** — get/list elements, parameters, levels, materials, phases, families, project info, warnings, batch element locations/bounding boxes (mm)
- **Geometry creation** — walls, floors, roofs, rooms, levels, grids, doors, windows, columns, beams, foundations, MEP (ducts/pipes), topography, curtain walls
- **Reinforcement** — rebar sets (straight bars with count/spacing), area (mesh) & path reinforcement, rebar cover types, type listing and host inspection, bar-bending data (leg lengths, bend angles, schedule positions, SVG shape sketches)
- **Documentation & QA** — spatial auto-numbering, rule-based parameter fill, bulk geometry join/unjoin, host ↔ hosted navigation, mass take-off by material density, drawing-set generation (view + sheet per level), batch PDF/DWG export with filename templates, assemblies with shop-drawing views
- **Model health** — `diagnose_model` (warnings by kind, in-place families, exploded CAD imports, unplaced rooms, oversized groups, design options, views without templates — each with severity and a recommendation) and `clean_model` to remove them, dry-run by default
- **Coordination** — worksets assignment by rule, and reading elements out of linked models in host coordinates
- **Setting-out** — element coordinates in shared/site or internal system, N/E ordering, natural-sorted marks, optional CSV
- **Family Editor** — author parametric families natively: list/add/remove family parameters, set formulas, set values (mm), flip instance/type, associate nested-element parameters, create linear arrays with parametric counts, create labeled dimensions between references
- **Learning & escape hatch** — `save_memory`, `get_script_journal`, `generate_diagnostic_report`; `execute_csharp` / `run_python` / `run_dynamo_python` (full-API code for actions no tool covers — off by default, enable via a settings checkbox)
- **Element ops** — move, rotate, copy, mirror, array, delete, set_parameter, pin/unpin, join/unjoin
- **Views** — 3D, floor plan, ceiling plan, section, elevation, callouts, duplicate, dependent views, set scale, apply template, crop/section box
- **Sheets** — create sheets, place views/schedules on sheets, move viewports
- **Schedules** — real Revit ViewSchedules with field selection, CSV export
- **Annotation** — dimensions, tags, text notes, detail lines, filled regions, reference planes, revisions, spot elevations/coordinates
- **Visibility** — hide/isolate in view, color overrides, category visibility, filters
- **Interactive** — `pick_point_in_view` for click-to-place workflows
- **Families** — load .rfa files, list loaded families, generic instance placement, duplicate/edit types
- **Groups** — create/place/ungroup model groups
- **Export & IO** — export view image, PDF, DWG, schedule CSV, save document
- **Selection** — `select_similar` for "select all instances of this type"

See [`ClaudeRevit/Tools/`](ClaudeRevit/Tools) for tool implementations and shared helpers. Some files implement related tools together. The authoritative list is the registration block in [`App.cs`](ClaudeRevit/App.cs).

---

## Architecture

- **`App.cs`** — `IExternalApplication` entry point; registers tools, dockable pane, ribbon button, and the DocumentChanged learning hook
- **`Tools/`** — every `IRevitTool` class. Add a new file + register in `App.cs` → it's available to Claude.
- **`Tools/ToolDispatcher.cs`** — `IExternalEventHandler` that runs tool calls on Revit's API thread. Wraps each tool in a `Transaction`, wraps each turn in a `TransactionGroup` for one-undo-per-prompt.
- **`Services/ChatService.cs`** — the provider-agnostic agentic loop. Streams each turn through either the Anthropic API or an OpenAI-compatible backend into a common turn model; sets cache control on the system prompt, tools and history for 1-hour prompt caching; handles compaction and tool-result aging.
- **`Services/OpenAIBackend.cs`** — SSE streaming, function calling and usage accounting for any OpenAI-compatible endpoint (the "Alt" model).
- **`Services/ScriptJournal.cs` / `ExperienceStore.cs`** — the learning layer: journals every script run with its model delta, builds the proven-scripts digest injected into the system prompt, and writes the diagnostic report.
- **`Services/HistoryStore.cs` / `MemoryStore.cs` / `ApiKeyStore.cs` / `SettingsStore.cs`** — persistence: conversation history, Claude's memory, encrypted keys, and settings.
- **`UI/ChatPaneView.xaml`** — WPF chat pane (virtualized message list, markdown rendering, clickable element-id links).
- **`Services/SelectionService.cs`** — tracks Revit selection changes to keep Claude's context current.

---

## License

MIT — do whatever you want with it.
