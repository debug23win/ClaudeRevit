# Roadmap / parked ideas

Durable notes for design ideas we've agreed on but deliberately deferred. Not a task list —
context so the reasoning isn't re-derived each time.

## Scaling threshold: lazy custom-tool library (PRIMARY — remember this)

**Problem.** The self-learning loop (`save_tool` promoting a proven `execute_csharp` pattern into a
permanent native tool) is good, but two shortcuts in the current design don't scale. If the library
grows to hundreds/thousands of saved tools (e.g. "30% of the ~7,400-method Revit API"), both break:

1. **Startup compile time.** `DynamicToolLoader.LoadAll()` compiles *every* `*.cs` in the tools dir
   with Roslyn on *every* Revit launch. ~2,200 tools × ~50–150 ms ≈ minutes of startup, plus one
   `AssemblyLoadContext` each = heavy memory. **This is the real killer**, more than tokens.
2. **Per-request tokens.** Custom tools are currently marked "always visible" (see
   `ChatService.AllowedTools` + `ToolCatalog.IsCustom`) on the assumption there are only a few.
   At 2,200 × ~150 tokens ≈ 330k tokens/request — breaks caching, cost, and the context window,
   re-creating exactly the catalogue tax that progressive loading (v1.68) removed.

**Fixes (both tractable, both deferred until the library is actually big):**

- **Tokens → put saved tools behind `find_tools`.** Once numerous, custom tools stop being
  always-visible and join the same progressive/lazy-load path as the built-in long tail: indexed by
  name/description/category, revealed on demand. Per request stays ~10–15k. `find_tools` scoring
  over a few thousand entries is microseconds. Needs real tags/categories on saved tools (today they
  fall into "Query" by the class-name heuristic).
- **Startup → lazy compile + compiled-assembly cache.** At launch read only a lightweight index
  (name + description + category), compile a tool only when `find_tools` first reveals/uses it, and
  cache the emitted DLL to disk (recompile only when the `.cs` changes). Startup with thousands of
  tools becomes instant because nothing compiles at boot.
- **Library hygiene.** Dedup, versioning, and pruning of dead/duplicate tools so the pool stays
  searchable and `find_tools` ranks well.

**Trigger.** Wire an automatic switch: when the custom-tool count exceeds ~120, flip the library
into lazy mode (both mechanisms above) so growth to thousands never degrades startup or cost — and
the user doesn't have to think about it. Premature now (we have dozens); implement at the threshold.

## Other parked ideas (agreed, deferred)

- ~~**"Auto-эконом" mode: Haiku executor + Opus advisor.**~~ SHIPPED (v1.70): Auto's executor
  (Sonnet 5 / Haiku 4.5) and advisor (Opus 4.8 / Fable 5) are selectable in Settings. Per-task
  diagnostics (time/tokens/rounds) added alongside. Benchmark mode (v1.71) compares models on
  graded tasks with an impartial Opus judge.
- **Auto-update after Revit closes.** A background updater launched on shutdown: check GitHub
  releases, silently install a newer version, and pull the latest knowledge base (tools + patterns).
  Loaded DLLs can't self-update while Revit runs. Requires user opt-in + a signed installer.
- **Collective experience sharing (many users, via GitHub/server).** Compounding library of *proven,
  vetted* tools. Must be safe: saved tools are arbitrary C# → no P2P auto-sync of raw code (RCE risk).
  Safe shape: (a) ship a curated tool pack in releases, (b) optional anonymous telemetry of successful
  patterns → human review → fold the best into the next release. Honest expectation: this makes the
  add-in dramatically more *reliable/fast/cheap on common tasks*, not "10× smarter" on novel problems
  (base-model intelligence is fixed). The win is accumulated competence, not raised IQ.
