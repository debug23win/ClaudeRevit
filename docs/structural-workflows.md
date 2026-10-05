# Native components, keyed nodes and live SPDS specifications

The v3.8.3 baseline and the development additions below use native **ViewSchedules**, with data from model
parameters. No text-note or drafting-table snapshots are used as specifications.

## Before modelling

The API and subscription/MCP agent instructions require inspection and one bundled
clarification of unresolved requirements before the first mutation. Reuse answers
already present in the conversation or project memory. Clarify dimensions and
clearances, native structural representation, materials, editable section drivers,
design versus reconstruction, KM/KMD/KD scope, table forms, standard edition, source
scope (including nested-instance counting), units, grouping, installed font and
sheet placement. Read-only inspection can continue while waiting for answers.

`inspect_structural_capabilities` lists loaded structural profiles, actual native
connection types and schedules. Prefer native beams/columns and compatible detailed
`create_structural_connection` types. The Steel ribbon alone does not establish
that a compatible detailed type is loaded. A Generic Model box/angle family is
not a native structural member. A generic connection links members logically and
does **not** create plates or bolts. Missing resources require a clarified choice
between loading compatible resources and authoring custom parametric components.

## Geometry-driving family parameters

`create_parametric_section` works in an active ordinary RFA and builds a rectangular,
box, I, channel, angle, circular tube or paired-timber extrusion. Width/height/web/flange/gap dimensions drive reference-plane and
sketch constraints; formulas drive inner coordinates; Length drives the extrusion.
Each independent driver is tested at 0.8 and 1.2 times its nominal value, with actual
solid bounds, origin and volume checked against the analytical section area times length. A failed
check rolls back the whole operation. Default `preview:true` rolls back a valid
test too; use `preview:false` to commit. The template category is preserved.

The family needs a suitable parallel 2D view. Circular tubes use native arcs and radial
dimensions. `material_id` associates the family Material parameter with the extrusion;
optional explicit `density_kg_m3` adds physical Area/Density/Mass formulas. Density
does not automatically follow a later Material change. `placement_length_parameter`
can link Length to an inspected existing template instance driver, also independently
flexed. The tool does not infer the appropriate placement parameter. Check template
reference planes, beam behaviour, materials and loading before placing it as framing.

For every custom family component, use `flex_family(require_geometry_change:true)`
with independent minimum/maximum driver scenarios. A parameter that changes without
changing actual tessellated solid geometry fails this check. This detects ineffective
drivers; it is not proof that every dimension changed correctly. Volume and detailed
dimension checks still matter. Existing non-parametric families are not repaired
automatically: their constraints and formulas must be authored and tested.

`change_element_type` preserves compatible writable **instance** parameters by
identity and data type, then checks them again after regeneration. Supply
`preserve_parameters` for critical names: an unavailable/unrestorable value rolls
back that item. Type properties follow the selected type. Replacement element IDs
and changed/restored parameters are reported. Preview is optional, false by default.

## Whole connection nodes

`plan_truss_layout` generates a consistent global two-frame topology across spans,
including continuous diagonal parity, bracing, staggered modular splices and explicit
end remainders. It computes no section sizing, loads or structural optimum.

`upsert_connection_node` stores a keyed assembly manifest **inside the RVT**. A part
can bind an existing instance, or place a loaded unhosted point family. Native curve
members and hosted families must first be placed through their dedicated tools.
Placement may use global mm coordinates, a parent part's actual local transform or
a stable planar face reference. Parameters, orientations and void-cut targets are
part of the manifest. Keys update existing parts instead of duplicating them.

Defaults are `preview:true` and `require_valid:true`. Preview with
`require_valid:false` exposes failed declared checks; committing with the same flag
explicitly accepts them. Omitted parts require `remove_missing_parts:true`; deletion
that cascades to unmanaged dependents is rejected. `expected_revision` catches stale
updates. External edits/deletions are visible in `get_connection_node`.

New nodes default to `auto_update:true`. The required updater follows declared
parent/face placements and Length mappings such as
`value_from:{part_key:"beam",name:"Height",scope:"type",scale:1,offset_mm:10}`.
Sources must be actual unique compatible parameters; dependency cycles are rejected.
Existing legacy nodes retain manual refresh until explicitly enabled. Automatic
refresh never creates replacement parts or deletes them. A missing part, invalid
interface or direct edit to a managed child marks `needs_refresh` and preserves the
last accepted state. Reapply the saved node (same `node_key`, omit `spec`) deliberately
and run `validate_connection_node`. Relative points follow the parent frame; supplied
orientation axes remain explicit global axes. Test rotated member cases separately.
The manifest is reusable for other family assemblies, not only bridge joints.

