# BIMStarter reference acceptance cases

Eight RFA files were downloaded from the signed-in catalogue for local inspection.
The files were inspected and flexed in Revit 2027 build `27.0.4.412` on 5–6 October
2026. All eight nominal native solid checks passed. Of 62 size/count scenarios,
60 changed actual solid geometry as expected; the two unchanged array scenarios
are explained by a minimum-count formula below. [Executed native results](native-acceptance-2026-10.md)
distinguish accepted cases from remaining visual/placement limitations.
Do not redistribute the downloaded files with this repository.

| Card and reference | Revit version on card | What to inspect independently |
| --- | --- | --- |
| [Column base with traverse](https://www.bim-starter.com/family?guid=dd36aa5b-2b26-48e0-bbe0-4b98a1ddcc74) | 2017 | Nested plate/rib identities; independent dimensions; column section changes; schedule counts |
| [Welded I beam](https://www.bim-starter.com/family?guid=8d1a1974-aab5-417c-8f4d-0e54b907a910) | 2017 | Unequal flanges, variable height, actual placement length, nested component totals |
| [Welded box beam](https://www.bim-starter.com/family?guid=aee9c572-0d15-4b52-bdcf-e1ffdf374f2d) | 2017 | Card v2 says widths below 180 mm were fixed: test 160/179/180/181 mm after discovering the real width driver |
| [Bolt/nut/washer](https://www.bim-starter.com/family?guid=7446e482-d46f-4f07-b08b-5e8c1de65aed) | 2017 | Ordinary/high-strength variants, diameter/length, actual coaxial through-bores, grip and child visibility |
| [Paired equal angles, X](https://www.bim-starter.com/family?guid=08b6806b-0178-436c-a00a-792debd74c4a) | 2017 | Card describes two-piece quantity/length with no nesting; distinguish geometric multiplicity from shared-child count |
| [Bent channel](https://www.bim-starter.com/family?guid=69033584-284d-4903-b2bd-c4a477ea9569) | 2017 | Actual profile dimensions, thickness limits, native framing placement and type change |
| [Rectangular bar array](https://www.bim-starter.com/family?guid=88669f88-5104-411b-b662-18339b27d743) | 2020 | Quantity 1/2/several where supported, two independent spacings, actual bar geometry, explicit counting |
| [Typical embedded part](https://www.bim-starter.com/family?guid=f648c3db-cb3c-431e-877c-c8bf40462fe9) | 2020 | Plate plus nested bar array and optional end plates; nested dependencies, variant switching and steel mass |

## Findings from the downloaded files

- The embedded part has three nesting levels; inspection visits 11 family documents.
  Its plate/bar/end-plate structure carries actual parameter associations. Constraint
  warnings are returned rather than silently resolved by deleting geometry.
- The welded box beam's real nominal width is 180 mm. Independent width changes to
  160, 179 and 181 mm passed. Its native StructuralFraming placement was also tested:
  a 6 m beam became 8 m, and changing the type width from 180 to 260 mm changed actual
  solid bounds. This does not validate every generated beam template.
- The welded I beam has three shared plate components and four supplied types;
  all four type switches committed and produced native solids. Unequal flange/height
  drivers must come from the inspected definitions, not the type-name text.
- Paired angles contain two geometric members without nested children. Their length
  formula includes a factor of two; multiplying again by a nested-child count would
  overcount. Three selected angle types and three channel types passed nominal-solid
  switching checks.
- The column base and bolt use a single-space type name. Preserve the exact name;
  trimming it would change the lookup. Column-base plate dependencies and bolt
  diameter/length scenarios changed actual geometry.
- The bar array clamps its calculated length-direction count to at least two:
  `if(Колво длина < 2, 2, Колво длина)`. Setting 1 or the nominal 2 cannot change
  geometry and correctly fails a strict *require change* assertion. Other 12 array
  size/spacing/count cases passed. Rebar-category family geometry is not proof of
  native hosted `Rebar`.
- Embedded-part holes/end-plates and bolt ordinary/high-strength visibility toggles
  committed, but family-editor solid traversal did not change its fingerprint.
  These three visibility cases remain unverified in a placed project/view; that
  result alone does not prove the reference family is defective. The synthetic
  shared-child visibility case did change actual geometry and passed.

## Reproducible inspection procedure

1. Use a disposable saved local RVT and a development build with `inspect_family_files`.
   Keep originals closed. First pass supplies file paths only, discovering actual
   parameter names/GUIDs, formulas, types and nested dependency identities.
2. Build scenarios from those definitions; do not infer names from the catalogue.
   Test independent small/nominal/large drivers, return-to-nominal, variant visibility
   and boundary values. The background document is closed unsaved after each family.
3. Compare actual native solid bounds/volumes and child identities. A parameter that
   changes without its expected geometry changing is a failed acceptance case.
4. In a separately saved scratch project, place representative native framing/connection
   families and test placement length, section retyping, declared node dependencies,
   bores and live schedule mass/counts. Compare manually derived expected values.
5. Export schedule sheets and inspect wrapping, fonts, borders and overlap results.
   Save seed hashes, Revit build, exact scenarios and native results with the report.

The developer prepares synthetic fixtures and independent analytical expectations;
catalogue files serve as local regression references. The
[native acceptance harness](../ClaudeRevit.NativeTests/README.md) executed these
checks in disposable background documents. Originals were closed without saving,
and the active document was preserved. Downloaded files are not distributed here.
