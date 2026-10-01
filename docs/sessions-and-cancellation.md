# Project conversations and MCP cancellation (v3.6)

The pane stores a separate conversation and separate Claude Code/Codex session IDs
for each document. Saved files use their normalized path; workshared projects use
the central GUID; cloud projects use the cloud project/model GUIDs. Unsaved documents
use an identity valid for this Revit process, so two unrelated `Project1` documents
cannot inherit each other's chat. Saving an unsaved document establishes a new scope.

Storage is under `%AppData%\ClaudeRevit\conversations\<project hash>\<slot>\`.
An exclusive lease prevents two running Revit instances from sharing a slot. A later
run can reuse an available slot and restore its history. A particular concurrent
instance is not guaranteed to regain the same slot after both instances restart.
History and CLI session files are replaced atomically. Legacy global conversation
files remain on disk; they are not automatically attributed to an arbitrary project.
Clearing a conversation clears that scope's history/session IDs.

Switching to another document manually cancels a busy pane task. The old conversation
is saved before the new one loads; draft text and attached images are cleared. A tool
that deliberately activates a family/document can continue its own task. Its messages
stay in the originating conversation; the next prompt starts in the new document's
scope. A queued operation checks its intended document again before it executes.

## What Stop does

- It cancels the pane's CLI process and its private MCP turn channel.
- Cancelled jobs still in Revit's queue are dropped without executing.
- A synchronous Revit API operation cannot be forcibly interrupted. The pane displays
  **Stopping…** while that operation finishes. A transactional tool checks cancellation
  before commit and rolls back; script tools have a group for rolling back their managed
  transactions. Completion is reported after cleanup.
- Previously committed steps can remain. Nontransactional actions such as an export
  or document activation cannot generally be undone by cancellation. Inspect the result
  and use Revit Undo if needed.
- Requests belonging to other MCP clients/turns remain independent. Late requests on
  a closed turn channel are rejected.

## Connecting an external client

This is a stateful Streamable HTTP server. `initialize` returns `Mcp-Session-Id` in the
HTTP response header. Send that header on subsequent requests/notifications. Missing
headers return HTTP 400; unknown/expired sessions return 404 and require initialization
again. HTTP DELETE ends that session and cancels its requests. GET returns 405 because
this server does not offer a standalone SSE stream. Idle sessions expire after two hours
when another connection is initialized; the server caps connected sessions at 256.

`notifications/cancelled` addresses an active request ID in that same session. A numeric
ID and a string ID are distinct. An HTTP connection closing alone is not cancellation.
These semantics follow the MCP [transport](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)
and [cancellation](https://modelcontextprotocol.io/specification/2025-06-18/basic/utilities/cancellation) specifications.

The packaged Windows PowerShell stdio bridge consists of **both**
`revit-mcp-bridge.ps1` and `revit-mcp-bridge.cs`, in the same folder. It preserves session
headers and forwards notifications concurrently with pending tool calls; stdin EOF
ends its server session. Update both files when updating an older manual installation.

Settings → MCP lists connected clients. Select the intended connection before requesting
a model/identity report. Reported model names come from the client, so a report is not
independent proof of which model the client runs. MCP cannot switch a client's model.

## Validation

The automated suite covers project/instance leases, restart restoration, atomic files,
queued versus running cancellation, typed request IDs and per-client model directives.
The WPF pane suite covers switching history while busy and controlled family activation.
The transport suite starts the real HTTP server and a real Windows PowerShell bridge,
with two independent clients and cancellation during a pending call. Its dispatcher is
a test double: actual transaction rollback must still be exercised in a supported Revit
installation. CI builds against the Revit 2025, 2026 and 2027 APIs before release.
