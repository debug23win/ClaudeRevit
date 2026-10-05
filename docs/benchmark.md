# Benchmark: tasks and points

Open 📊 in the Revit pane. **Codex subscription** is the default for modelling
and judging. Choose their models and reasoning efforts independently. Refresh
reads the real account catalog. API billing requires an explicit backend choice.

## Points

Each graded task has three numbers, all out of 100:

- **Quality Q**: independent judge evaluating actual before/after Revit evidence,
  accuracy, completeness, native editability and successful family flex scenarios.

Development after v3.8.3 adds mandatory objective checks before judging: units,
native categories/hosts, declared sizes, actual curved mesh samples, family flex,
tag/dimension owner views, preserved baseline IDs and CSV contents. A confident
judge cannot override a failed objective check. Quality is capped by the weighted
fraction of passed mandatory checks; incomplete evidence cannot pass. If all
objective evidence is missing, the result is ungraded, rather than a measured zero.
The remaining requirements still need independent judging. Rebar checks sample
centerlines and hosts; they do not certify every bar position, cover or design.
- **Speed S**: `100 × min(1, reference_seconds / actual_seconds)`.
- **Total**: `Q × (0.8 + 0.2 × S / 100)`.

For example, quality 90 and speed 50 give **81 points**. Quality 0 always gives
0 total regardless of speed. Faster times cannot raise the total above quality.
Times at/below the reference get the same maximum speed bonus; this avoids
rewarding tiny timing differences. Each task has a fixed reference (basics 30 s,
ordinary tasks 120 s, new complex tasks 300–480 s). These are provisional
comparison references, not measured/calibrated production performance targets.
Changing a cancellation limit does not change the reference or formula.

Use **Repeats** (1–10) with fresh document copies for repeated measurements.
The summary JSON groups task/model/seed/environment/configuration and reports
count, median, p95, range and pass rate. **Calibrate** accepts a group only with
at least five runs, every run passed, objective checks complete and quality >=90.
It records median modelling time and p95, without changing the scoring formula.
Calibration is used only for a matching seed, Revit build/machine environment,
judge/effort, limits/reset mode and objective-contract version. Otherwise the
fixed provisional reference remains visible. No production timing calibration
has been measured for the new native workflows yet.

Timing includes the modeller and its tool calls, including CLI/model setup. It
excludes the independent probes, judge and reset. Default limits are 60 tool
rounds/calls and 15 minutes per task; 0 disables that limit. A budget cutoff is
marked in the reason; actual partial results can still receive quality credit.

The summary shows mean quality, speed and total over **graded tasks only**, with
separate passed, skipped and ungraded counts. Judge failure is **?**, not a
measured zero. Wrong document/missing seed is **—** and receives no points.
Compare the same completed task set, judge/effort, seed file, limits and hardware;
a mean over a different subset is not an equivalent comparison.

Results append to `%APPDATA%\ClaudeRevit\benchmark_results.jsonl`. Each row
stores backend/model/effort, independent judge choices, actual models, verdict,
quality/speed/total, reference time, formula version, modelling seconds, rounds,
tokens and reason. `score` is the total in v3.7.2; older records used it for
quality. Use `scoring_version=quality-speed-v1` to distinguish new records.

## Latency and subscription tool discovery (v3.7.4)

The pane and benchmark CLI use `execution_profile=compact_mcp_v1`: 13 common
native tools plus `discover_revit_tools` and `invoke_revit_tool`. Discovery
returns up to five matching input schemas with a next-page offset; the gateway
uses the ordinary document-bound dispatcher, always-enabled code execution and cancellation.
Specialised family/rebar/standards tools remain available. Ordinary external MCP
clients still receive the full catalogue. API mode uses its existing progressive
group loading and records `api_progressive_v1`.

The prompt supplies actual level IDs/elevations, active view and selection.
Agents query only missing facts instead of surveying the whole project for every
simple creation. Native results can establish success without repeated broad
statistics calls. This is shared chat behaviour, not task-specific answers.
Codex discovery is cached for five minutes; Refresh always queries the account.
CLI/config/account file metadata changes invalidate the cache. Each CLI launch
still checks subscription authentication; no API fallback is enabled.

Progress separates checking the seed, opening its copy, reading evidence,
checking the model catalogue, reading context, running the model, grading and
restoring the seed. "Waiting for first tool/result" includes CLI/auth startup
and inference; it does not mean Revit has spent that time creating an element.

The **Revit** column is summed dispatcher execution time for subscription calls,
including transaction handling and document updates. JSONL `phase_seconds`
contains `catalog`, `context`, `model_and_tools`, `mcp_tool_wait_sum`,
`revit_queue_sum` and `revit_execution_sum`. Queue time measures waiting for
Revit's API callback. MCP waits include queue and execution and may overlap for
parallel calls; do not subtract their sum from total time to infer model time.
Total modelling time/scoring keeps including setup/context/model/tools and
excluding independent evidence, judge and reset. API native timings are not
instrumented and its Revit column is unavailable.

Compare the same execution profile and suite version. The reduced catalogue and
better level/grid evidence can affect both latency and judge scores relative to
v3.7.3; an older run is not an equivalent model-only comparison.

## Documents and reset

Run project tasks in a disposable **RVT**. Run F1–F5 in an already open ordinary
**RFA**. Conceptual mass/adaptive templates may not support the requested ordinary
forms. The runner checks document kind and prerequisites before starting a model
turn; All tasks is 29 tasks, with incompatible tasks skipped.

