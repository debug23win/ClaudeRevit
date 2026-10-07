# Norm audit, Russian documentation, imports, coordination, versions and analysis

These tools arrived after v3.8.7. Several ideas came from forks of
[mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) (MIT):

- NewLevelHub: normative checks, ТЭП, explications, finish schedules.
- vietnguyen0603: PDF extraction with calibration.
- rezahanif and sky92archangel: clashes, IFC and Navisworks.
- nhantruong96: analytical model and loads.

Every implementation here is native and written for this add-in.

**Status:** all the tools compile for Revit 2025, 2026 and 2027. The pure logic has unit tests: rule thresholds, room classification, ТЭП arithmetic, numbering order, and PDF extraction, calibration and wall detection on generated PDFs. **None of these tools has been run in Revit yet.** Treat the first runs as acceptance tests, and keep `preview=true` (the default) until a result looks right.

## Norm audit (СП РФ)

`audit_norms` measures the model against these clauses. It is read-only.

| Rule | Code, clause | Requirement (general case) | How it is measured |
|---|---|---|---|
| `evac_exit_width` | СП 1.13130.2020 п. 4.2.19 | Clear width ≥ 0.8 m | A "Ширина в свету" or "Clear Width" parameter if the family has one, otherwise the nominal width (flagged as nominal) |
| `evac_exit_height` | п. 4.2.18 | Clear height ≥ 1.9 m | Same approach, using height |
| `corridor_width` | п. 4.3.3 | ≥ 1.0 m; 1.2 m when more than 50 people evacuate | Width of the rectangle that has the room's area and perimeter |
| `stair_run_width` | п. 4.4.1 | ≥ 1.2 m; 1.05 m for Ф1.3 up to 75 m; 1.35 m for Ф1.1, Ф2.1, Ф2.2, Ф3.4, Ф4.1 | Narrowest actual run width |
| `stair_riser_max` / `_min` | п. 4.4.3 | 5–22 cm | Actual riser height |
| `stair_tread_min` | п. 4.4.3 | ≥ 25 cm | Actual tread depth |
| `stair_slope_max` | п. 4.4.3 | ≤ 1:1 | Riser ÷ tread |
| `ceiling_height_living` | СП 54.13330.2022 п. 5.12 | ≥ 2.5 m; 2.7 m in IA, IБ, IГ, IД, IVА | Ray from the floor top up to a ceiling, floor or roof at the room point |
| `ceiling_height_circulation` | п. 5.12 | ≥ 2.1 m (in-apartment halls and corridors) | Same ray method |
| `area_common_living` | п. 5.11 | ≥ 16 m²; 14 m² in a one-room apartment | Revit room area |
| `area_bedroom` | п. 5.11 | ≥ 8 m² | Revit room area |
| `area_kitchen` | п. 5.11 | ≥ 8 m²; 5 m² in a one-room apartment | Revit room area |
| `railing_height_exterior` | п. 6.4.4 | ≥ 1.2 m | Top-rail height of the railing type, for railings not on a stair |
| `railing_height_interior` | п. 6.4.5 | ≥ 0.9 m | Top-rail height of the railing type, for railings on a stair |

### Room classification

The audit decides what a room is from its name. It recognises Кухня, Спальня, Общая комната/Гостиная, Коридор, Лестничная клетка, Прихожая and similar names, with English fallbacks.

### Which doors are checked

A door counts as being on an evacuation path in either of these cases:

- one side of the door has no room (an exit to the outside);
- one side is a corridor, hall or stair.

### Context and overrides

These inputs select the variant of a clause:

- `functional_class`
- `climate_subregion`
- `corridor_occupants`
- `apartment_parameter`, which enables the one-room-apartment variants

`overrides` sets project values, for example `{"corridor_width": 1400}`. Pass `list_rules=true` to get the catalogue.

### Limits

- **The audit is a screening aid.** The responsible engineer still confirms which norms apply, which edition is in force, and any exceptions.
- **Nominal door widths overstate clear widths.** Results measured from a nominal width say so.
- **Ceiling rays can miss.** A ray only sees what the 3D view used for the audit shows: the default `{3D}` view, or another non-perspective 3D view if there is none. Hidden categories or a section box make it miss, and the room's upper limit is reported instead.

### Optional sections

Two more rule sets run when you ask for them with `sections`:

