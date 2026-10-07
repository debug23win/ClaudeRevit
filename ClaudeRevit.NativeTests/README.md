# Native acceptance harness

This is a developer-only Revit 2027 library. CI compiles it but cannot execute its
checks without a licensed running Revit. It is not included in the installer.

Build with .NET 10 and `SkipDeploy=true`. Stage the resulting DLLs in a fresh,
immutable directory and scan them before loading. To test a development build
alongside an installed add-in, create a separate `AssemblyLoadContext`, eagerly
load its `ClaudeRevit.dll`, then load `ClaudeRevit.NativeTests.dll`. Share Revit API
and framework/dependency assemblies with the default context. Eager loading is
required: a late resolving callback alone can bind to the installed add-in.

From a valid Revit API callback, invoke `NativeAcceptance.Queue(requestPath)`.
The harness creates an `ExternalEvent` and runs after the launcher's transaction
has ended. Never invoke document lifecycle tests inside `execute_csharp`'s own
transaction. The installed add-in's server and pane are left running; the test
assembly does not call its `OnStartup` or register its global updaters.

Requests are UTF-8 JSON. A `.started.json` marker and `.result.json` report are
written next to the request. Reports record Revit build, exact development DLL
hash, elapsed time, active/original document preservation and leaked background
documents. Every test document is closed without saving user-owned originals.
Generated fixtures require unused output paths; existing files are not replaced.

Supported actions:

- `review_fixes`: no arguments. Live checks for the v3.8.6 review fixes, each in its own
  disposable metric project: `change_element_type` really changes the type, `execute_csharp`
  result serialization never throws (Curve, Document, nested anonymous objects), and
  `set_parameter` resolves a material named like an existing type. The LiveSPDS updater
  change (never throwing from Execute) has no native case yet: `UpdaterData` cannot be
  constructed outside Revit's own update cycle.

- `tool`: `type` is `InspectFamilyFiles`, `AnalyzeFamilyStructure` or `FlexFamily`;
  `arguments` is that tool's schema. For downloaded references, supply absolute
  `file_path` or `files[].path`; do not redistribute the library files.
- `template_probe`: `templates` contains absolute RFT paths. Reports actual
  default view direction and parameters before choosing a sketch plane/driver.
- `sections`: `template`, `output_directory`, optional `shapes` list. Creates seven
  constrained section seeds, checks analytical volume and physical mass outside
  the tool's returned claims, then saves native RFA fixtures. Built-in section
  flex additionally checks independent dimensions and return to nominal.
- `nested`: `template`, `child_file` (rectangle section seed), `output_directory`.
  Creates two shared nesting levels, associates the independent width driver and
  visibility variant, tests small/large/variant changes and verifies nominal
  geometric volume without counting a shared child twice.
- `checkpoint`: `output_file`. Creates a new metric project, checks preview
  rollback, bounded progress, saved/reopened persistence, duplicate retry and
  rejection after an independent edit. The saved seed has one completed step.
- `benchmark_probe`: checks native level/unit and material evidence against the
  objective evaluator, including a deliberately wrong elevation. It does not
  invoke subscription models or establish performance timings.
- `live_schedule`: `section_file`, `output_file`. Compares independently expected
  material mass after section-size edits, physical-density edits and deletion.
  Optional `titleblock_template` and `image_directory` place native schedules on
  an A3 sheet, export PNG pixels, and report native cell styles/rows and layout
  audits. Numerical success does not certify wrapping or drawing compliance.
- `schedule_forms`: `section_file`, `output_file`, `titleblock_template`,
  `image_directory`. Creates three live timber forms, checks native length-driven
  amounts and physical material masses, then exports their A3 sheet.
- `bore`: `section_file` (tube seed). Checks actual inner cylindrical faces and
  rejects an offset axis and a diameter larger than the through-bore.
- `reference_placement`: `family_file` (downloaded BIMStarter welded box beam).
  Tests actual native beam placement length and width-driven solid bounds in a
  disposable project. It does not certify a generated structural template.
- `dependent_node`: `section_file`, `output_file`. Checks relative placement and
  parameter/actual-volume dependencies, preserves an independent managed-part
  edit and tests a deleted source. The saved file is a negative regression case.

For updater checks, the harness briefly pauses the corresponding older installed
ClaudeRevit updater during its single synchronous API callback. A test updater
is registered only for the disposable test document. Both registration and pause
are undone in `finally`; no user edit can interleave on Revit's API thread.

Only executed reports can establish acceptance. Successful compilation, a
catalogue description or the presence of a fixture generator cannot do so.
See [executed October 2026 cases](../docs/native-acceptance-2026-10.md) for results
and the [BIMStarter findings](../docs/bimstarter-reference-cases.md) for expected
minimum-count behavior and remaining visibility checks.
