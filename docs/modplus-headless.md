# ModPlus headless adapters — Revit 2027

ClaudeRevit v3.9.3 integrates the supplied `modplus-headless-adapters.zip` source
package and its 12 October 2026 binding snapshot. Registration happens at plugin
startup; copying a bootstrap or external C# tool is unnecessary. No vendor DLLs
or decompiled ModPlus implementation sources are distributed.

## Entry points and actual coverage

`run_modplus_headless` is the compact API-chat entry point. MCP clients can also
discover 85 aliases named `modplus_` plus the lowercase module ID, for example
`modplus_mprnumbering`. Those aliases share the same schema; the router adds
`module`. Ordinary ModPlus discovery loads the router rather than 85 duplicate
schemas. `get_modplus_tools` links module records to their headless entry points.

| Action | Behavior |
| --- | --- |
| `describe` | Page method bindings, inspect an exact type, and return native operation schemas. Metadata needs no installed ModPlus. |
| `native` | Run one of 32 independently implemented Revit API operations across 27 modules, with no ModPlus DLL or dialog required. |
| `validate` | Check exact original method IDs, snapshot identity and the known call graph without running the algorithm. |
| `execute` | Run up to 100 typed `new` / `set` / `call` steps against listed methods in already-loaded, matching ModPlus assemblies. |

The 83 available original modules contain 5,287 candidate method bindings;
4,800 have supported argument signatures. These numbers describe metadata/ABI
coverage, **not 4,800 verified unattended workflows**. Two additional aliases
(`mprCopyParameterValues`, `mprFloorByRoom`) provide native operations despite
the original 2027 modules being unavailable in the snapshot. `mprSumParameters`
continues to use the fixed adapter documented in [the base integration](modplus.md).

Native operations cover parameters and numbering, text replacement, sheets,
view duplication, crop boxes, view ranges, template filters, visibility and
graphics, viewport alignment, text notes/fonts, phases, worksets, warnings,
join/unjoin/order, type changes, deletion, curve division/copying, parameter
copying, room floors and floor/toposolid conversion. Rebar coverage here means
setting visibility in 3D; existing ClaudeRevit tools handle rebar creation.

## Examples

Call `run_modplus_headless` with:

```json
{"module":"mprNumbering","action":"describe","limit":5}
```

Use the returned native schema to construct `args`; exact parameter selectors
can use a built-in name or shared-parameter GUID. This preview sets an instance
comment and then rolls the model change back:

```json
{
  "module":"mprParameters",
  "action":"native",
  "operation":"set_parameters",
  "preview":true,
  "args":{
    "element_ids":[12345],
    "values":[{"parameter":{"built_in":"ALL_MODEL_INSTANCE_COMMENTS"},"value":"Checked"}]
  }
}
```

After reviewing a preview, repeat with `preview:false` to commit. Pass the
returned `plan_token` when present; the dispatcher enforces its normal unchanged
document check. Newly created IDs from a preview are temporary. Native contracts
explicitly distinguish millimetres from internal feet; follow each returned schema.

For original-method pipelines, obtain exact method/type IDs from `describe`,
then use `validate`. Use argument objects by the original parameter names.
`$ref` resolves stored values or `app`, `uidoc`, `doc`, `view`; arbitrary DLL paths
and cross-document references are rejected. `transaction:auto` detects ownership;
`owned` permits the original algorithm's transactions, while `single` opens a
transaction around each call. In every mode an outer group covers the operation.

## Settings, project binding and transactions

The code-execution switch is required for headless calls, including original
pipelines. The **ModPlus** group controls the router; **ModPlus headless** controls
the aliases. Both groups and the code switch apply even through a saved-source
proxy. The existing optional confirmation setting still applies.

The bound project must be the active Revit tab. Original methods also require
ModPlus's own current document to match. Reflected execution checks assembly
MVID and SHA-256 against the snapshot and never loads vendor assemblies itself.
`CLAUDEREVIT_MODPLUS_BINDINGS` can point to a regenerated local binding JSON;
it is read at startup and must come from a trusted local source.

These tools own their transactions and run outside the chat's turn group. Preview
defaults to true and rolls back model edits. Commit produces one named Undo group
per call. Exceptions and cancellation before commit roll back the group; cancellation
cannot interrupt an individual Revit API or vendor call while it is executing.
Explicit original-pipeline selection is validated and restored afterward.
Large replies are shortened while preserving success/error and rollback fields.

## Limits and verification

The original-method guard rejects known UI/pick/dialog calls, asynchronous work,
P/Invoke, reflection/dynamic execution, unresolved virtual/delegate dispatch and
known file writes, exports and document saves. It is deliberately conservative
and is **not an arbitrary-code sandbox**. A rejected method needs a dedicated
native adapter. Preview covers Revit model edits; it cannot undo vendor static
state or every possible external side effect.

Local validation passed: 354 unit tests, 51 isolated base-adapter/policy checks,
42 headless metadata/call-graph checks, and compilation of the integrated adapter
sources against the installed Revit 2027 API. CI checks the same managed contracts
against the production assembly; release packaging builds Revit 2025/2026/2027.
No live RVT/RFA execution with licensed ModPlus was performed for this integration.
Geometry, Undo and vendor behavior on a real model still need native acceptance checks.

Revit 2025 and 2026 retain the four base catalog/interactive tools; these headless
bindings are registered only in Revit 2027. They do not reproduce every option
of ModPlus's original windows or remove any vendor licensing requirement.
