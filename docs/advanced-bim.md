# Native BIM tools and subscription benchmark — v3.7

The release registers 213 tools. New API operations are independently implemented;
they do not embed BIMStarter DLLs or copy its command implementations.

## Precise modelling operations

| Need | Tool | Contract |
|---|---|---|
| Reference-based dimensions | `get_dimension_references`, `create_dimension_from_references` | Original stable references; geometry coordinates transformed into document space. Pass the returned document key and a line/arc in mm. Linear dimensions work in projects/families; angular/radial/arc-length creation is limited to ordinary Family Editor. |
| Review actual deletion cascade | `delete_elements_checked` | Default committed/rolled-back preview includes dependents. Apply requires its document key and complete expected cascade; changes to the cascade cause rollback. |
| Material takeoff | `get_material_quantities` | Native volume m³, base area m² and painted area m², separately. Totals cover the current page; accumulate all pages. No estimate for objects without quantities. |
| Native beam system | `create_beam_system` | Closed planar boundary, loaded framing type, level, direction edge, fixed spacing and justification. |
| Parameter colors | `color_elements_by_parameter` | Categorical palette or numeric gradient; GUID/name resolution, optional type fallback, legend rows and element counts. Legend data is returned in JSON; a Revit legend view is not automatically created. |

Creation/edition tools with `preview` default to **true**. They commit a native
transaction to expose Revit validation, then roll back their transaction group.
Preview IDs for newly created objects are omitted. After reviewing the result, repeat
with `preview=false` to apply. These tools have their own undo boundaries; a family
inspection/document transition can create separate undo entries. Ordinary API
operations have one Undo boundary per completed call/batch; no transaction/group spans asynchronous inference.

## Complex families

1. `get_project_standards` and `get_shared_parameter_catalog`: inspect the target
   template's GUIDs, specs, bindings, tags and schedules before authoring.
2. `open_family_editor`: open an RFA, create from an RFT, or save a loaded family's
   editable copy to a new absolute RFA path. Existing files are not overwritten.
3. `analyze_family_structure`: recursive nesting, shared components, hosting,
   child→parent associations, formulas and inferred dependencies, types, reference
   planes, dimensions, forms/combinations, imports and image underlays. Reported
   IDs belong to each inspected family document. Child documents close unsaved;
   limits/uneditable children are explicit.
4. `add_shared_family_parameter`: locate an actual external TXT definition by GUID,
   preserving shared identity/spec/instance scope. Ordinary `add_family_parameter`
   is appropriate for local geometry drivers, not replacement shared parameters.
5. `create_family_form`: extrusion, blend, sweep, swept blend or revolution;
   solid/void; line/three-point-arc profiles and paths; existing family parameter
   associations. Rectangular extrusions can create pinned reference planes,
   locked edge alignments and labelled width/height dimensions.
6. `combine_family_forms`: native solid/void combinations. `place_nested_family_instance`
   places unhosted or explicit face-hosted children and associates their parameters.
7. `flex_family`: existing types or explicit dimension/visibility/type scenarios;
   commit/regenerate/probe, then roll each scenario back. Review failures, warnings,
   visible solids and bounds. Save/load into the target project only after testing.

Example in an ordinary RFA with a plan view parallel to XY:

```json
{
  "kind": "extrusion",
  "rectangle_mm": {"width": 600, "height": 400},
  "depth_mm": 120,
  "width_parameter": "Width",
  "height_parameter": "Height",
  "preview": true
}
```

To drive depth too, create a local length parameter first and pass
`parameter_bindings: {"EXTRUSION_END_PARAM":"Depth"}`. Profiles are world coordinates
for extrusion/blend/revolution; sweep/swept-blend profiles use local XY at z=0 and
their paths use world coordinates. A swept blend currently takes one path curve.

```json
{
  "scenarios": [
    {"name":"small", "values":{"Width":300,"Height":200}, "require_solid":true},
    {"name":"large", "values":{"Width":1200,"Height":800}, "require_solid":true}
  ]
}
```

Flex values: length mm, angle degrees, area m², volume m³; other doubles use Revit
internal units, booleans yes/no and material/type values are IDs. General
`set_parameter` retains its internal-unit double contract. The flex probe sums
visible solid volumes; it is not a Boolean union, clearance check or proof that
every possible size/option works. Explicitly test extreme dimensions, one-element
arrays, hidden options and alternative nested types.

## Complex native reinforcement

`create_rebar_geometry` creates actual Rebar objects, not visual DirectShapes or
nested IFC families:

- Shape-driven ordered line/arc contours, including stirrups, with hook types,
  start/end orientations, and end treatments in Revit 2026+.
- Layouts: single, number with spacing, fixed number, maximum spacing and minimum
  clear spacing; distribution side and first/last inclusion.
- Free-form explicit curve loops for individual bars, including different bar
  shapes. The tool has no custom free-form update server and does not promise
  automatic rebuilding when a host changes.

`get_rebar_constraints` reports handles, current/preferred constraints and actual
host/rebar candidates. `set_rebar_constraint` rechecks document/handle/candidate
keys before selecting a preferred constraint or changing a native offset.
`create_rebar_coupler` uses native Revit validation for two bar ends or an end cap.
These tools do not certify cover, lap length, collisions or structural adequacy.

