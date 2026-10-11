# ModPlus integration

ClaudeRevit has four built-in tools from the supplied ModPlus Revit 2027 analysis
bundle. They are available to API chat and both subscription agents through the
ordinary tool discovery path. Requests mentioning ModPlus / «Модплюс» pre-load the
deferred **ModPlus** group; its switch is in Settings alongside other native groups.

| Tool | Actual coverage |
| --- | --- |
| `get_modplus_tools` | Search 86 module records: 84 available for 2027, 215 declared external commands. Match the live ribbon, including nested buttons. Page modules and commands separately. |
| `get_modplus_algorithm` | Page command/service classes and method signatures. Read a bounded excerpt from the user's local source bundle when present. Metadata works without local source files. |
| `modplus_sum_parameters` | Fixed read-only adapter to the installed free `mprSumParameters.Services.GetSelectElements` public service. No user-selected method or DLL path. |
| `run_modplus_command` | Preview or queue one exact live ModPlus ribbon button through Revit `PostCommand`. It can require a license, dialog or user picks. `posted=true` means queued, not completed. |

The catalog snapshot was captured on **12 October 2026**. Source coverage and
candidate signatures are analysis data, not 84 completed unattended adapters.
Interactive commands retain ModPlus's normal UI and licensing. Never use them in
an unattended benchmark; the tool rejects that mode. Wait for the user to finish
the plugin dialog, then inspect the actual model result.

## Local bundle and versions

Metadata is embedded in ClaudeRevit. Decompiled source and vendor DLLs are not
distributed in the repository or release packages. The supplied local analysis
bundle remains under:

```
%AppData%\ClaudeRevit\tools\modplus-revit-2027\catalog.json
%AppData%\ClaudeRevit\tools\modplus-revit-2027\source\*.decompiled.cs
```

`CLAUDEREVIT_MODPLUS_DATA` can point to another local analysis-bundle directory.
If a local catalog is present it takes precedence, after schema/version/hash/path
validation. Otherwise the embedded snapshot is used. The source tool accepts
module IDs, never arbitrary file paths, and does not compile or execute excerpts.

Catalog inspection works in supported Revit versions 2025–2027. **ModPlus execution
requires Revit 2027** and the corresponding installed plugin. The sum adapter
requires `mprSumParameters_2027` and `ModPlus_Revit_2027` already loaded by ModPlus;
it never loads vendor DLLs itself. Its exact module SHA-256 must match the catalog.
After a vendor update, refresh the local analysis catalog before using the adapter.

All executable calls require the bound project to be the active Revit tab. The sum
adapter also checks ModPlus's own current document. Explicit selections are
validated before execution and restored after reading, including errors and
cancellation. For a posted UI command the requested selection stays in place so
the queued command can read it; failed posting restores the prior selection.

The unchanged `ModPlusTools.cs` from the supplied archive is recognized by its
exact SHA-256 and superseded by the built-in tools at startup. Its file is preserved.
Edited extensions retain ordinary override behavior and follow the existing
arbitrary-code switch. The four fixed built-in adapters do not execute arbitrary
C# and follow the ModPlus native-group switch instead. The code-execution switch
continues to control C#/Python/custom tools.

## Examples

```json
{"query":"армирование","limit":10}
```

For `get_modplus_tools`, page a large module's commands with `module`,
`command_offset` and `command_limit`. For `get_modplus_algorithm`:

```json
{"module":"mprSumParameters","class_query":"Services","method_query":"GetSelectElements","line_count":60}
```

Class and method pages use `class_offset`, `class_limit` and `method_offset`.
For `modplus_sum_parameters`:

```json
{"element_ids":[12345,12346],"parameter_query":"Объем","precision":3,"coefficient":1,"limit":25}
```

The service's grouping, native unit conversions, text aggregation and counting
of type parameters **per selected instance** are preserved. Sum and scaled sum
are rounded independently after applying the coefficient. Long displayed text
is bounded and marked `textTruncated`; exact `sumExact`, `roundedExact` and `scaledRoundedExact` strings preserve precision through the transport's numeric rounding. Do not
treat per-instance type-parameter sums as a unique-type count or a material takeoff.

For `run_modplus_command`, use the exact installed `ClassName` from the catalog's
`installed` list. `preview` defaults true. Missing/ambiguous buttons, foreign
command IDs, wrong project tabs and unavailable commands are refused.

## Validation

350 unit tests and 41 isolated adapter checks passed locally. Adapter checks run
the production tool code and real Anthropic schemas with Revit-only test contracts:
catalog paging, nested ribbon matching, source bounds, unsupported versions,
cross-document rejection, selection handling, preview, unattended-command rejection
and queued-versus-completed status. They run in CI and before release packaging.

API compilation is checked for all supported versions. **Execution against a
licensed running Revit with ModPlus has not been verified in this release.** The
isolated checks cannot prove actual ribbon command IDs, loaded-module compatibility,
plugin-dialog completion or vendor service behavior on a real model.
