# Responsive chat and interface modes (v3.8.2)

Open chat settings → **General / Основное → Chat interface / Режим окна чата**.
Choose a mode, save settings and restart Revit to apply it. Switching only on
restart keeps one live conversation controller and avoids moving a running task
between WPF dispatchers.

| Mode | Behaviour |
|---|---|
| Separate window (default, recommended) | Chat has an independent STA thread and input queue. Typing, attachments, request supplements, Copy log, progress and Stop remain available while Revit executes a synchronous script/API call. The ribbon Chat button shows/hides this window; closing it hides it without cancelling the task. The Revit dock contains an Open chat launcher. |
| Docked Revit pane | The original dockable chat. It shares Revit's UI thread and pauses while a synchronous API operation occupies that thread. Compilation of directly invoked C# scripts is still done on a worker. |

The separate window deliberately has no Revit HWND owner/parent: coupling their
input queues would defeat the purpose. It can go behind Revit like another window;
use the taskbar/Alt+Tab or the Chat ribbon button to return to it. It is one chat
window per Revit process. Different project tabs keep their own conversations,
drafts, attachments and tasks in either mode. Project changes reach the separate
dispatcher as immutable document/workspace snapshots, without background reads
of the Revit API.

## C# execution and Stop

`execute_csharp` compiles on a worker before queueing execution in Revit. The queued
job retains the compiled bytes, intended document and original tool instance;
it does not recompile inside its transaction. `validate_csharp` also compiles on
a worker. The compilation cache/reference metadata are safe for concurrent readers.
Compilation time is recorded separately in the task journal.

The compiler inserts `ScriptRuntime.CheckCancellation()` into each `for`,
`foreach`, `while` and `do` loop body, including nested loops and deconstruction
foreach loops. Stop cancels the token; the next check throws cancellation and the
dispatcher rolls back the current managed transaction/group. Compiler diagnostic
line numbers remain relative to the original snippet.

An individual native Revit call cannot be interrupted safely. Stop waits for it
to return. Already committed earlier steps can remain. Arbitrary code that catches
and suppresses cancellation, native/library loops, recursion and Python/Dynamo
scripts do not gain automatic C# loop checks. There is no thread abortion or
reentrant Revit message pumping.

Scripts may report `ScriptRuntime.ReportProgress(done, total, stage)` in their
loops. Updates are throttled to about ten per second within a stage; stage changes
and completion are delivered immediately. API and MCP chat routes receive progress.

## Validation

The isolated WPF regression deliberately blocks the host UI thread and checks
draft entry, a live supplement, script progress and Stop on the independent
dispatcher. It also checks foreign-thread project activation and shutdown cleanup.
Emitted C# regression tests check loop semantics, cancellation of an infinite loop
and original compiler error line numbers. Native Revit modelling was not run by
these tests.

The split follows the [WPF dispatcher/threading model](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model).
