# OpenAI and Codex with Revit 2027

## Chat inside Revit

In Settings → Models choose OpenAI (the economical GPT-5 Mini preset) or
OpenAI GPT-6 Astra. Enter your OpenAI API key in the password field and choose
Alt in the chat model menu. The key uses the existing Windows DPAPI encrypted
storage. ChatGPT subscription access and OpenAI API billing are separate.

Requests to the official `https://api.openai.com/v1` endpoint use Responses API.
This is required for Astra function calls. Other compatible providers continue
using Chat Completions. Claude's existing API and subscription modes are retained.
Responses use `store: false`; encrypted reasoning is retained in conversation
history and replayed only to the same model. Incomplete streams never execute
the partially received tool calls.

Official references:

- https://developers.openai.com/api/docs/guides/latest-model
- https://developers.openai.com/api/docs/guides/migrate-to-responses
- https://developers.openai.com/api/docs/guides/reasoning

## Drive Revit from Codex

1. Install the CI build for Revit 2027, then start Revit. In Settings → Subscription
   (MCP), enable the local MCP server. Save. Arbitrary code execution is optional
   and is not needed for ordinary modeling tools or the connection test.
2. Keep `scripts/revit-mcp-bridge.ps1` at a stable local path. Register it with
   Codex (replace the example path with its actual absolute path):

   ```powershell
   codex mcp add clauderevit -- powershell.exe -NoProfile -NonInteractive -File "C:\path\to\revit-mcp-bridge.ps1"
   ```

3. Reconnect MCP / start a fresh Codex session so the tools load. Revit must remain
   running. The bridge reads the token and port from `%APPDATA%\ClaudeRevit\settings.json`
   for every request; the token is never put in Codex configuration or arguments.
4. Call `test_revit_connection`. It reports the Revit version and the active
   document title, then creates a level in a separate unsaved test document,
   verifies it, rolls back, verifies removal, and closes that test document.
   A null active document means no project was open; open a project and repeat
   if verification of an open project is required.

The MCP endpoint is local and authenticated. `POST /mcp` serves JSON-RPC;
authenticated `GET /health` serves diagnostics. `GET /mcp` returns 405 because
this stateless implementation has no server-initiated SSE stream.

Codex's selected model drives the MCP tools using Codex's own authentication.
This route does not require an OpenAI API key inside Revit. The in-Revit OpenAI
chat route above does require a separately billed API key.

Codex reference: https://learn.chatgpt.com/docs/extend/mcp?surface=cli
