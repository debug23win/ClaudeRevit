# Parameter modelling and diagnostics

Prefer parameter generators for repeated building geometry. Discover their exact schemas once, preview a representative batch and commit with `preview:false`. Each completed batch has one Undo entry. No transaction is held open while an agent thinks or between MCP calls. Stop is cooperative: generators check between elements, and C# snippets can call `ScriptRuntime.CheckCancellation()` / `ScriptRuntime.ReportProgress(1, 3, "phase")`. An uninterrupted synchronous API call must finish before cancellation can settle.

`generate_floor_stack` uses millimetres throughout its table:

```json
{
  "floor_type_id": 123,
  "preview": true,
  "structural": true,
  "storeys": [
    {"level_name":"Tower 01","elevation_mm":0,"contour_mm":[[0,0],[10000,0],[10000,8000],[0,8000]]},
    {"level_name":"Tower 02","elevation_mm":4000,"scale":0.9,"rotation_deg":5,"contour_mm":[[0,0],[10000,0],[10000,8000],[0,8000]]}
  ],
  "provenance": {"sources":["project drawings"],"dimensions":[{"name":"storey height","value":4000,"unit":"mm","status":"assumed"}]}
}
```

Replace `123` with an actual loaded FloorType ID. Each contour closes automatically; optional `holes_mm` supplies interior contours. Put common `contour_mm` / `holes_mm` at the root to avoid repeating them for every storey; a row can override them. Revit short-curve tolerance, intersections, hole placement and level names are checked before mutation. `snap_tolerance_mm` defaults to zero; requesting up to 10 mm aligns nearly axial edges and returns the maximum vertex displacement. Limits are 1,000 storeys and 2,000 vertices per contour.

`generate_facade_grid` requires a loaded, non-hosted OneLevelBased FamilySymbol and level. Points are `origin_mm + column_step_mm * column + row_step_mm * row`; up to 5,000 instances are created in a batch. Hosted curtain panels still need their host tools. `generate_spire` takes `profile_mm:[[radius,z],...]` with increasing elevations, and optional `origin_mm:[x,y,0]`. In a project its result is geometric DirectShape, so it is not a structural column. In a family it creates a native editable Revolution. Pass a previous generated `element_id` to replace it atomically; the returned ID changes.

`set_model_provenance` stores source references, measured/assumed/unknown dimensions, related member IDs and notes. It preserves generator parameters. `audit_model_provenance` reports representation and annotations; an annotation is not independent proof of geometry or an engineering design check.

`validate_csharp` performs compilation only. `execute_csharp` also compiles before starting a transaction; use `System.Text.Json` for JSON. Assemblies that embed a conflicting Newtonsoft.Json definition are aliased away from the ordinary Newtonsoft namespace. Compilation cannot detect every Revit runtime/API error.

`export_image` exports a selected view through Revit and returns verified PNG dimensions and pixels to the agent. An all-black export is an error; a uniform non-black image carries a warning. It supports `.png` paths and does not depend on desktop screen capture.

The pane's **Copy log** includes the current project's recent task journal, diagnostic log tail and chat. `%APPDATA%\ClaudeRevit\task_journal.jsonl` contains task IDs/document keys, native tool timing and complete category/element deltas. Added/deleted counts are final changes, and previews/rolled-back calls record no committed changes. Modified counts describe touched existing elements, not a semantic comparison of every parameter. Timing includes instrumentation; queue time is separate from native execution. Exports can leave files after model rollback.
