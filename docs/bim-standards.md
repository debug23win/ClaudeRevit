# BIMStarter, ADSK and complex family authoring

## Available in v3.6

The pane sends a small, current standards summary with every prompt. Both API and MCP
agents receive guidance to inspect the live template before changing its parameters
or schedules. Two core tools are available immediately:

- `get_project_standards`: project/family shared parameter GUIDs; actual Forge data
  types and unit formats; instance/type bindings and category IDs; view templates;
  browser organization; schedule IDs. Pass `schedule_id` for fields (including hidden
  fields and shared GUIDs), filter values and sorting. Parameter results support
  `query`, `offset` and `limit`. Schedule/template lists are capped at 120.
- `get_shared_parameter_catalog`: search definitions by name, group, description or
  GUID. Reads the bundled BIMStarter reference and the FOP configured in Revit, or an
  explicit `file_path`. Returns source information, original legacy data-type tokens,
  file hashes and malformed/duplicate-definition warnings. It reads the source;
  it does not change Revit's shared-parameter setting.

`get_element_parameters` retains same-name parameters with different identities and
returns parameter IDs, shared GUIDs and specs. `get_family_parameters` reports shared
GUIDs too. `set_parameter` accepts `parameter_guid` for a shared parameter; ambiguous
names are rejected instead of choosing the first match. Values keep the tool's existing
unit contract: double values use Revit's internal units.

**Identity is a GUID.** A name/prefix is a hint; a translated parameter can have the
same GUID. A definition in the FOP is not evidence of a category binding in a project.
Preserve the binding, data type, shared identity and template-controlled values.
`add_family_parameter` creates an ordinary family parameter, so it is not a replacement
for an existing shared parameter used by a tag or schedule.

## Verified BIMStarter FOP metadata

The source files found in the local BIMStarter installation are `Weandrevit 2020.txt`
(299 definitions) and `Weandrevit 2020 ENG.txt` (234 definitions). Together they contain
313 unique GUIDs. They are distinct snapshots rather than identical translated lists;
220 GUIDs occur in both, with matching data-type tokens. The embedded reference contains
their factual definition metadata. File hashes and provenance are in
[`provenance.json`](../ClaudeRevit/Standards/provenance.json).

The groups cover dimensions, project organization, title blocks/project information,
arrays, reinforcement, materials, marking, views, steel, embedded parts, combined marks,
bent reinforcement and architecture. Group 10 contains parameters imported from ADSK;
it is not a complete official ADSK FOP2021 catalog.

Examples from the actual Russian FOP:

| Parameter | GUID | Original data type |
|---|---|---|
| Орг.ГлавнаяДетальСборки | 7b011a82-6ead-45ee-a188-7a7721dfb452 | YESNO |
| Орг.ИзделиеТипПодсчета | 68df3fae-3e98-429a-a168-1b6f7a1485b0 | INTEGER |
| Мрк.МаркаИзделия | 92ae0425-031b-40a9-8904-023f7389963b | TEXT |
| Мрк.МаркаКонструкции | 5d369dfb-17a2-4ae2-a1a1-bdfc33ba7405 | TEXT |
| Арм.КоэффициентНахлеста | 7e6d8905-bc85-4621-8403-f4d460411b89 | NUMBER |
| О_Масса / Cmn_Weight | 32989501-0d17-4916-8777-da950841c6d7 | NUMBER |

The last row matters: its description says kilograms, but its original type is NUMBER.
Another parameter, О_МассаКг, uses MASS. Treating both as the same spec can break formulas
or formatting. Runtime inspection reports the actual current Revit spec.

