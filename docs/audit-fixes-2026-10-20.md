# Audit fixes and validation — 20 October 2026

Baseline: `main` commit `24d62d5dc8f7013c1830b1c5287f1dae03f42f19` (15 October).
The supplied interim report contains 168 entries (1 critical, 50 high, 103 medium, 14 low), including overlaps and unverified native hypotheses. This change implements the confirmed result-safety, units, cleanup, discovery and observability recommendations; it does not certify all 168 entries as independent defects or resolved findings.

## Result and execution contract

- Completion recognizes `succeeded:false` and legacy `error` results. Batch integer `ok` counts remain integers; `status` distinguishes success, partial and failure.
- Transactional logical failures roll back before responding. Failed items in `run_batch` roll back their subtransaction while successful items remain in the batch. Script transaction groups also roll back on logical failures. This protects Revit model changes, not external file/network effects of arbitrary code.
- Results and aged history stay one JSON value. Truncation preserves failure, preview, commit and rollback control fields. Large diagnostics retain an archive reference.
- `get_full_result` returns JSON pages: `content`, `offset`, `next_offset`, `has_more`, `total_chars`. Follow the returned offset. Each serialized page fits the response ceiling, including escaped characters and emoji.
- Archive entries retain up to 2,000,000 characters; `archive_truncated` explicitly identifies larger originals. Memory and disk are bounded. The in-memory cache evicts the least recently used entry (a plain dictionary evicted the newest one after its first removal, so later lookups re-read the archive file). Archives are historical evidence, not a substitute for re-querying edited model elements.

## Model correctness and deletion

- Structural density uses internal kg/ft³, not lb/ft³. Light materials are not rejected by a fabricated minimum density.
- `derive_parameters` converts millimetres/m²/m³ for matching measurable target specs. Number/Text targets retain source values; incompatible measurable dimensions are rejected.
- Requested wall/floor thickness means total compound width; the edited core width accounts for retained finish layers. Nonpositive core widths fail.
- Default type preservation excludes read-only values and derived head height; explicit named preservation remains available.
- Cleanup uses Revit's native Purge Unused candidates. It retains at least one family type and treats location-less rooms as unplaced. Non-linked imports require explicit selection; being non-linked does not mean an import is exploded or disposable.
- Every candidate deletion is rehearsed in a subtransaction. Cascades outside the candidate set are reported and rolled back on apply as well as preview. Family unloading checks placed instances and dependency cascades before deletion.
- Undo arguments accept integral `1.0` and string integers with validation, never silently turn invalid input into a whole-action undo. Unscoped calls get independent action IDs. External edits/manual Undo/Redo invalidate agent history. Each posted step checks the active document; restoration of a foreign step waits for the Redo event.

## Context and responsiveness

- Direct API discovery reveals individual tools, at most 12 per search; exact names reveal one. Ambiguous Russian grid/section-size stems no longer prewarm reinforcement or family authoring. Disabled tools are excluded.
- Tools picked out by the query's own words are ranked, and matches beyond the 12 are listed by name so one exact-name search loads any of them. A group named only by a keyword is loaded whole: tool descriptions are English, so a Russian request ("хомуты по зонам") gives no word to rank by, and the first 12 in registration order would hide the rest of the group. Every keyword group in a prompt is loaded, so "арматура и разрез" gets both.
- Group keywords match at the start of a word or after a common Russian verb prefix ("образмерь", "замаркируй", "сгруппируй", "заармируй"). Keys are split like the text, so "технико-эконом" and "СП 1.13130" match, and quotes, guillemets and brackets separate words.
- Subscription MCP discovery keeps its existing compact gateway; API discovery and subscription timings are different execution profiles.
- Codex driving rules are included in the initial prompt, not duplicated in every resumed prompt. MCP initialization continues to deliver current session instructions.
- Catalog lists retain complete cached data and expose bounded pages/counts, section selection, family-category and substring filters. External edits invalidate the affected document cache.
- Cyrillic headless results are re-encoded without `\uXXXX` escapes before the budget is measured, so the limit counts real text and the kept head is readable. Callers still serialize with the default encoder; the test now builds its input the same way.
- Prepared PNG attachments fit the actual inline base64 budget. Markdown throttles continuous deltas instead of postponing rendering until streaming stops.
- Optional closing suggestions do not trigger automatic continuation. Benchmark API round limits use the benchmark's configured budget rather than a smaller pane setting.

