#region

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Zones;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using DemoViewer.NET.Extensions.StratBook.Views.StratBook;
using DemoViewer.NET.Views.Shell;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The Strat Book hub with a strat open in the editor, on an in-memory store (nothing on disk): the rail and
///     the strat list open or collapsed, and steps of several verbs so each row's field set shows.
/// </summary>
public static partial class Variants
{
    private static HubTabView StratEditor(bool railCollapsed, bool listCollapsed, bool bare = false,
        Action<StratBookTabViewModel>? configure = null, IZonePlaceResolver? places = null, string? template = null, string map = "de_mirage")
    {
        StratBookLayout layout = new() { IsListCollapsed = listCollapsed };
        StratBookTabViewModel strats = SeededStratBook(layout, bare, places, template, map);
        configure?.Invoke(strats);
        HubTabViewModel hub = new(HostIds.StratBookHub, "Strat Book", "STRAT BOOK") { IsRailCollapsed = railCollapsed };

        List<WorkspaceTabDescriptor> sections =
        [
            new()
            {
                TabId = "stratbook.browser", Header = "Strats", Order = 0, HostId = HostIds.StratBookHub,
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
                TabId = id, Header = header, Order = i + 1, HostId = HostIds.StratBookHub, Badge = badge,
                ViewFactory = () => new TextBlock { Text = header, HorizontalAlignment = HorizontalAlignment.Center }
            });
        }