The author's documented 2020 counting scheme assigns one main component per product,
uses count-type 1 for cages and 4 for embedded parts, and identifies product members
with a common product mark containing a hyphen. Shared nested components carry the
counting parameters. Native groups/assemblies alone do not establish this accounting.
Check the live filters and scope before applying that scheme to a modified template.
[Author's explanation](https://weandrevit.ru/armaturnye-karkasy-i-zakladnye-v-shablone-weandrevit-2020/).

The 2022 template update also describes paired pylon model/detail families and
corresponding schedules, and changes handling of free-form reinforcement properties.
These dependencies make geometry-only reproduction insufficient.
[Template update](https://weandrevit.ru/obnovlenie-shablona-weandrevit-2020-2/).

## ADSK orientation and limits of the evidence

The developer's manual explains that the 2.0 template package contains FOP2019,
FOP2021 and ADSK-ФОП.xlsx. Common project definitions come from mandatory, optional
and title-block groups; categories and instance/type scope are part of the binding.
The complete official TXT/XLSX and actual ADSK RTE were not available for direct
inspection in this session. Their GUIDs are not fabricated or copied from an unverified
mirror. Configure the actual FOP in Revit or pass its path to the catalog tool.
[FOP manual](https://manual2021.bim2b.ru/1-obshhaya-instrukcziya-po-rabote-s-shablonam/1-1-nachalo-raboty/rabota-s-fajlom-obshhih-parametrov/).

ADSK_ names are reported as hints, not a verified standard edition. An exact GUID
match against the configured source is reported separately. Templates commonly
organize views using ADSK_Назначение вида; a view template may lock this value.
Inspect the current browser organization and template instead of renaming everything.
[View organization](https://manual2021.bim2b.ru/category/1-obshhaya-instrukcziya-po-rabote-s-shablonam/1-1-nachalo-raboty/).

Upgrading a 2020/2022 template into a supported Revit version does not make it a
different parameter standard. The plugin builds for 2025–2027; it does not install
into Revit 2020/2022. The original BIMStarter RTE was located, but its complete live
Revit contents were not extracted here. The new tool reads the actual upgraded
project/family when used inside Revit.

## What the BIMStarter family examples show

The analysis below uses public cards and the author's template documentation.
Authenticated RFA downloading was unavailable because the browser automation runtime
failed, even after the user logged in. Internal RFA formulas, constraints and flex
behavior have therefore not been verified.

| Example | Published structure | Implication for an authoring agent |
|---|---|---|
| [Column with end-zone densification](https://www.bim-starter.com/family?guid=1c22d159-e8e3-4e34-96df-f0940dd11ec6&version=4) | Nested stirrups and multiple vertical bar forms; top/bottom/middle zones | Separate geometry, zone distribution, parameter propagation and end conditions; exercise short columns and one-zone cases. |
| [Rectangular reinforced column](https://www.bim-starter.com/family?guid=f49b63eb-e5f4-4f5e-b21b-0293d4f9ebcb&version=9) | Parametric reinforcement, four/eight vertical bars, stirrups; published fixes include anchoring and diameters | Flex test both bar-count modes, lap/anchorage variants and diameters. |
| [Pylon with openings and embedded parts](https://bim-starter.com/family?guid=c8efa431-f671-4d80-a398-ea423a2bf418) | Nested bars, plates, anchors and embedded parts with visibility controls | Build a dependency graph; test cut geometry, visible/hidden options and nested materials independently. |
| [Two-sided embedded part](https://www.bim-starter.com/family?guid=20e5fe4c-5635-4cc4-ae52-39ee422d9795) | Workplane-based family, nested bar and plate, used inside pylons | Preserve host/reference planes, shared-component identity and product accounting. |
| [Rectangular vertical stirrup](https://bim-starter.com/family?guid=d99ef20f-fb83-47b1-a24e-cb06fd6f1cdb) | Card explicitly describes nested IFC reinforcement and bend variants | Verify its actual Revit class; a reinforcement category does not imply a native Rebar object. |

## Next implementation priorities

These are proposed additions, not claims that v3.6 already creates every complex family.

1. **Family inspection and flex validation.** Open/edit/create family documents; inventory
   nested shared families, formulas, reference planes, labelled dimensions, visibility,
   materials and host behavior. Flex all types plus minimum/maximum dimensions and each
   option, regenerate, capture failures and restore the original type/values. Save a
   new RFA only after the checks pass. This gives a measurable acceptance criterion.
2. **Structured parametric authoring.** Add extrusion, blend, sweep, swept blend, revolve,
   void/cut and nested-instance creation, plus reference alignment and parameter
   association. Shared parameter creation must use a verified external definition's GUID,
   not recreate its display name. Support a declarative dependency graph and a single
   rollback boundary. Start with a reinforced column/embedded part before a pylon with
   openings. Autodesk exposes these forms in the [family creation API](https://blog.autodesk.io/revit-family-creation-api-labs/).
3. **Native complex reinforcement.** Multi-segment planar bars, closed stirrups, explicit
   hooks/terminations, all layout modes, varying bars, free-form curves, constraints,
   couplers and host/cover validation. Keep this workflow distinct from nested IFC
   geometry. Inspect lengths, spacing, bar counts, cover and quantities after creation.
   [Autodesk reinforcement API](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Discipline_Specific_Functionality/Structural_Engineering/Structural_Model_Elements/Reinforcement/Revit_API_Revit_API_Developers_Guide_Discipline_Specific_Functionality_Structural_Engineering_Structural_Model_Elements_Reinforcement_Rebar_html.html).
4. **Template compatibility audit.** Check a new family's GUIDs, specs, nested shared
   components and binding scope against the target project; compare tag output and
   schedule totals before/after. Report missing parameters and incompatible identities
   before loading the family into a production model.

Current dedicated family tools cover parameters/formulas/associations, reference planes,
labelled dimensions and arrays. Current rebar creation covers straight or existing
shape-driven bars, number/spacing layouts, area/path reinforcement and batches of
straight bars. Arbitrary C# remains an opt-in escape hatch; it does not replace a
validated family authoring workflow.
