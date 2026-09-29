#region

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;
using TabPlacement = DemoViewer.NET.Modules.Abstractions.TabPlacement;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The Strat Book hub with a strat open in the editor, on an in-memory store (nothing on disk): the rail and
///     the strat list open or collapsed, and steps of several verbs so each row's field set shows.
/// </summary>
public static partial class Variants
{
    private static StratBookHubView StratEditor(bool railCollapsed, bool listCollapsed, bool bare = false,
        Action<StratBookTabViewModel>? configure = null)
    {
        StratBookLayout layout = new() { IsRailCollapsed = railCollapsed, IsListCollapsed = listCollapsed };
        StratBookTabViewModel strats = SeededStratBook(layout, bare);
        configure?.Invoke(strats);
        StratBookHubViewModel hub = new(layout);

        List<WorkspaceTabDescriptor> sections =
        [
            new()
            {
                TabId = "stratbook.browser", Header = "Strats", Order = 0, Placement = TabPlacement.StratBook,
                ViewModelFactory = () => strats, ViewFactory = () => new StratBookTabView()
            }
        ];
        (string Id, string Header, string? Badge)[] others =
        [
            ("situations.search", "Situations", null), ("tags.matrix", "Tags", "3"), ("utility.book", "Utility", null),
            ("review.queue", "Review", "12"), ("dossier.book", "Dossier", null)
        ];
        for (int i = 0; i < others.Length; i++)
        {
            (string id, string header, string? badge) = others[i];
            sections.Add(new WorkspaceTabDescriptor
            {
                TabId = id, Header = header, Order = i + 1, Placement = TabPlacement.StratBook, Badge = badge,
                ViewFactory = () => new TextBlock { Text = header, HorizontalAlignment = HorizontalAlignment.Center }
            });
        }

        hub.Sections.Reconcile(sections);
        hub.OnActivated(new StillContext());
        return new StratBookHubView { DataContext = hub };
    }