- **`sp59`** (СП 59.13330.2020, accessibility):
  - `mgn_door_width`: clear width of a doorway accessible to wheelchair users ≥ 0.9 m (п. 6.1.5).
  - `mgn_corridor_width`: width of corridors and halls on paths of movement ≥ 1.8 m (п. 6.2.1).
  - `mgn_wc_width` and `mgn_wc_length`: universal toilet cabin ≥ 2.2 × 2.25 m (разд. 6.3). The audit finds these rooms by name: «универсальн», «МГН», «инвалид».
- **`fire_distance`** (СП 4.13130.2013, табл. 1): distances between this building and each linked building, and between the links.
  - Give `fire_classes`, for example `{"this": "II C0", "Корпус 2": "III C1", "default": "II C0"}`.
  - The distance depends on the pair of buildings: 6, 8, 10, 12 or 15 m.
  - Footprints are the convex hulls of the walls in plan. A hull is never farther away than the real outline, so a distance that passes really passes.

### Office rule file

You can change the rules without a new build. Put a JSON file at `%AppData%\ClaudeRevit\norm-rules.json`, or pass its path as `rules_file`. The file can:

- change thresholds (`overrides`);
- switch rules off (`disable`);
- add rules of your own (`rules`). An added rule picks one of the existing measurements (`check`), the rooms it applies to (`kinds` and/or `name_pattern`) and a `section`.

There is a commented example in [examples/norm-rules.json](examples/norm-rules.json). `audit_norms list_rules=true` shows the rule set in force and which file it came from. A broken file is reported with the reason, and the audit then does not run.

### Marking findings on drawings

`annotate_norm_findings` takes the audit's `annotate_input` and adds the following:

- a text note with a leader to each element;
- optionally, a revision cloud on the latest revision (it creates a "Нормоконтроль" revision if the project has none);
- optionally, the note appended to a text parameter.

## Russian documentation

| Tool | Output |
|---|---|
| `compute_tep` | ТЭП: footprint and construction volume (approximate, from floor slabs, and labelled as approximate), storeys, room area, living area, apartment area with and without summer spaces, and apartments by number of rooms. `render=true` draws the table. |
| `create_room_explication` | A live room schedule: number, name, area to 0.01 m², an optional category column, level subtotals and a grand total. |
| `create_floor_explication` | A drafting table: rooms per floor type, the layer build-up and the area. The floor under a room is the topmost slab whose top face contains the room point. |
| `create_finish_schedule` | A drafting table: rooms grouped by ceiling and wall finish. Ceiling area equals room area. Wall area is perimeter × height minus door and window openings. |
| `fill_title_block` | Writes stamp fields by parameter name, looking on the sheet first, then the title block, then (optionally) Project Information. |
| `fit_schedule_to_sheet` | Places a schedule inside the ГОСТ Р 21.101 frame above the stamp, splitting it into side-by-side segments when it is too tall. |
| `number_rooms` | Renumbers rooms in reading, snake or path order, with `{L}`, `{N}` and `{A}` tokens. It writes in two passes, so swapping two numbers never collides. |

The drafting tables are snapshots: re-run the tool with `replace=true` after the model changes.

## PDF plan → model

`pdf_to_model` reads a **vector** PDF attached to the chat. Scanned PDFs have no vectors to read.

1. Run `mode=analyze` with no calibration. It lists grid-label candidates and their PDF coordinates.
2. Calibrate with one of these:
   - two known points: `{points:[{pdf, model_mm}, …]}`;
   - a plot scale and an origin: `{scale:100, pdf_origin, model_origin_mm}`;
   - `{by_model_grids:true}`, which matches grid intersections by name and reports an RMS error.
3. Run `analyze` again. It now shows the detected walls (as parallel face-line pairs) with a thickness histogram, and the detected grids.
4. Run `mode=create` with a `level`, first with `preview=true`.
   - Each wall gets the basic wall type with the nearest thickness.
   - Grids that already exist in the model are skipped.

Use `region_pdf` to leave out the title block and legends. Detection is heuristic, so check the samples before creating anything.

## Coordination

- **`check_clashes`**
  - Set A is in this model. Set B is in this model or in a loaded link (`set_b.link`).
  - Candidate pairs come from bounding boxes and are confirmed by intersecting solids. Each clash reports the overlap volume and its centre.
  - Joined pairs and host/insert pairs are ignored by default.
  - `create_view=true` builds a 3D review view boxed to the clashes.