        hub.Sections.Reconcile(sections);
        hub.OnActivated(new StillContext());
        return new HubTabView { DataContext = hub };
    }

    // The step rows' inline checks: a move with no destination (warning beside "to") and a step whose time runs
    // backwards (refusal beside "at"), scrolled into the editor's view.
    private static HubTabView StratEditorChecks()
    {
        HubTabView hub = StratEditor(true, true, configure: strats =>
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
    private static HubTabView StratEditorSetPlace()
    {
        StratBookTabViewModel? strats = null;
        HubTabView view = StratEditor(true, true, configure: vm => strats = vm);
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.StepSelection.Select(strats.Editor.Steps.First(r => r.Verb == "hold").Id);
            strats.Canvas.BeginSetPlace();
        }, DispatcherPriority.Background);
        return view;
    }

    // A 1:05 push that sends B, C and D to three places, each watching something, D at a set angle: three lines
    // in the row, and three cones on the canvas facing what they watch. Selected once the view is up, C's line with it.
    // listCollapsed false is the narrowest editor, 315 px at 1280.
    private static HubTabView StratEditorLines(bool listCollapsed)
    {
        StratBookTabViewModel? strats = null;
        HubTabView view = StratEditor(false, listCollapsed, configure: vm =>
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
            Verb = "push",
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

    // Two players holding one place and one watching, shown as one "who"; then a lurk with its areas and rotate.
    // The editor at its narrowest (rail and list open), scrolled to the two rows.
    private static HubTabView StratEditorWhoLurk()
    {
        StratBookTabViewModel? strats = null;
        HubTabView view = StratEditor(false, false, configure: vm =>
        {
            strats = vm;
            StratDocument document = vm.Session.Document!;
            double at = document.Steps[^1].AtSeconds - 5;
            StratStep pair = new()
            {
                Id = Guid.NewGuid(), AtSeconds = at, Actor = StratVocabulary.ActorAll, Verb = "hold",
                Assignments =
                [
                    new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteA" }, Watch = new StepWatch { Places = ["Stairs", "CTSpawn"] } },
                    new StepAssignment { Slot = "C", To = new PlaceRef { Place = "BombsiteA" }, Watch = new StepWatch { Places = ["Stairs", "CTSpawn"] } }
                ]
            };
            StratStep lurk = new()
            {
                Id = Guid.NewGuid(), AtSeconds = at - 5, Actor = "E", Verb = "lurk",
                Lurk = new StepLurk
                {
                    Areas = ["PalaceInterior", "Connector"],
                    Rotate = new LurkRotate { AtSeconds = 40, When = "on the call", To = new PlaceRef { Place = "BombsiteB" } }
                }
            };
            vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(pair, StratJsonContext.Default.StratStep)));
            vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(lurk, StratJsonContext.Default.StratStep)));
        });
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            StratStepRow row = strats!.Editor.Steps[^1];
            strats.StepSelection.Select(row.Id);
            if (view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "StepRows") is { } rows
                && rows.ContainerFromIndex(strats.Editor.Steps.Count - 2) is { } pairRow && rows.ContainerFromIndex(strats.Editor.Steps.Count - 1) is { } lurkRow)
            {
                lurkRow.BringIntoView();
                pairRow.BringIntoView();
            }
        }, DispatcherPriority.Background);
        return view;
    }

    // At 0:41, after the plant, A and B stand side by side on the site.
    // The A execute from its template, mid-execute: A and B running from spawn onto the site, the others where their
    // throws left them. The zones are read before the view is up, so the capture has the arrivals.
    private static HubTabView StratEditorExecuteMotion(double atSeconds = 48)
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_mirage");
        StratBookTabViewModel? strats = null;
        HubTabView view = StratEditor(true, true, true, vm => strats = vm, zones, "execute-a");
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.Canvas.Transport.Pause();
            strats.Canvas.Transport.Seek(DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes.StepSchedule.TickFor(atSeconds, 115));
        }, DispatcherPriority.Background);
        return view;
    }

    // The A execute's throws landed on places, mid-execute: the Jungle smoke bloomed, the Connector smoke in flight.
    private static HubTabView StratEditorExecuteThrows()
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_mirage");
        StratBookTabViewModel? strats = null;
        string[] landings = ["Jungle", "Connector", "BombsiteA", "BombsiteA"];
        HubTabView view = StratEditor(true, true, true, vm =>
        {
            strats = vm;
            StratDocument document = vm.Session.Document!;
            int n = 0;
            List<PatchOp> ops = [];
            for (int i = 0; i < document.Steps.Count && n < landings.Length; i++)
            {
                if (document.Steps[i].Utility is not { } utility)
                {
                    continue;
                }

                UtilityRef landed = new() { Kind = utility.Kind, Landing = new UtilityLanding { Place = landings[n++] } };
                ops.Add(PatchOp.ReplaceOp($"/steps/{i}/utility",
                    JsonSerializer.SerializeToNode(utility, StratJsonContext.Default.UtilityRef),
                    JsonSerializer.SerializeToNode(landed, StratJsonContext.Default.UtilityRef)));
            }

            vm.Session.Apply([.. ops]);
        }, zones, "execute-a");
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.Canvas.Transport.Pause();
            strats.Canvas.Transport.Seek(DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes.StepSchedule.TickFor(57.8, 115));
        }, DispatcherPriority.Background);
        return view;
    }

    // A lurk whose rotate-to is a point picked outside every callout, and its lurk areas field focused with the
    // callout list open over the editor at its narrowest.
    private static HubTabView StratEditorLocationList()
    {
        StratBookTabViewModel? strats = null;
        HubTabView view = StratEditor(false, false, configure: vm =>
        {
            strats = vm;
            StratStep lurk = new()
            {
                Id = Guid.NewGuid(), AtSeconds = vm.Session.Document!.Steps[^1].AtSeconds - 5, Actor = "E", Verb = "lurk",
                Lurk = new StepLurk
                {
                    Areas = ["PalaceInterior"],
                    Rotate = new LurkRotate { AtSeconds = 40, To = new PlaceRef { X = -1234.4, Y = -560.6, LevelMinZ = -256 } }
                }
            };
            vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(lurk, StratJsonContext.Default.StratStep)));
        });
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "StepRows") is not { } rows
                || rows.ContainerFromIndex(strats!.Editor.Steps.Count - 1) is not { } row)
            {
                return;
            }

            row.BringIntoView();
            Dispatcher.UIThread.Post(() =>
            {
                if (row.GetVisualDescendants().OfType<DemoViewer.NET.Extensions.StratBook.Controls.PlaceField>().FirstOrDefault(f => f.Name == "LurkAreasField")
                        ?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box)
                {
                    box.Focus();
                }
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
        return view;
    }

    // Chips at the editor's narrowest: a two-player push whose lines watch and go via callouts and a point, and a
    // lurk row with watching, via and long areas. With open, the lurk areas add field has "a" typed, its list showing.
    private static HubTabView StratEditorChips(bool open)
    {
        StratBookTabViewModel? strats = null;
        HubTabView view = StratEditor(false, false, configure: vm =>
        {
            strats = vm;
            double at = vm.Session.Document!.Steps[^1].AtSeconds - 5;
            PlaceRef point = new() { X = -1234.4, Y = -560.6, LevelMinZ = -256 };
            StratStep push = new()
            {
                Id = Guid.NewGuid(), AtSeconds = at, Actor = StratVocabulary.ActorAll, Verb = "move",
                Assignments =
                [
                    new StepAssignment
                    {
                        Slot = "B", To = new PlaceRef { Place = "BombsiteA" }, Watch = new StepWatch { Places = ["Stairs", "CTSpawn", "Jungle"] },
                        Via = ["TopofMid", "Connector"], ViaPoints = [point]
                    },
                    new StepAssignment
                    {
                        Slot = "C", To = new PlaceRef { Place = "BombsiteA" }, Watch = new StepWatch { Places = ["TRamp"], Points = [point] },
                        Via = ["PalaceAlley"]
                    }
                ]
            };
            StratStep lurk = new()
            {
                Id = Guid.NewGuid(), AtSeconds = at - 5, Actor = "E", Verb = "lurk",
                Assignments =
                [
                    new StepAssignment
                    {
                        Slot = "E", Watch = new StepWatch { Places = ["Connector", "TopofMid"], Points = [point] }, Via = ["Catwalk", "Underpass"]
                    }
                ],
                Lurk = new StepLurk
                {
                    Areas = ["PalaceInterior", "Connector", "Underpass", "Apartments"], AreaPoints = [point],
                    Rotate = new LurkRotate { AtSeconds = 40, To = new PlaceRef { Place = "BombsiteB" } }
                }
            };
            vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(push, StratJsonContext.Default.StratStep)));
            vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(lurk, StratJsonContext.Default.StratStep)));
        });
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "StepRows") is not { } rows
                || rows.ContainerFromIndex(strats!.Editor.Steps.Count - 2) is not { } pushRow
                || rows.ContainerFromIndex(strats.Editor.Steps.Count - 1) is not { } lurkRow)
            {
                return;
            }

            strats.StepSelection.Select(strats.Editor.Steps[^1].Id);
            pushRow.BringIntoView();
            lurkRow.BringIntoView();
            if (!open)
            {
                return;
            }

            // Scrolled first and focused once that has settled, so the list opens where the field ends up.
            if (lurkRow.GetVisualDescendants().OfType<DemoViewer.NET.Extensions.StratBook.Controls.PlaceField>().FirstOrDefault(f => f.Name == "LurkAreasField")
                    ?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is not { } box)
            {
                return;
            }

            box.BringIntoView();
            Dispatcher.UIThread.Post(() => Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.Text = "a";
            }, DispatcherPriority.Background), DispatcherPriority.Background);
        }, DispatcherPriority.Background);
        return view;
    }

    private static StratBookTabViewModel SeededStratBook(StratBookLayout layout, bool bare, IZonePlaceResolver? places = null,
        string? template = null, string map = "de_mirage")
    {
        StratStore store = new(null);

        // Read before New Strat asks, so the strat is created with its spawn step before the capture.
        StratSpawnSource? spawns = template is null ? null : new StratSpawnSource();
        spawns?.ForAsync(map).Wait();
        StratBookTabViewModel vm = new(store, null, null, false, layout: layout, spawns: spawns,
            canvasPlaces: places is not null
                ? _ => Task.FromResult<IZonePlaceResolver?>(places)
                : map => Task.Run(() => new AssetZonePlaceResolverSource().TryGet(map)),
            canvasRouting: () => true);
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = map;
        vm.NewStratCommand.Execute(template);
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