Leave **Use fresh document copy per task** enabled. Save a local non-workshared
scratch RVT/RFA first, with no pending unsaved edits. The runner opens a uniquely
named file copy per task and collects before/after evidence there. It restores the
seed document and closes the temporary copy without saving, including after
cancellation. Geometry edits, deleted elements, added types and family parameters
cannot contaminate the seed or another task. If you manually switch documents,
grading stops and only the owned background copy is closed; your chosen document
stays active. Exports outside the scratch directory and other external side effects
remain; the owned temporary directory is removed. Do not save/switch/edit during
a run. With reset disabled, changes remain in the active document and tasks can
contaminate one another; use a fresh document for each comparison.

In v3.7.3, document guards compare the open native document using Revit's
[`Document.Equals`](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/f0efbd19-9399-1ee7-96e5-fe1dbbaa0815.htm),
which treats separate managed wrappers of the same open document as equal.
This fixes the false “document changed before copying the seed” error in v3.7.2
and disappearing typed chat input in unsaved documents. A real switch still
stops queued work; reopening a file gets a fresh session key. Temporary-copy
cleanup uses the same comparison when deciding whether to reactivate the seed.

No transaction group spans separate event callbacks. Revit requires transactions
to close before an event returns ([Autodesk documentation](https://help.autodesk.com/cloudhelp/2018/ENU/Revit-API/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Transactions/Transactions_in_Events.html)).
Ordinary API jobs drained in a single callback can share an undo group; a complete
asynchronous chat turn may have multiple undo entries. Copy/open/close time is
excluded from modelling speed.

F2 requires loaded editable unhosted child families with a driving **instance
length parameter** that supports association. F3 requires an existing assembly
with at least two child levels. Use your licensed local families as fixtures;
no downloaded commercial library is redistributed. Geometry/form tasks can run
in a blank suitable RFA. For project reinforcement tasks, provide concrete
structural host types and suitable rebar/hook types, or allow the model to load
them using its tools. Missing resources reduce achievable quality.

## Complex tasks

| ID | What it tests | Reference |
|---|---|---:|
| L5 | 10-storey, 120-column frame; selectively retype 30 columns | 480 s |
| R3 | Eight longitudinal bars and three stirrup spacing zones | 360 s |
| R4 | Slab opening, top/bottom mesh, trimming and U bars | 480 s |
| R5 | Curved beam, curved native bars, denser end zones | 480 s |
| D6 | Rebar schedule with fields, grouping and checked quantities | 300 s |
| F1 | Constrained solid, formula and six independent size/aspect tests | 360 s |
| F2 | Four nested children, driving associations and three parent sizes | 480 s |
| F3 | Recursive nesting audit, shared/host properties and all-type flex | 360 s |
| F4 | Native solid/void cut, formula and four size/aspect variants | 480 s |
| F5 | Native revolve, sweep, blend and swept blend; all-type flex | 480 s |

The original B0–B6, L1–L4, R1–R2, S1 and D1–D5 remain available. Use a task
group to run fewer scenarios.

## Evidence and verification limits

The judge gets element IDs and before/after changes, coordinates, host links,
rebar diameters/quantities/layout/centerlines, openings, schedule fields/body
rows, sheet viewports and annotation view IDs. Family tests independently call
`analyze_family_structure` and `flex_family` after modelling, so the modeller's
reported checks alone cannot establish success. Flex commits/regenerates each
scenario and rolls it back, reporting solids, volumes, bounds, values and failures.

Element snapshots are bounded to 2000 per class. Changed snapshots are retained
before sampling; judge samples are capped to 200 items, schedule rows to 200 and
columns to 30. Truncation/unavailable evidence is explicit and cannot justify
full credit. A solid's bounding box alone does not prove its shape, every rebar
position or absence of clashes. Sampled centerlines and quantity evidence are
useful checks, not a complete engineering compliance audit. The grader is a
model and can vary; keep it fixed and review its cited reasons.

159 pure tests and isolated real-WPF/MCP/CLI checks cover scoring, filtering
evidence, independent flex dispatch, prerequisites, cancellation/copy cleanup ordering,
document changes, displayed averages and version-based CLI selection. Builds
target the matching Revit APIs for 2025–2027. The new geometry scenarios and
fresh-copy opening/activation/closing still require execution validation inside Revit.
`test_revit_connection` now contains a separate unsaved native fixture checking
instance/type collection and nested group rollback; it does not modify the user
document. Compilation and isolated tests do not establish live native success.

## Evidence and prerequisites (v3.8.0)

The judge receives actual DirectShape mesh triangle counts and sampled surface points, solid face/volume evidence, rebar centerlines/layout/hosts, native joint member IDs and source annotations. Metadata alone does not prove geometry. R1/R3 skip without points if the seed has neither a concrete column type nor a valid column rebar host. An empty bar catalog can be populated by `create_rebar_type`; R2 uses native structural floors and native area/path reinforcement. A generic structural connection is a logical link, not a detailed plate/bolt joint.

`usage_scope` distinguishes Codex per-turn accounting from cumulative fallback. Cached input and reasoning are subsets, so they must not be added again to input/output totals. Delegated Codex usage is marked parent-only; child-agent consumption is not included in that counter. Comparisons must use the same task set, seed, judge and effort. This release changes evidence and prerequisites, not the quality/speed formula.
