using System.Collections.Generic;

namespace ClaudeRevit.Services;

// A graded set of modelling tasks used to compare how efficiently different models solve the same
// work (success × tokens × time). Prompts are what a user would type; Criteria is the objective
// rubric handed to the impartial judge (a fixed Claude model), which grades from the actual
// before/after model state — NOT from the tested model's own narration — so the score is
// independent of which model was under test.
public sealed record BenchmarkTask(string Id, string Title, string Prompt, string Criteria,
    int ReferenceSeconds = 120, bool FamilyDocument = false, bool RequiresNestedSeed = false,
    string? FlexScenarios = null);

public static class BenchmarkTasks
{
    public static readonly IReadOnlyList<BenchmarkTask> All = new[]
    {
        // --- Basics: one core tool each, verifiable straight from the probe delta. ---
        new BenchmarkTask("B0", "Level (unit conversion)",
            "Create a level named \"Bench B0\" at elevation 3500 mm.",
            "PASS if levels increased by 1 and a new entry near 3.5 m appears in level_elevations_m. " +
            "Unit conversion mm→m must be correct — FAIL if it lands near 3500 m (wrong conversion)."),

        new BenchmarkTask("B1", "Wall",
            "Create one straight wall exactly 5 m long on Level 1.",
            "PASS if walls increased by 1 and wall_lengths_m gained a value ≈ 5 (±0.1)."),

        new BenchmarkTask("B2", "Floor",
            "Create one rectangular floor 4 m × 3 m on Level 1.",
            "PASS if floors increased by 1 and floor_areas_m2 gained a value ≈ 12 (±1)."),

        new BenchmarkTask("B3", "Grid",
            "Create one straight grid line 10 m long.",
            "PASS if grids increased by 1."),

        new BenchmarkTask("B4", "Structural column",
            "Place one 400×400 mm structural column on Level 1 (create/load a suitable type if needed).",
            "PASS if structural_columns increased by 1."),

        new BenchmarkTask("B5", "Material",
            "Create a new material named \"Bench Concrete\" with a grey colour.",
            "PASS if materials increased by 1."),

        new BenchmarkTask("B6", "DirectShape box",
            "Create a DirectShape box 2 m × 2 m × 2 m near the origin (a coarse mesh).",
            "PASS if direct_shape_count increased by 1 and its size_m is ≈ [2,2,2] (±0.3). This checks " +
            "the DirectShape tool works and the probe can see it."),

        // --- Composite / non-standard tasks. ---
        new BenchmarkTask("L1", "Basic room (multi-tool)",
            "On Level 1, build a rectangular room 8000×5000 mm: four walls forming a closed loop, " +
            "a floor over it, a flat roof, and a door in one wall.",
            "PASS if 4 new walls form a closed 8×5 m rectangle (endpoints coincide), plus 1 floor " +
            "(area ≈ 40 m²), 1 roof, and 1 door hosted in a wall. FAIL on an open loop or missing pieces."),

        new BenchmarkTask("L2", "5-storey frame (parametric)",
            "Create a 5-storey structural frame: floors/levels at 0, 3.5, 7, 10.5 and 14 m; a 12×12 m " +
            "slab on each level; 400×400 mm structural columns at the four corners of every level; and " +
            "grids A/B and 1/2 running through the corner columns.",
            "PASS if 5 slabs at the correct elevations, 20 columns (4 corners × 5 levels) at the correct " +
            "XY and vertically aligned across levels, and 4 grids exist. FAIL if column count ≠ 20 or " +
            "levels/elevations are wrong."),

        new BenchmarkTask("L3", "Query→filter→act (accuracy)",
            "First create four straight walls on Level 1 with lengths 8 m, 7 m, 5 m and 4 m. Then find " +
            "every wall longer than 6 m, change its type to a 300 mm generic wall, and tell me the total " +
            "length of the walls you changed.",
            "The setup makes exactly two walls (8 m and 7 m) longer than 6 m. PASS only if those two were " +
            "retyped and the 5 m and 4 m walls were left untouched, and the reported total is 15 m. This " +
            "tests precision and honest reporting — over-acting or a wrong total is a FAIL."),

        new BenchmarkTask("L4", "Barrel-vault mesh (freeform, no freeze)",
            "Model a barrel-vault roof over a 20×10 m hall: a half-cylinder shell, radius 5 m, its axis " +
            "along the 20 m length, as a coarse DirectShape mesh, sitting on top of 4 m walls. Keep the " +
            "mesh coarse (a few hundred faces).",
            "PASS if a DirectShape exists whose bounding box ≈ 20×10×5 m with its base near z≈4 m, the mesh " +
            "is coarse (well under ~2000 faces), Revit did not freeze, and the surface is actually a " +
            "half-cylinder (not a box). FAIL on a heavy mesh, a freeze, or wrong geometry."),

        new BenchmarkTask("R1", "Rebar — simple form",
            "Place longitudinal rebar plus stirrups inside a 400×400 mm × 4 m structural concrete column " +
            "(create the column first if none exists), using the dedicated rebar tools.",
            "PASS if reinforcement (a rebar set / bars) is actually hosted inside the column: several " +
            "vertical bars and transverse stirrups, placed with the rebar tools (not faked as lines). " +
            "FAIL if no Rebar elements were created or they are outside the host."),

        new BenchmarkTask("R2", "Rebar — complex form",
            "Create a 6×4 m structural concrete slab (floor) on Level 1, then reinforce it: add area (mesh) " +
            "reinforcement across it, and add path reinforcement along its edges. Use the appropriate " +
            "dedicated tools for each.",
            "PASS if both area reinforcement AND path reinforcement elements were created in the slab, " +
            "using the correct distinct tools. Partial credit context: note which of the two succeeded. " +
            "FAIL if neither was created or they are not associated with the slab."),

        new BenchmarkTask("S1", "Steel frame with a connection",
            "Build a small steel portal frame: two steel columns 4 m tall, 6 m apart, with a steel beam " +
            "spanning between their tops; then create a connection/joint between the beam and each column " +
            "(use a steel-connection approach — a Structural Connection element or an equivalent detailed " +
            "joint).",
            "PASS if 2 steel columns + 1 steel beam form the portal at the right geometry AND at least one " +
            "beam-to-column connection/joint element (StructuralConnectionHandler or equivalent) was " +
            "created. FAIL if only the bare members exist with no connection, or geometry is wrong."),

        // --- Documentation & delivery: the half of BIM that isn't modelling. These are the real
        // discriminators — each needs a CHAIN of tools where a wrong intermediate result is only
        // visible at the end (a sheet with no viewport, a schedule with no fields, tags placed in
        // the wrong view), so a model that doesn't verify its own work fails them.
        new BenchmarkTask("D1", "Sheet set (view + sheet + viewport)",
            "Create two levels 3 m apart named \"Bench D1 A\" and \"Bench D1 B\", put a wall on each, then " +
            "produce a drawing sheet for EACH level: a floor plan of that level, placed on its own sheet " +
            "with a title block, numbered \"BD-101\" and \"BD-102\". Each plan must actually appear on its " +
            "sheet, not just exist.",
            "PASS only if 2 new sheets exist with those numbers AND each has a viewport holding a floor " +
            "plan of the matching level. FAIL if sheets were created but no view is placed on them, if " +
            "both plans landed on one sheet, or if the plans are of the wrong levels."),

        new BenchmarkTask("D2", "Schedule with fields + export",
            "Create a wall schedule named \"Bench D2 Walls\" with the fields Family and Type, Length and " +
            "Area, then export it to CSV in the temp folder and tell me the exported file path and the " +
            "number of data rows it contains.",
            "PASS if a ViewSchedule of Walls named \"Bench D2 Walls\" exists WITH those fields added, a CSV " +
            "was written, and the reported row count matches the walls in the model. FAIL if the schedule " +
            "has no fields, if nothing was exported, or if the reported row count is invented rather than " +
            "taken from the export."),

        new BenchmarkTask("D3", "Annotate a plan (dimension + tags)",
            "Create a level \"Bench D3\", a floor plan view of it, and three walls forming a U shape. In " +
            "THAT plan view: tag every wall and add one dimension between the two parallel walls. The " +
            "annotation must live in the new plan view, not in whatever view happened to be active.",
            "PASS if the tags and the dimension exist AND their OwnerViewId is the new Bench D3 plan. FAIL " +
            "if annotation was placed in the previously active view, if tags are missing, or if no " +
            "dimension was created."),

        new BenchmarkTask("D4", "Find-and-fix (filter → act → verify)",
            "Create five walls of differing lengths on one level, then find every wall shorter than 4 m and " +
            "set their Comments parameter to \"SHORT\". Leave the others untouched. Report how many you " +
            "changed and their ids.",
            "PASS only if exactly the walls under 4 m carry Comments=SHORT, the longer ones are unchanged, " +
            "and the reported count matches reality. FAIL if every wall was tagged, if the threshold was " +
            "applied in feet instead of metres, or if the reported count doesn't match the model."),

        new BenchmarkTask("D5", "Model audit (diagnose → clean → prove)",
            "Audit this model's health, then remove whatever unused/redundant content you safely can, and " +
            "report what you found and what you removed — with before/after numbers. Do not delete any " +
            "geometry I placed.",
            "PASS if the model was actually diagnosed (not guessed at), a cleanup was performed or " +
            "correctly reported as unnecessary, and before/after numbers are given that match the probe " +
            "delta. FAIL if placed geometry was deleted, if numbers are invented, or if it only described " +
            "what it would do without doing it."),

        new BenchmarkTask("L5", "10-storey frame and selective correction",
            "Build a native concrete frame with 10 storeys at 3500 mm intervals, a 15×12 m slab on each " +
            "storey and a 4×3 column grid at X=0/5000/10000/15000 and Y=0/6000/12000 mm. " +
            "Use 400×400 mm columns, 120 columns total. Then change only the 30 columns at X=15000 mm " +
            "to 500×500 mm. Verify coordinates, elevations, counts and the unchanged 90 columns. No DirectShape substitutes.",
            "PASS requires 10 native floors, 120 structural columns in the prescribed grid and 30 selectively retyped " +
            "columns. Score completeness, correct alignment/elevations and accurate selective change from element evidence.", 480),

        new BenchmarkTask("R3", "Column cage with three stirrup zones",
            "Create a native 600×600 mm, 6000 mm tall concrete column. Reinforce with eight Ø20 longitudinal bars " +
            "inside 40 mm cover and closed Ø10 stirrups: 100 mm spacing in the bottom/top 1200 mm zones and " +
            "200 mm in the middle 3600 mm. Use native Rebar sets with explicit host, layout and hook choices; " +
            "verify centerlines, counts and actual spacing. No lines, meshes or DirectShapes as reinforcement.",
            "PASS requires native host-linked Rebar: eight longitudinal bars and three distinct stirrup zones. " +
            "Verify diameter, layout quantity/spacing, elevations and centerlines relative to host bounds/cover. " +
            "Counts alone do not establish compliance; missing hook/cover evidence loses credit.", 360),

        new BenchmarkTask("R4", "Slab opening, two layers and edge reinforcement",
            "Create an 8000×6000×250 mm native concrete slab with a centered 1500×1000 mm opening. " +
            "Use native reinforcement for top and bottom Ø12 bars in both directions at 150 mm spacing, " +
            "30 mm cover. Trim the mesh at the opening, add two Ø16 trimming bars along each opening edge " +
            "and U bars at the perimeter. Verify bar geometry and hosting; no reinforcement through the opening.",
            "PASS requires a native slab/opening and distinct top/bottom reinforcement in both directions, " +
            "native trimming/edge bars, host links, diameters and centerline evidence. Penalize bars crossing the " +
            "opening or leaving host bounds. Do not infer layers or cutouts just from Rebar counts.", 480),

        new BenchmarkTask("R5", "Curved beam reinforcement and anchorage",
            "Build a native curved reinforced-concrete beam following a 90-degree circular arc of 5000 mm " +
            "radius, section 300×600 mm, with 35 mm cover. Add four Ø16 longitudinal bars following its curve " +
            "and Ø8 stirrups at 150 mm along the arc with denser 75 mm end zones. Use native Rebar/free-form " +
            "tools as appropriate. Verify actual curved centerlines, host and anchorage; report unsupported constraints honestly.",
            "PASS requires a native curved structural member and hosted curved native rebar with correct radii/cover, " +
            "quantity and end-zone geometry. Straight chords, meshes and unhosted shapes fail. Unsupported operations " +
            "cannot receive full credit even when reported honestly.", 480),

        new BenchmarkTask("D6", "Rebar schedule and checked quantities",
            "Create a 400×400×4000 mm concrete column with four Ø16 longitudinal bars and Ø8 stirrups at " +
            "200 mm spacing. Build a native rebar schedule Bench D6 Rebar containing diameter, bar length, " +
            "quantity and total length, grouped by bar type. Verify the visible rows and totals against actual " +
            "Rebar sets; do not invent fields or values.",
            "PASS requires hosted native reinforcement, a Rebar ViewSchedule with the requested fields/grouping, " +
            "nonempty body rows and lengths/quantities consistent with rebar evidence. A named empty schedule fails.", 300),

        new BenchmarkTask("F1", "Constrained parametric solid, six size tests",
            "In this ordinary RFA create a native rectangular extrusion driven by length parameters Bench_W, " +
            "Bench_D, Bench_H using reference planes and labeled dimensions/parameter associations. " +
            "Start with 1000×600×800 mm. Create types Bench Small and Bench Large, and formula Bench_H=Bench_W*0.8. " +
            "Flex minimum, nominal, maximum and extreme aspect ratios without broken constraints. " +
            "Keep this document active; do not save, open another document or use imported/DirectShape geometry.",
            "PASS requires native solid forms, driving dimensions/associations and the formula; independent flex " +
            "must pass every scenario with actual solid bounds/volume changing to the requested sizes. Merely adding " +
            "parameters/types without geometry response fails.", 360, true, FlexScenarios: """
            [{"name":"small","values":{"Bench_W":300,"Bench_D":200},"require_solid":true},
             {"name":"nominal","values":{"Bench_W":1000,"Bench_D":600},"require_solid":true},
             {"name":"large","values":{"Bench_W":3000,"Bench_D":1800},"require_solid":true},
             {"name":"wide","values":{"Bench_W":2500,"Bench_D":200},"require_solid":true},
             {"name":"deep","values":{"Bench_W":300,"Bench_D":2000},"require_solid":true},
             {"name":"repeat","values":{"Bench_W":1000,"Bench_D":600},"require_solid":true}]
            """),

        new BenchmarkTask("F2", "Nested assembly, associations and variants",
            "Use the editable unhosted child families already loaded in this RFA. Place four native nested instances " +
            "in a 2×2 assembly. Add parent length Bench_W and a compatible parent length parameter Bench_ChildSize " +
            "associated with a driving child instance length parameter on all four children. Use Bench_ChildSize=Bench_W/4. " +
            "Create assembly types Bench Small and Bench Large at Bench_W=800 and 2400 mm. Inspect recursive nesting " +
            "and flex both types and intermediate sizes; prove each child changes, not just the parent metadata. " +
            "Keep this RFA active; do not save or switch documents. Discover the actual child parameter names first.",
            "PASS requires four new nested FamilyInstances, four real parent-child driving associations and the formula. " +
            "Independent flex at 800/1600/2400 mm must change all four children's bounds with visible native solids. " +
            "Inspect recursive graph and types. An association to a non-driving text parameter fails.", 480, true, true, """
            [{"name":"small","values":{"Bench_W":800},"require_solid":true},
             {"name":"middle","values":{"Bench_W":1600},"require_solid":true},
             {"name":"large","values":{"Bench_W":2400},"require_solid":true}]
            """),

        new BenchmarkTask("F3", "Multi-level nesting audit and flex",
            "Analyze the existing multi-level nested RFA down to depth 4. Identify shared/unshared children, " +
            "host types, dependencies, associated parameters, formulas and imports. Add types Bench Variant A/B " +
            "using two different valid combinations of existing length and yes/no parameters. Flex all existing " +
            "types, then your two variants; report exact failures and geometry changes from real probes. " +
            "Do not delete baseline content, save or switch documents.",
            "PASS requires a recursive graph reaching at least two child levels and accurate shared/host/association " +
            "evidence, two new valid types, independent all-types flex with solids and no constraint errors. " +
            "Claims about nodes skipped due to limits/uneditable status receive no credit.", 360, true, true, "[]"),

        new BenchmarkTask("F4", "Solid and void forms with a size matrix",
            "In this RFA create a native solid extrusion driven by Bench_W/Bench_D/Bench_H, plus a native " +
            "void extrusion cutting a centered rectangular through-hole driven by Bench_Hole=Bench_W/4. " +
            "Nominal size 1200×800×600 mm. Combine the cut properly and constrain the hole. Flex small, large " +
            "and narrow variants. Keep the RFA active; do not save or use mesh/imported geometry.",
            "PASS requires native solid and void forms and a real geometry combination. Independent flex must " +
            "show valid positive solids, correct outer bounds and reduced volume for the through-hole at every " +
            "size. A visible void that does not cut the solid fails.", 480, true, FlexScenarios: """
            [{"name":"small","values":{"Bench_W":600,"Bench_D":400,"Bench_H":300},"require_solid":true},
             {"name":"nominal","values":{"Bench_W":1200,"Bench_D":800,"Bench_H":600},"require_solid":true},
             {"name":"large","values":{"Bench_W":2400,"Bench_D":1600,"Bench_H":1200},"require_solid":true},
             {"name":"narrow","values":{"Bench_W":1200,"Bench_D":350,"Bench_H":600},"require_solid":true}]
            """),

        new BenchmarkTask("F5", "Native revolve, sweep, blend and swept blend",
            "In this RFA create four separate native solid forms: a 360-degree revolve of a stepped spindle " +
            "profile (overall height 1000 mm, maximum radius 150 mm); a rectangular 100×150 mm profile swept " +
            "around an L path with 1000 and 600 mm legs; a 500 mm high blend between centered 400×400 and " +
            "200×200 mm rectangles; and a swept blend along a straight 1000 mm path between 300×300 and " +
            "150×150 mm profiles. Add three family types and verify every type regenerates with all four " +
            "native solids. Keep this RFA active; do not save or use imported/DirectShape approximations.",
            "PASS requires the four native API form kinds Revolve, Sweep, Blend and SweptBlend, appropriate " +
            "bounds and positive visible volumes, three types and independent successful all-types flex. " +
            "Replacing a requested form with an extrusion or mesh loses that form's credit.", 480, true, FlexScenarios: "[]"),
    }.Select(t => t.Id.StartsWith("B", System.StringComparison.Ordinal) ? t with { ReferenceSeconds = 30 } : t).ToArray();
}