## Evidence and diagnostics

Every changed call returns a receipt with exact change counts, a paged archive of changed IDs, and a small post-write readback sample (existence/category/representation/bounds in mm). Category counts resolve affected surviving elements rather than labelling the unsampled remainder as counted geometry. Deleted categories that cannot be known remain explicitly unknown.

A receipt proves only the readback scope. Before claiming dimensional or parameter correctness, use `verify_model_result` with declared expected values; it already performs independent native checks. No generic success flag certifies engineering adequacy or drawing compliance.

Task journals expose authorization-process and API-provider request counts/times plus post-write readback time, in addition to existing total, queue, Revit, worker and token/cache metrics. Phase sums may overlap; provider time includes network/server/streaming and must not be presented as pure model reasoning time. Failed journal writes are surfaced in diagnostics. SPDS updater failures/incomplete sources persist in the model and are exposed by `audit_spds_schedule`. The updater writes that state only when it changes, so ordinary edits do not modify the table's DataStorage element (in a workshared model, writing it on every edit would hand its ownership to whoever edited last and fail collaborators' edits). `refresh_utc` is the time the state was last recorded.

## Validation

Follow-up review fixes: 426 unit tests pass, 14 of them new; each new test fails on the previous revision. Builds for Revit 2025/2026/2027, MCP, CLI and the 51 ModPlus checks pass (run on Linux with Windows targeting).

Local checks on Windows: **412 unit tests passed**, isolated pane/MCP/CLI checks passed, **51 ModPlus adapter checks passed**, builds for Revit 2025/2026/2027 and native-harness compilation succeeded (existing compiler warnings remain). No production project was modified, no live model calls were billed, and native Revit execution remains pending.

Pure regression tests exercise failure preservation, escaped/Unicode paging, archive retention, Cyrillic budgeting, density including light materials, integer parsing, selective tool discovery, catalog paging, aged result structure and scoped phase accounting. WPF checks use the real markdown renderer and a dense image fixture. Transport/CLI checks use isolated fake clients/processes and no paid model calls.

`ClaudeRevit.NativeTests` adds four independent `review_fixes` cases:

| Case | Independent expected result |
| --- | --- |
| `audit_total_wall_thickness` | 20 + editable core + 30 mm totals 300 mm |
| `audit_derived_length_internal_units` | A wall's measured length copied into a Length parameter remains the same number of internal feet |
| `audit_material_density_7850` | A 1 m³ solid with structural steel density weighs 7850 kg |
| `audit_placed_unbounded_room_survives_cleanup` | A placed room with zero area remains after cleaning unplaced rooms |

Compilation is not execution: these new native cases still require Revit 2027. Also run live acceptance for Python exception rollback, painted/nested references during purge, family unload cascades, Undo/Redo interleaved with manual edits/tab switching, and SPDS refresh recovery. Use disposable RVT/RFA fixtures, not production models.

## Claims intentionally not converted into speculative fixes

Physical SPDS text height requires an independent exported-PDF calibration. Auth configuration-file churn needs traces; no weakening of subscription authentication was introduced. Exact token/cost savings require measured backend-specific baselines. External repository candidate counts need source lists and deduplication; existence of a repository is not a reviewed implementation. ElementLevelFilter is a native slow filter, not a quick filter.

For each remaining report entry record: source commit, Revit build, backend/model, fixture hash, exact call, expected/actual result, reproduction status, severity/confidence, duplicate parent and native evidence. Measure before/after using identical seed/model/limits; keep quality checks separate from speed.
