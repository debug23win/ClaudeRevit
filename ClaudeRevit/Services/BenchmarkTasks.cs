using System.Collections.Generic;

namespace ClaudeRevit.Services;

// A graded set of modelling tasks used to compare how efficiently different models solve the same
// work (success × tokens × time). Prompts are what a user would type; Criteria is the objective
// rubric handed to the impartial judge (a fixed Claude model), which grades from the actual
// before/after model state — NOT from the tested model's own narration — so the score is
// independent of which model was under test.
public sealed record BenchmarkTask(string Id, string Title, string Prompt, string Criteria);

public static class BenchmarkTasks
{
    public static readonly IReadOnlyList<BenchmarkTask> All = new[]
    {
        new BenchmarkTask("L0", "Level (unit conversion)",
            "Create a level named \"Bench L0\" at elevation 3500 mm.",
            "PASS if exactly one new Level exists at elevation ≈ 3500 mm (≈11.48 ft, ±10 mm). " +
            "Unit conversion mm→ft must be correct. FAIL if placed at 3500 ft or wrong elevation."),

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
    };
}