- **`export_ifc`**: IFC2x3, IFC4, IFC4RV, IFC4DTV or IFC4x3. Optionally filter by view, and pass extra exporter options through. The export runs in a rolled-back transaction, so the model is not changed.
- **`export_nwc`**: needs Autodesk's Navisworks NWC exporter. Exports the whole model or one 3D view, optionally with links, using shared or internal coordinates.

## Structural analytical model

- **`get_analytical_model`** lists:
  - analytical members and panels, and the physical elements they are associated with;
  - physical elements that have no analytical counterpart;
  - load cases with their load counts;
  - supports.
- **`create_analytical_model`** creates and associates:
  - members for framing and columns;
  - optionally, panels for structural floors and walls.
- **`create_structural_loads`** creates point, line and area loads, either on analytical hosts or free-standing.
  - Units are kN, kN/m, kPa and kN·m. Gravity is −z.
  - A missing load case is created along with its nature.
- **`create_boundary_conditions`** adds fixed, pinned or roller supports at member ends, or at every column base.

## Undo, safe bulk writes, change impact

- **`undo_last`** rolls back the agent's whole last action in one call: every change made while answering the last request, or the last `steps` calls.
  - Revit cannot merge transactions after they are committed, so this replays Revit's own Undo once per step, newest first.
  - Every undone step must be one of the agent's transactions (named "Claude…"). If a manual edit by you is on top of the undo stack, it is restored with Redo and the undo stops.
  - The document must be the active tab. `list=true` shows the recent actions. Revit's Redo brings undone steps back.
- **`plan_token`.** Every preview now returns a `plan_token` and what the preview changed.
  - If you pass the token with `preview=false`, the call applies only when its inputs and the model are unchanged since that preview. Any edit in between invalidates it: another agent edit, a manual change or an undo.
  - The result then reports planned against actual counts of added, modified and deleted elements.
  - Calls without a token behave as before.
- **`survey_change_impact`** lists what an edit would also touch:
  - hosted inserts and rebar;
  - tags and dimensions;
  - geometry joins;
  - groups;
  - pinned elements;
  - elements borrowed by others;
  - instances of a type.

## Imports and versions

- **`dwg_to_model`** reads a linked or imported DWG by layer:
  - grids: named 1, 2, 3… and А, Б, В…, because DWG text is not readable through the API;
  - columns: rectangles and circles;
  - beams: pairs of parallel lines.

  It matches or creates sized types. Run `analyze` first, then `create` with `preview=true`.
- **`ifc_to_native`** rebuilds DirectShapes from an opened IFC or an IFC link (Renga, Tekla, nanoCAD BIM) as native walls, floors, columns and beams.
  - Elements that are not box-like enough are skipped, and the skip is reported.
  - Each new element's volume is compared with the original.
  - Openings are not rebuilt.
- **`snapshot_model`** and **`compare_model_versions`**: take a snapshot at each issue, then compare. The comparison lists what was added, deleted, moved or retyped, and which parameters changed, by category, with element ids.
- **`check_model_package`** checks a set of models before issue: all open projects, or a folder of RVT files opened detached and closed without saving. It looks at:
  - warnings;
  - room problems;
  - missing or unloaded links;
  - imported CAD;
  - in-place families;
  - purgeable elements;
  - empty required Project Information and sheet fields;
  - unsaved changes;
  - whether all models share the same site location and survey point.

## Refusal fallback and other API changes

- **Server-side fallback.** Requests to Claude Fable 5, Fable 5.1 and Opus 5 send `fallbacks: "default"`.
  - When the safety classifier declines a request, Anthropic's recommended fallback model answers it, and the transcript says which model answered.
  - Usage is billed at the rate of the model that served the request.
  - Switch it off under Settings → General.
  - If the API rejects the parameter for a request, that turn is retried without it, and the session stops sending it.
- **`eager_input_streaming`** is on for the code tools (`execute_csharp`, `run_dynamo_python`, …), so a long script doesn't stall the stream.
- **`strict` tool schemas are not used.** They require `additionalProperties: false` and every field to be required, which would mean rewriting about 280 schemas full of optional fields. The tools already validate their inputs and return typed, named errors.
