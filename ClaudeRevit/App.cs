using System;
using System.Reflection;
using Autodesk.Revit.UI;
using ClaudeRevit.Services;
using ClaudeRevit.Tools;
using ClaudeRevit.UI;

namespace ClaudeRevit;

public class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            ToolRegistry.Instance.Register(new GetActiveViewInfo());
            ToolRegistry.Instance.Register(new GetLevels());
            ToolRegistry.Instance.Register(new GetSelection());
            ToolRegistry.Instance.Register(new QueryElements());
            ToolRegistry.Instance.Register(new AnalyzeWarnings());
            ToolRegistry.Instance.Register(new CreateWall());
            ToolRegistry.Instance.Register(new CreateFloor());
            ToolRegistry.Instance.Register(new CreateLevel());
            ToolRegistry.Instance.Register(new CreateGrid());
            ToolRegistry.Instance.Register(new CreateRoof());
            ToolRegistry.Instance.Register(new CreateRoom());
            ToolRegistry.Instance.Register(new CreateView());
            ToolRegistry.Instance.Register(new CreateSection());
            ToolRegistry.Instance.Register(new CreateElevation());
            ToolRegistry.Instance.Register(new CreateSheet());
            ToolRegistry.Instance.Register(new PlaceViewOnSheet());
            ToolRegistry.Instance.Register(new PlaceDoor());
            ToolRegistry.Instance.Register(new PlaceWindow());
            ToolRegistry.Instance.Register(new MoveElements());
            ToolRegistry.Instance.Register(new RotateElements());
            ToolRegistry.Instance.Register(new CopyElements());
            ToolRegistry.Instance.Register(new DeleteElements());
            ToolRegistry.Instance.Register(new SetParameter());
            ToolRegistry.Instance.Register(new SetColorOverride());
            ToolRegistry.Instance.Register(new TagElements());
            ToolRegistry.Instance.Register(new PickPointInView());
            ToolRegistry.Instance.Register(new CreateDimension());
            ToolRegistry.Instance.Register(new GetElementParameters());
            ToolRegistry.Instance.Register(new GetElementBoundingBox());
            ToolRegistry.Instance.Register(new MeasureDistance());
            ToolRegistry.Instance.Register(new GetProjectInfo());
            ToolRegistry.Instance.Register(new ListFamilyTypes());
            ToolRegistry.Instance.Register(new ListMaterials());
            ToolRegistry.Instance.Register(new GetPhases());
            ToolRegistry.Instance.Register(new SetActiveView());
            ToolRegistry.Instance.Register(new HideElementsInView());
            ToolRegistry.Instance.Register(new IsolateElementsInView());
            ToolRegistry.Instance.Register(new MirrorElements());
            ToolRegistry.Instance.Register(new ArrayElements());
            ToolRegistry.Instance.Register(new PinElements());
            ToolRegistry.Instance.Register(new UnpinElements());
            ToolRegistry.Instance.Register(new CreateTextNote());
            ToolRegistry.Instance.Register(new CreateDetailLine());
            ToolRegistry.Instance.Register(new CreateFilledRegion());
            ToolRegistry.Instance.Register(new CreateReferencePlane());
            ToolRegistry.Instance.Register(new CreateSchedule());
            ToolRegistry.Instance.Register(new Create3DView());
            ToolRegistry.Instance.Register(new DuplicateView());
            ToolRegistry.Instance.Register(new ApplyViewTemplate());
            ToolRegistry.Instance.Register(new SetViewScale());
            ToolRegistry.Instance.Register(new PlaceFamilyInstance());
            ToolRegistry.Instance.Register(new LoadFamily());
            ToolRegistry.Instance.Register(new ListLoadedFamilies());
            ToolRegistry.Instance.Register(new CreateStructuralColumn());
            ToolRegistry.Instance.Register(new CreateBeam());
            ToolRegistry.Instance.Register(new CreateOpeningInWall());
            ToolRegistry.Instance.Register(new CreateGroup());
            ToolRegistry.Instance.Register(new PlaceGroup());
            ToolRegistry.Instance.Register(new CreateViewFilter());
            ToolRegistry.Instance.Register(new ApplyFilterToView());
            ToolRegistry.Instance.Register(new ExportImage());
            ToolRegistry.Instance.Register(new SaveDocument());
            ToolRegistry.Instance.Register(new SelectSimilar());
            ToolRegistry.Instance.Register(new DuplicateSheet());
            ToolRegistry.Instance.Register(new GetSheetViews());
            ToolRegistry.Instance.Register(new MoveViewportOnSheet());
            ToolRegistry.Instance.Register(new ExportScheduleCsv());
            ToolRegistry.Instance.Register(new TagAllInView());
            ToolRegistry.Instance.Register(new PlaceRoomTag());
            ToolRegistry.Instance.Register(new LinkDwg());
            ToolRegistry.Instance.Register(new ListLinks());
            ToolRegistry.Instance.Register(new ReloadLink());
            ToolRegistry.Instance.Register(new HideCategoryInView());
            ToolRegistry.Instance.Register(new GetModelStatistics());
            ToolRegistry.Instance.Register(new CreateDraftingView());
            ToolRegistry.Instance.Register(new ExportPdf());
            ToolRegistry.Instance.Register(new ExportDwg());
            ToolRegistry.Instance.Register(new CreateCurtainWall());
            ToolRegistry.Instance.Register(new CreateTextWithLeader());
            ToolRegistry.Instance.Register(new DuplicateFamilyType());
            ToolRegistry.Instance.Register(new SetTypeParameter());
            ToolRegistry.Instance.Register(new UnloadFamily());
            ToolRegistry.Instance.Register(new CreateMaterial());
            ToolRegistry.Instance.Register(new SetElementMaterial());
            ToolRegistry.Instance.Register(new CreateCallout());
            ToolRegistry.Instance.Register(new CreateRevisionCloud());
            ToolRegistry.Instance.Register(new SetViewDetailLevel());
            ToolRegistry.Instance.Register(new DeleteView());
            ToolRegistry.Instance.Register(new ChangeElementType());
            ToolRegistry.Instance.Register(new CreateSelectionFilter());
            ToolRegistry.Instance.Register(new CreateShaftOpening());
            ToolRegistry.Instance.Register(new CreateDuct());
            ToolRegistry.Instance.Register(new CreatePipe());
            ToolRegistry.Instance.Register(new LinkRevitModel());
            ToolRegistry.Instance.Register(new SetProjectInfo());
            ToolRegistry.Instance.Register(new SetViewRange());
            ToolRegistry.Instance.Register(new CreateCameraView());
            ToolRegistry.Instance.Register(new SetViewPhase());
            ToolRegistry.Instance.Register(new ResetViewOverrides());
            ToolRegistry.Instance.Register(new FlipWall());
            ToolRegistry.Instance.Register(new PlaceSymbol());
            ToolRegistry.Instance.Register(new UngroupElements());
            ToolRegistry.Instance.Register(new DuplicateGroupType());
            ToolRegistry.Instance.Register(new SetElementPhases());
            ToolRegistry.Instance.Register(new CreateTopographyFromPoints());
            ToolRegistry.Instance.Register(new CreateModelLine());
            ToolRegistry.Instance.Register(new CreateIsolatedFoundation());
            ToolRegistry.Instance.Register(new CreateWallFoundation());
            ToolRegistry.Instance.Register(new ListWorksets());
            ToolRegistry.Instance.Register(new RenameElement());
            ToolRegistry.Instance.Register(new GetElementsInRoom());
            ToolRegistry.Instance.Register(new CreateSpotElevation());
            ToolRegistry.Instance.Register(new CreateSpotCoordinate());
            ToolRegistry.Instance.Register(new CreateRevision());
            ToolRegistry.Instance.Register(new CreateDependentView());
            ToolRegistry.Instance.Register(new SetCropBox());
            ToolRegistry.Instance.Register(new Set3DSectionBox());
            ToolRegistry.Instance.Register(new CreateSketchPlane());
            ToolRegistry.Instance.Register(new JoinGeometry());
            ToolRegistry.Instance.Register(new UnjoinGeometry());
            ToolRegistry.Instance.Register(new AddScheduleField());
            ToolRegistry.Instance.Register(new GetElementsOnLevel());
            ToolRegistry.Instance.Register(new GetIntersectingElements());
            ToolRegistry.Instance.Register(new AddCurtainGrid());
            ToolRegistry.Instance.Register(new GetTypeParameters());
            ToolRegistry.Instance.Register(new ListRebarTypes());
            ToolRegistry.Instance.Register(new CreateRebar());
            ToolRegistry.Instance.Register(new CreateRebarBatch());
            ToolRegistry.Instance.Register(new CreateAreaReinforcement());
            ToolRegistry.Instance.Register(new CreatePathReinforcement());
            ToolRegistry.Instance.Register(new GetRebarInHost());
            ToolRegistry.Instance.Register(new CreateRebarCoverType());
            ToolRegistry.Instance.Register(new ListRebarCoverTypes());
            ToolRegistry.Instance.Register(new SetRebarCover());
            ToolRegistry.Instance.Register(new ListViewTemplates());
            ToolRegistry.Instance.Register(new GetProjectCatalog());
            ToolRegistry.Instance.Register(new SaveMemory());
            ToolRegistry.Instance.Register(new SaveProjectMemory());
            // Family Editor suite: parametric family authoring (parameters, formulas,
            // associations, arrays) that previously had to go through execute_csharp.
            ToolRegistry.Instance.Register(new GetFamilyParameters());
            ToolRegistry.Instance.Register(new AddFamilyParameter());
            ToolRegistry.Instance.Register(new RemoveFamilyParameter());
            ToolRegistry.Instance.Register(new SetFamilyParameterFormula());
            ToolRegistry.Instance.Register(new SetFamilyParameterValue());
            ToolRegistry.Instance.Register(new SetFamilyParameterInstance());
            ToolRegistry.Instance.Register(new AssociateFamilyParameter());
            ToolRegistry.Instance.Register(new CreateLinearArray());
            ToolRegistry.Instance.Register(new CreateFamilyDimension());
            ToolRegistry.Instance.Register(new ListFamilyInstances());
            ToolRegistry.Instance.Register(new ListFamilyDimensions());
            ToolRegistry.Instance.Register(new GetElementLocations());
            ToolRegistry.Instance.Register(new GetScriptJournal());
            ToolRegistry.Instance.Register(new GenerateDiagnosticReport());
            ToolRegistry.Instance.Register(new GetFullResult());
            // Self-extension: create/remove persistent custom tools loaded from disk.
            ToolRegistry.Instance.Register(new SaveTool());
            ToolRegistry.Instance.Register(new DeleteTool());
            ToolRegistry.Instance.Register(new ListCustomTools());
            ToolRegistry.Instance.Register(new GetToolSource());
            // ExecuteCSharp is the DEFAULT escape hatch (compiled synchronously via
            // CSharpCompilation.Emit — the old CSharpScript sync-over-async deadlock is
            // gone; no Dynamo dependency). RunDynamoPython remains for Python-flavoured
            // scripts and proven community snippets; registered after C# on purpose.
            ToolRegistry.Instance.Register(new ExecuteCSharp());
            ToolRegistry.Instance.Register(new RunDynamoPython());
            ToolDispatcher.Initialize(ToolRegistry.Instance);

            // Self-extension: load persistent custom tools written to %AppData%\ClaudeRevit\
            // tools\*.cs. Only loads when code execution is enabled (dynamic tools are
            // arbitrary compiled code). A broken tool file is skipped, never fatal.
            var dyn = Tools.DynamicToolLoader.LoadAll();
            if (dyn.Loaded.Count > 0 || dyn.Errors.Count > 0)
                Services.Log.Info(
                    $"Dynamic tools: loaded {dyn.Loaded.Count} ({string.Join(", ", dyn.Loaded)}); " +
                    $"errors {dyn.Errors.Count}.");

            // Learning mode: capture the model delta of script tool calls (see ScriptJournal).
            application.ControlledApplication.DocumentChanged += ScriptJournal.OnDocumentChanged;

            SelectionService.Initialize(application);

            var view = new ChatPaneView();
            application.RegisterDockablePane(PaneIds.Chat, "Claude Chat", new ChatPaneProvider(view));

            const string tabName = "Claude";
            application.CreateRibbonTab(tabName);
            var panel = application.CreateRibbonPanel(tabName, "Claude AI");

            var assemblyPath = Assembly.GetExecutingAssembly().Location;
            var chatButton = new PushButtonData(
                name: "ShowChatButton",
                text: "Chat",
                assemblyName: assemblyPath,
                className: "ClaudeRevit.Commands.ShowChatPaneCommand"
            )
            {
                ToolTip = "Toggle the Claude chat pane.",
                LongDescription = "Opens or closes the dockable Claude AI chat panel."
            };
            panel.AddItem(chatButton);

            Services.Log.Info($"ClaudeRevit started (assembly {Assembly.GetExecutingAssembly().GetName().Version}).");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Services.Log.Error("OnStartup failed", ex);
            TaskDialog.Show("Claude Add-in", $"Failed to start: {ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        // Events registered in OnStartup must be unregistered here (Revit add-in contract).
        try { application.ControlledApplication.DocumentChanged -= ScriptJournal.OnDocumentChanged; }
        catch { /* shutting down anyway */ }
        // Post-close learning report: summarize the accumulated Dynamo/C# scripts into a
        // developer-facing file so recurring patterns can be promoted to native tools.
        try { ExperienceStore.WriteDiagnosticReport(); }
        catch { /* shutting down anyway */ }
        // Spend persistence is debounced (30s) — flush the tail so short sessions don't
        // silently under-count and inflate the balance countdown.
        try { SettingsStore.FlushSpend(); }
        catch { /* shutting down anyway */ }
        return Result.Succeeded;
    }
}
