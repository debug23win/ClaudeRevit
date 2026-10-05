# Executed native acceptance — October 2026

Tests ran in licensed Revit **2027, build 27.0.4.412**, on 5–6 October 2026, with
the development add-in loaded into a separate assembly context. The installed
v3.8.3 pane/server stayed running. Background tests closed their disposable
documents; reports confirmed preservation of the original and active document,
with no leaked background documents. No downloaded reference original was saved.

The developer generated the synthetic RVT/RFA seeds and independent analytical
expectations. Compilation alone is not listed as a native pass.

| Executed case | Native evidence | Result |
| --- | --- | --- |
| Seven constrained sections | Rectangle, box, I, channel, angle, tube and paired timber: small/large dimensions, return to nominal, origin, analytical volume and explicit-density mass | Passed |
| Circular tube | Actual native inner/outer cylindrical radii; curved-solid integration tolerance 0.01%; other section volume tolerances 0.001% | Passed |
| Two shared nesting levels | Independent width 240/360 mm and child visibility; nominal volume 0.24 m³, shared leaves counted once | Passed |
| Downloaded BIMStarter files | Eight nominal solids and 60 geometry-changing size/count scenarios | Passed; two clamped/nominal array cases are expected unchanged |
| Downloaded box beam placement | Native StructuralFraming beam: width 180→260 mm and placement length 6→8 m | Passed |
| Bore inspection | Actual coaxial 276 mm through-bore accepts a required 200 mm bore, rejects a 5 mm offset axis and required 280 mm diameter | Passed; no connection-capacity assertion |
| Dependent node | Width 300→500 mm updates plate 170→270 mm; 1000 mm source move updates dependent position; independent plate edit preserved; deleted source marks refresh needed | Passed |
| Saved checkpoint | Preview rollback, saved/reopened progress, resume, duplicate retry without extra element, rejection after independent edit | Passed |
| Objective benchmark probes | Correct 3.5 m level passes; deliberately changed 3.5 ft level fails with quality ceiling 50; actual RGB 128 material passes | Passed |
| Live steel mass | Actual material volume × structural-asset density; section edit, density edit and deletion give 1884→2512→2240→1400 kg | Passed |
| Steel summary formatting | Detail and four independently grouped native summaries each show the expected 1.4 t, explicit four-sided data borders and 2.5 mm text; A3 placements have no overlaps/margin overflow | Passed |
| Three timber forms | Material statement, element specification, scheme specification: native length edit gives amount 2→3 m and total physical mass 120→180 kg | Passed |

Native tests found and fixed missing background-family views, reference-plane
regeneration before alignment, attempts to flex derived tube/template dimensions,
and illegal schedule footer-option setters. The test harness uses cloned solids
for independent traversal after geometry options are disposed.

## Limits

- Three real reference visibility variants remain unverified in a placed view;
  family-editor geometry traversal returned unchanged solid fingerprints.
- The placement pass applies to the downloaded welded box beam. Generated structural
  templates still require their own project placement acceptance.
- Benchmark probes verify grading evidence. They do **not** establish subscription
  model quality/speed, calibrated reference times, or reproduce the earlier crash.
- Explicit section density is an agreed input; its formula does not automatically
  follow a later material change. Live SPDS material-volume mappings do use actual
  structural-asset density.
- Steel summaries use separately grouped native schedules because Revit footer
  rows ignore border overrides. The result is not an exact single-table form 2
  facsimile. The native blank row before data can remain; approved schedule
  appearance and short construction-group headings must be agreed and reviewed.
- Schedule numerical updates and margin/overlap checks passed. Native sheet pixels
  must also be reviewed for grid lines, fonts, wrapping and titleblock zones before
  issuing drawings; this report is not blanket SPDS/GOST certification.
  The final steel/timber A3 exports were reviewed: data/summary borders, body text,
  values and placement are present. Narrow note/group headers can split words;
  agree short headings or adjust an approved appearance before production issue.
- Native reinforcement/connection strength, cover, clash compliance and every
  library family are outside these executed cases.

Raw reports, generated seeds and image exports remain local. The harness records
the exact development DLL SHA-256 per job, Revit build, elapsed time and document
preservation. Different stages were used while fixing failures; the successful
report for each case, not an earlier failed attempt, is the acceptance evidence.