`bimstarter_model_tools` edits layouts/display, explodes **uniform shape-driven**
sets into native single bars, removes area systems while retaining native bars,
and performs explicit parameter propagation, joins/cuts/coping, beam-end joins,
view-template/filter operations and schedule refresh. Varying/free-form explosion
and nontranslation bar transforms are rejected rather than approximated.

`create_rebar_schedule_images` renders actual centerlines, curve lengths and total
length to unique PNGs, imports native ImageTypes and optionally sets an existing
writable IMAGE shared parameter by GUID. Preview removes both imports and its new
temporary files. Nonplanar bars are labelled as projections; these sketches are
not certified manufacturing/GOST drawings.

## BIMStarter command coverage

`get_bimstarter_tools` indexes **63 external commands** in
[Tereami/BimStarter](https://github.com/Tereami/BimStarter/tree/8447492db5292307b4bcf19919b1d6605e734a43),
by Aleksandr Zuev. At this snapshot: 10 native equivalents, 26 native workflows,
19 partial workflows and 8 interactive plugin commands. Every referenced native
tool is registered. The catalog separates source commands from installed buttons.

Partial coverage means a related workflow, not complete parity. Specialized
tables/drawings, selected geometry automation and library/cloud/account actions
can require the actual plugin. `audit_bimstarter_families` compares local RBS_GUID /
RBS_VERSION metadata and duplicate identities; it does not claim to know the
latest cloud version.

`run_bimstarter_command` checks a live compatible BIM-STARTER ribbon button and
native command postability. Its default preview checks only. Apply queues the
command; `posted=true` means **queued**, never completed. The plugin may require
user interaction. Wait for its dialog to finish, then inspect the resulting model.
If ribbon-derived command IDs are unavailable, use that button's exact journal ID
or its native alternative. Source-only commands are not callable installed buttons.
Posting is disabled during unattended benchmarks. Old .NET Framework plugin DLLs
are not assumed compatible with Revit 2025–2027.

The upstream repository declares CC BY-SA and many files carry CC BY-NC-SA headers.
See its [license](https://github.com/Tereami/BimStarter/blob/8447492db5292307b4bcf19919b1d6605e734a43/LICENSE.MD)
and individual files. ClaudeRevit includes factual command metadata and independent
native API implementations; source code/DLLs from BIMStarter are not redistributed.
[AYDrafting](https://www.aydrafting.com/aidrafting-russia) was reviewed as a feature
reference, not incorporated code.

## Benchmark

Both modeller and judge default to **Codex/ChatGPT subscription**. Each has an
independent provider/model/effort choice. Codex discovers the actual available
catalog; Claude Code accepts aliases or an explicit version. API is a separate
explicit choice; use `alt` / `alt:<model-id>` for the configured alternative API.

The modeller uses the pane's existing ephemeral ChatService/MCP channel, model
validation and cancellation/drain path. The subscription judge has no MCP or
shell/web tools. Claude Code also disables unrelated MCP/settings sources while
retaining OAuth; `--bare` is never used because it disables OAuth. API/cloud
environment credentials are excluded from subscription child processes. API or
Console login is refused as subscription auth, and errors do not trigger API fallback.

The judge receives objective before/after probes with complete scalar counts and
array totals plus bounded samples. Invalid replies/unavailable probes are ungraded.
JSONL records backend, requested model, effort, models used, judge choices, budgets,
tokens, time and reason. Keep judge model/effort fixed for comparable runs.

v3.7.2 adds quality/speed/total points and ten complex tasks (29 total), with
independent native family flex evidence. Run project tasks in a scratch RVT and
family tasks in an already open ordinary RFA. Missing document/seed prerequisites
are skipped without points. Each task uses a separate file copy of a saved local
non-workshared seed. That copy is closed unsaved and the seed is restored after
the task or cancellation. External exports remain. See the [benchmark guide](benchmark.md)
for scoring, task references, fixtures and evidence limits.

## Validation and limits

- 152 pure tests: parameter identities/editions, model selection, cleanup protection,
  probe deltas, quality/speed scoring, prerequisites, runner cancellation/reset,
  judge parsing, gradients and existing logic.
- Real WPF pane/benchmark checks with isolated backends: defaults, independent
  choices, typed models, busy/stop controls, project switching and narrow layouts.
- Live local HTTP MCP and stdio bridge checks with isolated clients.
- Real backend sources exercised through fake CLI child processes: subscription
  auth, exclusion of API environments, model routing, tools, errors, UTF-8 and cancellation.
- Actual Codex/ChatGPT no-tools calls succeeded for GPT-6 Astra and GPT-5.6 Luna.
  The tested Claude account reported subscription access disabled by its organization;
  that error is surfaced accurately, with no API fallback.
- Revit API builds for 2025–2027. Modelling, nested-family flex, new native geometry
  and interactive BIMStarter posting still require execution checks inside Revit.
  Revit 2027 was subsequently found on a custom drive; the earlier installation
  inventory missed it. The updated installer corrects that detection failure.

The source PDFs and authenticated RFA library files are not bundled. EIR auditing
is partial and does not certify customer compliance; see [Samolet EIR](samolet-eir.md).