Checks use actual solids and transforms: declared clashes, bounded planar contact,
bolt/hole axes, actual diameter/grip parameters, conservative stack envelopes and
forbidden splice zones. A recorded void-cut relationship is not geometric proof of
a bore. Declared bolt/stack rules also inspect actual internal cylindrical faces,
coaxial axes, bore diameter and full solid-envelope penetration. Unsupported/faceted
geometry remains incomplete; clearance, threading and capacity are not certified.
Geometry, interfaces,
calculation evidence and documentation evidence have separate readiness states;
calculation/documentation annotations are not verified engineering results.

## Live SPDS tables

`get_spds_table_profiles(category)` inspects actual schedulable field IDs and existing
project schedules. Reuse the approved template first. Profiles implement these forms:

| Profile | Form | Nominal widths, mm |
| --- | --- | --- |
| `steel_rollup` | GOST 21.502-2016, Annex L, form 2 | 30/30/30/10; 15 per construction group; 25 total |
| `timber_materials` | GOST 21.504-2016, form 1 | 105/15/15/50 |
| `timber_elements` | GOST 21.504-2016, form 2 | 15/65/15/20/20 |
| `scheme_specification` | GOST R 21.101, form 7 | 15/60/65/10/15/20 |

Choose `standard_edition:"2026"` or an explicitly agreed legacy `"2020"`. GOST R
21.101-2026 applies from 1 April 2026 and replaces 2020. Steel and timber drawings
also require their specific standards; the general specification form alone does
not replace the steel-consumption or timber-material statements.

`create_spds_schedule` maps native fields directly, optionally duplicating an approved
existing schedule. It sets headings, widths, units/accuracy, fonts/alignment,
filters and grouping. Every mapped field must resolve uniquely. It supports timber
and scheme forms; it does not create a steel pivot matrix.

`create_spds_table` creates live schedules with normalized calculated **model**
parameters. It supports the seven categories returned by its schema, either one
category or `category:"multi_category"` with `categories:[...]` and optional
`category_mappings`. Other categories use direct native fields through `create_spds_schedule`.
It reads explicit instance/type parameter mappings; numeric legacy mass fields need
explicit kg/t, while physical Mass fields retain dimension identity. Material amounts
need compatible m/m²/m³ mappings. Unit mass/amount is multiplied by quantity (default
one actual instance). Mixing parent and shared nested-child quantities must be agreed
before selecting sources. Required `nesting_policy` is `explicit`, `parents_only`
or `children_only`; filtering considers ancestors/descendants actually in the agreed
source scope and handles more than two nesting levels. Shared-nesting catalog
descriptions alone do not establish how quantities should be counted.

`mass_kg:{source:"material_volumes"}` uses actual native material volumes and physical
StructuralAsset density, without assuming 7850 kg/m³. It requires quantity 1 because
volume already describes the whole source instance. Missing material density/volume
marks the live data incomplete. Material/physical-asset edits trigger recalculation.

```json
{
  "profile": "steel_rollup",
  "standard_edition": "2026",
  "table_key": "KM-steel-main",
  "category": "OST_StructuralFraming",
  "nesting_policy": "explicit",
  "scope": "selected",
  "source_ids": [12345, 12346],
  "construction_groups": ["Фермы", "Связи"],
  "field_mapping": {
    "profile": {"parameter_name":"Профиль", "scope":"type"},
    "grade": {"parameter_name":"Марка стали", "scope":"type"},
    "size": {"parameter_name":"Размер профиля", "scope":"type"},
    "group": {"parameter_name":"Группа конструкций", "scope":"instance"},
    "mass_kg": {"parameter_name":"Масса", "scope":"instance", "unit":"kg"}
  },
  "name": "КМ Ведомость расхода стали",
  "font_name": "Arial",
  "preview": true
}
```

IDs and parameter names above are illustrative: inspect actual definitions/GUIDs
first. Text constants are only for explicitly agreed invariant classifications.
`scope:"selected"` retains the chosen source identities and live values; newly placed
instances join automatically only with `scope:"entire_category"`. Duplicate table
keys are rejected. Matrix construction-group columns are explicitly agreed and fixed;
an unknown later group marks the data incomplete. At most 16 derived tables and
10,000 category instances are supported per document.

