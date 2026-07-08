# Claude Revit

**English** | [Русский](README.ru.md)

Claude AI in Autodesk Revit — a dockable chat pane with **180+ tools** that let Claude inspect and modify your model directly. Ask it to create walls, generate schedules, place families, dimension grids, reinforce structural elements, author parametric families, draft sketches, and more. Runs on **Revit 2025, 2026 and 2027**.

Run it on the pay-per-token **Anthropic API**, on your **Claude Pro/Max subscription** (via a built-in MCP server + the Claude Code CLI — zero API cost), or on any **OpenAI-compatible** provider (DeepSeek, Gemini, OpenRouter, Groq, local Ollama…).

---

## Features

- **Dockable chat pane** in Revit, with streaming responses
- **180+ tools** spanning modeling, views, sheets, annotation, schedules, filters, families, the Family Editor, and reinforcement
- **Multiple AI providers** — Claude (Sonnet 5 / Opus 4.8 / Fable 5 / Haiku 4.5, + legacy Sonnet 4.6 / Opus 4.7) **or** any OpenAI-compatible endpoint: DeepSeek, Google Gemini, ChatGPT/OpenAI, Qwen, OpenRouter, Groq, and local **Ollama** / **LM Studio**. Pick "Alt" in the model dropdown; free and local models need no Anthropic key.
- **Auto (cost-optimized) mode** — the default: a cheap model (Sonnet 5) runs every turn and consults a stronger advisor (Opus 4.8, or Fable 5) mid-turn *only when it needs a plan*, via Anthropic's advisor tool. The cheap model's prompt cache stays warm all session; the advisor is billed only for the short consult. A legacy whole-turn model-switch is available in Settings.
- **Subscription mode (MCP / Claude Code)** — drive Revit on your **Claude Pro/Max subscription** instead of the pay-per-token API. The plugin runs a local **MCP server** (127.0.0.1, token-protected) exposing the Revit tools; the **Claude Code CLI** runs headless in the background and drives them. Tick **Subscription** in the model dropdown (or connect the MCP server to the Claude Desktop app with the ready-made config in Settings). Zero API cost.
- **Model benchmark (📊)** — run a graded task set (basics, composite, rebar, steel) on any model and compare pass rate, rounds, tokens and time. An independent judge grades strictly from an objective before/after probe (never the model's own claims); both the tested model and the judge can run on the subscription for a zero-API-cost benchmark.
- **Lazy-loaded toolset** — only a core set of tools rides in each request; specialised groups (rebar, MEP, schedules, sheets, annotation, sections, family editing, export) are revealed on demand via `find_tools`, cutting the per-request tool-schema cost by ~⅔ (a big saving on non-caching alt models). `run_batch` repeats one tool over many items in a single transaction.
- **Single-undo per prompt** — Ctrl+Z reverts everything Claude did in one turn
- **Selection awareness** — green pill shows what's selected; Claude knows what "this" means
- **Markdown rendering** + **selectable text** in messages
- **Clickable element IDs** — click any id in a tool result, Revit selects and zooms to that element
- **Cost & balance telemetry** with prompt caching (1h TTL on system prompt + tools ⇒ ~5–7× cheaper for long sessions); enter your credit balance and the pane shows the remaining estimate
- **Full context persistence** — the entire API conversation (tool calls, results, element IDs) survives Revit restarts, so Claude remembers what it built
- **Automatic compaction** — when the conversation outgrows the model's context budget, older turns are summarized instead of overflowing the window
- **Tool-result aging** — old tool results are truncated in place (and archived) to save tokens in long sessions; `get_full_result` retrieves an archived one on demand
- **Local learning layer** — `save_memory` persists your preferences and project standards; every script run is journaled with the model delta it produced, proven patterns are injected into the system prompt and **survive clearing the chat**, and a diagnostic report of recurring scripts is written when Revit closes (or on demand via `generate_diagnostic_report`) so they can be promoted into dedicated tools
- **Full Revit API escape hatch (opt-in)** — for anything no built-in tool covers, Claude can run scripts against the full Revit API: `execute_csharp` (the default — compiled C#, runs in a managed transaction) or `run_dynamo_python` (for Python snippets). **Off by default**: enable it with a checkbox in settings.
- **Configurable tool-round limit** — cap how many tool-call rounds Claude may take per message (default 24), raise it in Settings for long automated jobs
- **Optional confirmation for destructive / code ops** — off by default (every turn is one undo step); turn on an Allow/Deny dialog in settings
- **In-pane API key entry** — gear icon; keys are stored encrypted with Windows DPAPI (no plain-text env var)

---

## Install (for users)

You need:

- **Autodesk Revit 2025, 2026 or 2027** — the installer detects which of these you have and lets you tick the ones to install for
- **Windows** (Revit is Windows-only)
- **Anthropic API key** — get one at [console.anthropic.com](https://console.anthropic.com/settings/keys) and add credits in **Billing** — *or* a free/alternative provider (DeepSeek, Gemini, OpenRouter, Groq, local Ollama / LM Studio…), configured in Settings

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
3. **In the chat**, tick **Subscription** in the model dropdown — the selected model
   (Opus / Sonnet / Haiku) then runs on the subscription via the CLI. Or pick
   **Claude Code (subscription)** to use the CLI's default model.

You can also point the **Claude Desktop app** (or any MCP client) at the server — a
ready-to-paste config is shown in Settings → MCP.

> Note: the Auto advisor / haiku→opus escalation is an API-loop feature and does **not** apply
> in subscription mode — Claude Code runs its own agent loop with the one chosen model.

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

The plugin exposes **180+ tools** to Claude across these categories:

- **Inspection** — get/list elements, parameters, levels, materials, phases, families, project info, warnings, batch element locations/bounding boxes (mm)
- **Geometry creation** — walls, floors, roofs, rooms, levels, grids, doors, windows, columns, beams, foundations, MEP (ducts/pipes), topography, curtain walls
- **Reinforcement** — rebar sets (straight bars with count/spacing), area (mesh) & path reinforcement, rebar cover types, type listing and host inspection
- **Family Editor** — author parametric families natively: list/add/remove family parameters, set formulas, set values (mm), flip instance/type, associate nested-element parameters, create linear arrays with parametric counts, create labeled dimensions between references
- **Learning & escape hatch** — `save_memory`, `get_script_journal`, `generate_diagnostic_report`; `execute_csharp` / `run_dynamo_python` (full-API code for actions no tool covers — off by default, enable via a settings checkbox)
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

See the [`ClaudeRevit/Tools/`](ClaudeRevit/Tools) folder for the full list — every `.cs` file there is one tool.

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
