# OpenAI and Codex with Revit

## Chat inside Revit

Choose **API**, configure the OpenAI provider and your OpenAI API key in Settings,
then select Alt in the model menu. The key uses Windows DPAPI encrypted storage.
ChatGPT subscription access and OpenAI API billing are separate.

Requests to the official `https://api.openai.com/v1` endpoint use Responses API.
Other compatible providers continue using Chat Completions. Responses use
`store: false`; opaque encrypted reasoning is saved in history and replayed only
to the model that produced it. Incomplete streams never execute partial tool calls.

## Subscription agents inside Revit

Choose **Claude Code · MCP** or **Codex · MCP** in the agent menu. The model and
reasoning selectors appear below it. Choices are saved separately for each agent,
and the pane restores the last selected agent after restart. Switching agent does
not transfer its CLI conversation to the other agent; each resumes its own session.

For Codex, **Refresh models** reads the installed CLI's `model/list` catalog,
including all pages, visible models and supported reasoning levels. Automatic
uses the catalog's default model. The selected model and level are validated
before sending, then explicitly passed to Codex for fresh and resumed sessions.
A missing model or unsupported level returns an error instead of silently selecting
another. Changing model resets the level selector to Default.

Claude Code offers Automatic, Sonnet, Opus and Haiku aliases, or you can type a
full model ID. Its CLI receives `--model` and `--effort`. Supported reasoning
levels depend on the installed Claude Code version and model; a rejected choice
returns the CLI error. Choosing a model preserves that agent's existing session.

Codex checks native CLIs in PATH and desktop app installations and chooses the
highest actual `codex --version`, rather than stopping at an older PATH entry.
The version probe is bounded and cached until the file changes. An explicit
path in Settings takes precedence and remains pinned. It checks ChatGPT login without reading or
copying credentials. The Revit MCP connection is configured for that process
automatically. The MCP token is passed only in the child's environment. The
process uses a read-only filesystem sandbox, shell access is disabled, and
Revit model edits go through MCP tools. No API key or manual MCP registration is
needed for this pane route. Attached images are passed to Codex as local image input.

Selection is locked while a turn runs. **Clear** resets both CLI conversations.
Saved replies keep their original agent labels when the agent changes.

## External Codex app or CLI

Enable the MCP server in Settings and keep Revit running. The release packages
include `revit-mcp-bridge.ps1` and `revit-mcp-bridge.cs`; keep both files together
at a stable path. Register the PowerShell entry point with Codex:

```powershell
codex mcp add clauderevit -- powershell.exe -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "C:\path\to\revit-mcp-bridge.ps1"
```

Reconnect MCP or start a new Codex session. The bridge reads the current token and
port from `%APPDATA%\ClaudeRevit\settings.json` for each request; neither is
stored in Codex arguments or configuration. Call `test_revit_connection` to read
the Revit version and active document, create and verify a level in a separate
unsaved test document, roll it back, verify removal, and close the test document.
It leaves the user's document untouched.

`POST /mcp` serves authenticated JSON-RPC. Authenticated `GET /health` serves
diagnostics; `GET /mcp` returns 405 because the server has no SSE stream.
An external client's model is selected in that client. The add-in can request a
change on the next tool result but cannot force an external client's model switch.

## Validation

Unit tests cover routing, option forwarding, catalog validation, Responses
transport and event parsing. `ClaudeRevit.PaneTests` compiles the real pane with
an isolated in-memory backend and checks switching, independent saved choices,
typed IDs, busy controls and narrow layouts. It does not connect to Revit or an
account. The release workflow runs both suites and builds Revit 2025/2026/2027.

The installed Codex CLI 0.159.2 returned eight models in a catalog probe, with
`gpt-6.1-sol` as the default. The older PATH CLI 0.154.0 did not expose it.
Automatic discovery now selects 0.159.2; if you explicitly pinned an old CLI path,
clear it or select the updated executable before refreshing models. The account's
actual catalog remains authoritative; the plugin does not invent model availability. No
inference turn was run. Live modeling and real OpenAI API calls were not
revalidated for this release.

References: [Codex app-server](https://learn.chatgpt.com/docs/app-server),
[MCP configuration](https://learn.chatgpt.com/docs/extend/mcp?surface=cli),
[Responses migration](https://developers.openai.com/api/docs/guides/migrate-to-responses),
[Claude Code CLI](https://code.claude.com/docs/en/cli-reference).