    // The step rows' inline checks: a move with no destination (warning beside "to") and a step whose time runs
    // backwards (refusal beside "at"), scrolled into the editor's view.
    private static StratBookHubView StratEditorChecks()
    {
        StratBookHubView hub = StratEditor(true, true, configure: strats =>
        {
            strats.Editor.AddStepCommand.Execute(strats.Editor.Steps[0]);
            strats.Session.Apply(PatchOp.ReplaceOp("/steps/3/atSeconds", null, JsonValue.Create(112.0)));
        });

        hub.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (hub.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "StepRows") is { } rows)
            {
                rows.BringIntoView();
            }
        });
        return hub;
    }

    // The hold step selected once the view is up, as a click would, and Set On Map waiting for its click.
    private static StratBookHubView StratEditorSetPlace()
    {
        StratBookTabViewModel? strats = null;
        StratBookHubView view = StratEditor(true, true, configure: vm => strats = vm);
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.StepSelection.Select(strats.Editor.Steps.First(r => r.Verb == "hold").Id);
            strats.Canvas.BeginSetPlace();
        }, DispatcherPriority.Background);
        return view;
    }

    // A 1:05 move that sends B, C and D to three places, each watching something, D at a set angle: three lines
    // in the row, and three cones on the canvas facing what they watch. Selected once the view is up, C's line with it.
    // listCollapsed false is the narrowest editor, 315 px at 1280.
    private static StratBookHubView StratEditorLines(bool listCollapsed)
    {
        StratBookTabViewModel? strats = null;
        StratBookHubView view = StratEditor(false, listCollapsed, configure: vm =>
        {
            strats = vm;
            AddLinesStep(vm);
        });
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            StratStepRow row = strats!.Editor.Steps.Last(r => r.HasStoredLines);
            strats.StepSelection.SelectLine(row.Id, "C");
            if (view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "StepRows") is { } rows
                && rows.ContainerFromIndex(strats.Editor.Steps.IndexOf(row)) is { } container)
            {
                container.BringIntoView();
            }
        }, DispatcherPriority.Background);
        return view;
    }

    // Each token stands at its line's place and turns to what it watches.
    private static void AddLinesStep(StratBookTabViewModel vm)
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_mirage");
        (double X, double Y) Near(string place, double dx, double dy) =>
            zones?.PlaceCentre(place, 0) is { } c ? (Math.Round(c.X + dx), Math.Round(c.Y + dy)) : (dx, dy);

        StratDocument document = vm.Session.Document!;
        double level = document.Steps.SelectMany(s => s.Positions).FirstOrDefault()?.LevelMinZ ?? 0;
        StepPosition At(string slot, (double X, double Y) p) => new() { Slot = slot, X = p.X, Y = p.Y, LevelMinZ = level };
        StratStep step = new()
        {
            Id = Guid.NewGuid(),
            AtSeconds = document.Steps.Count > 0 ? Math.Min(65, document.Steps[^1].AtSeconds - 5) : 65,
            Actor = StratVocabulary.ActorAll,
            Verb = "move",
            From = new PlaceRef { Place = "TRamp" },
            Assignments =
            [
                new StepAssignment
                {
                    Slot = "B", To = new PlaceRef { Place = "PalaceInterior" },
                    Watch = new StepWatch { Places = ["BombsiteA", "CTSpawn"] }
                },
                new StepAssignment { Slot = "C", To = new PlaceRef { Place = "Connector" }, Watch = new StepWatch { Places = ["Stairs"] } },
                new StepAssignment
                {
                    Slot = "D", To = new PlaceRef { Place = "Stairs" }, Watch = new StepWatch { Places = ["TRamp"], YawDegrees = 135 }
                }
            ],
            Positions =
            [
                At("B", Near("PalaceInterior", 0, 0)), At("C", Near("Connector", 0, 0)), At("D", Near("Stairs", 0, 0))
            ]
        };
        vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep)));
    }

    private static StratBookTabViewModel SeededStratBook(StratBookLayout layout, bool bare)
    {
        StratStore store = new(null);
        StratBookTabViewModel vm = new(store, null, null, false, layout: layout,
            canvasPlaces: map => Task.Run(() => new AssetZonePlaceResolverSource().TryGet(map)));
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);
        if (bare)
        {
            return vm;
        }

        vm.Editor.Name = "A split through palace";

        StratEditorViewModel editor = vm.Editor;
        void Step(string actor, string verb, Action<StratStepRow>? fill = null)
        {
            editor.AddStepCommand.Execute(null);
            StratStepRow row = editor.Steps[^1];
            row.Actor = actor;
            row.Verb = verb;
            fill?.Invoke(editor.Steps[^1]);
        }

        Step("all", "move", r =>
        {
            r.FromText = "TSpawn";
            r.ToText = "TopofMid";
        });
        Step("B", "throw", r =>
        {
            r.UtilityKind = "smoke";
            editor.Steps[^1].LandingText = "Connector";
        });
        Step("C", "throw", r => r.UtilityKind = "flash");
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{editor.Steps.Count - 1}/utility/lineupId", null,
            JsonValue.Create(Guid.Parse("6b8c1f4e-2d3a-4c5b-9e7f-0a1b2c3d4e5f"))));
        Step("A", "hold", r => r.ToText = "Palace");
        Step("D", "rotate", r =>
        {
            r.FromText = "BombsiteB";
            r.ToText = "Middle";
        });
        Step("E", "plant", r => r.ToText = "BombsiteA");
        Step("all", "wait", r => r.Note = "hold for the flash");
        return vm;
    }

    // The Strat Book reads no demo; the hub only needs a context to activate its section.
    private sealed class StillContext : IModuleContext
    {
        public bool HasDemo => false;
        public string? DemoPath => null;
        public int TickRate => 64;
        public int CurrentFrameIndex => 0;
        public int CurrentTick => 0;
        public bool IsPlaying => false;
        public double Speed => 1;
        public IReadOnlyEntityView Entities => null!;
        public IReadOnlyList<PlayerRosterEntry> Players => [];
        public IReadOnlyList<IPlayerState> CurrentPlayers => [];
        public double CurtimeSeconds(int tick) => 0;
        public void RequestSeekToFrame(int frameIndex) { }
        public void RequestSeekToTick(int tick) { }
        public void RequestPlay() { }
        public void RequestPause() { }

        public event Action<IPlaybackSnapshot>? Advanced
        {
            add { }
            remove { }
        }
    }
}
