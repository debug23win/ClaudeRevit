# BIMStarter reference acceptance cases

Eight RFA files were downloaded from the signed-in catalogue for local inspection.
The metadata below was read from public cards; actual native parameter constraints,
shared nesting and flex behaviour have **not yet been validated in Revit**. This is
an acceptance plan and reference index, not a report of successful native tests.
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

## Native inspection procedure

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

The developer prepares synthetic fixtures and expected analytical measurements; the
catalogue files serve as realistic regression references, not as generated fixtures.
The [native acceptance harness](../ClaudeRevit.NativeTests/README.md) now implements
the synthetic fixture generation and analytical checks. Its successful compilation
does not establish a native result. Synthetic cases and native inspection still need execution in Revit before
the new workflows can be described as accepted. Old RFA versions can be inspected in
newer Revit, but the originals must not be saved back after upgrade.