Steel masses remain unrounded physical model values. Revit totals them and displays
tonnes at 0.1 accuracy. Profile/grade subtotals and a grand total are native schedule
totals. A second **live** schedule gives overall totals by metal grade; place it below
the main schedule on the sheet. Native row grouping keeps different unit masses and
measurement units separate, and source data changes update grouping/positions.

The persistent Revit updater recalculates after instance/type edits, additions or
deletions, within the same transaction and Undo. It does not hold an open transaction
between agent calls. Private `CR_SPDS_…` parameters never replace ADSK/BIMStarter FOP
identities. Mapping to derived SPDS fields is rejected to avoid update cycles.
The add-in must remain available for derived columns: Revit's required-updater warning
is enabled if it is missing. Directly mapped native schedules do not need that updater.

Invalid sources clear numeric derived values and expose an incomplete-data title and
error column, rather than retain stale totals. Such a table cannot be issued. Correct
source data to resume normal rendering. Source Mass must itself be a valid model
calculation/formula if it is expected to follow changing geometry; copying an old
number into a Mass parameter does not make that source value correct.

Use `place_view_on_sheet` and `export_image` to inspect wrapping, borders, font,
split/repeated headers, totals and the title block before issuing drawings. These
profiles do not certify the entire drawing set or project requirements.

`create_spds_schedule(category:"multi_category",include_linked_files:true)` can
include linked rows using compatible fields already present in those links. It does
not write derived parameters into read-only links. Derived pivot matrices currently
operate on the active document. `audit_spds_schedule` checks actual fields/specs,
widths/totals, source nesting, sheet margins and overlaps with schedules, viewports
and text. Visual exports are still required for row wrapping, fonts and repeated headers.

## Verified experience and resumable jobs

`verify_model_result` checks actual solids, parameters and declared native Rebar hosts.
Attach a successful document-local `journal_run_id` only for IDs actually changed by
that run and include a geometry check. Failed/incomplete reports remain diagnostic;
only passed reports can be retrieved as compatible experience. `get_verified_experience`
filters by exact Revit build and current family/type/parameter context. The prompt has
only an optional evidence index; it does not force reuse or certify structural design.

`run_checkpoint_job` stores step outputs, revision and signatures in RVT DataStorage
with the same bounded transaction group as the edits. Default preview rolls back.
Native transactional steps can reference earlier outputs by
`{from_step:"level",json_pointer:"/id"}`. Each committed batch is one Undo; no transaction
persists between MCP calls. Repeating completed jobs is a no-op. Changed plans,
revisions, tracked transforms/parameters/bounds or missing IDs block continuation.
Scripts, lifecycle/deletion tools and unsupported nontransactional tools are excluded.
Signatures do not constitute a full geometric equivalence proof; use independent
result checks for acceptance. Save the RVT to retain checkpoints after a process crash.

`inspect_family_files` opens up to ten local RFA references in background documents,
reports native parameters/types/nesting and optionally flexes supplied scenarios,
then closes without saving or upgrading the originals. Never redistribute downloaded
families without the owner's applicable license. Native acceptance for the development
additions is pending; see [reference cases](bimstarter-reference-cases.md).

## Journal and execution

Code execution is always enabled. The settings checkbox and persisted opt-out were
removed. Legacy settings and a disabled code group cannot hide or block scripts or
saved tools; the old interface marker remains only for compatibility. Optional
confirmation applies to destructive native operations, not script permission.

`validate_csharp` and worker compilation now record duration, success and compiler
errors in `task_journal.jsonl`. Task completion separately reports `worker_seconds`,
Revit execution and queue time. Concurrent worker duration can overlap: it is not
an additive decomposition of wall time. Prefer native tools, validate before changing
geometry, and do not retry an unchanged compiler error.

Automated checks cover pure topology, contract validation, section areas, coaxial
axes/zones, SPDS aggregation/live values, code availability, MCP transport, chat and
subscription CLI behaviour. Native family constraints, connection solids and schedule
updater/rendering still need an actual Revit acceptance run; compilation alone does
not verify those runtime behaviours.

Sources: [GOST R 21.101-2026](https://protect.gost.ru/gost/details/17bc12e8-6579-4145-b141-56855e772e7f),
[GOST 21.502-2016](https://protect.gost.ru/gost/details/57d18a56-0d60-4071-8f93-053f789060ad),
[GOST 21.504-2016](https://protect.gost.ru/gost/details/b4a24268-5643-4322-b59f-b60288da0806).
