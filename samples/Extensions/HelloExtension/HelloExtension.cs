using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.Sdk.Playback;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Input;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Timeline;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace HelloExtension;

/// <summary>
///     A tab that shows the open demo, a tab with a map of its own, a tab with a scene of its own frames and a
///     pointer tool over them, a hub tab with two sections, a status chip, a pass that counts the open demo's
///     frames and keeps the count as the demo's own data, a ruleset whose kills table the host keeps as library
///     facts and the tab reads back, a settings page the host renders, a Match Overview action that greets a
///     demo from a job on its parse and posts a notification when it is done, a 2D Playback toolbar button, map
///     layer and timeline lane, and the user's active account read from the host with the demos the library
///     holds it in, opened in the host's Player Overview from the tab.
/// </summary>
public sealed class HelloExtension : IExtension
{
    public const string ExtensionId = "dev.example.hello";
    public const string MasterSwitch = "pack.hello";
    public const string TabFeature = "tab.hello";

    /// <summary>Settings keys; the host stores them in the extension's own settings file.</summary>
    public const string ShowFramesKey = "tab.showFrames";

    public const string GreetingKey = "greeting";

    /// <summary>The keymap action that runs the toolbar button. Prefixed with the extension id, as every command id must be.</summary>
    public const string WhereAction = ExtensionId + ".where";

    /// <summary>The id the greeting's notification is posted under.</summary>
    public const string GreetedNotification = "hello.greeted";

    /// <summary>The hub tab's id, which its sections name as their host.</summary>
    public const string HubId = ExtensionId + ".hub";

    public string Id => ExtensionId;

    public string FeatureId => MasterSwitch;

    public IEnumerable<ExtensionFeature> Features =>
    [
        new(MasterSwitch, ExtensionFeatureKind.Extension, "Hello", "A sample extension.", null, AudienceDefaults.Everyone),
        new(TabFeature, ExtensionFeatureKind.Tab, "Hello tab", "Shows the open demo.", MasterSwitch, AudienceDefaults.Everyone)
    ];

    public IEnumerable<CommandDescriptor> Commands =>
    [
        new(WhereAction, "Hello: log the tick shown", "playback2d", new KeyGesture(Key.H, KeyModifiers.Shift), _ => false)
    ];

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(sp => new HelloTabViewModel(sp.GetExtensionContext<HelloExtension>()));
        services.AddSingleton<HelloChip>();
    }

    public void Contribute(IExtensionContributions contributions, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        IExtensionContext context = contributions.Context;
        contributions.Tabs(new HelloModule(services.GetRequiredService<HelloTabViewModel>));

        // The host draws the hub's tab and rail; the sections are ordinary tab descriptors naming it.
        contributions.HubTab(new HubTabContribution(HubId, "Hello hub", 51, "HELLO"));
        contributions.Tabs(new HelloHubModule());
        HelloChip chip = services.GetRequiredService<HelloChip>();
        contributions.StatusChip(new StatusChipContribution(ExtensionId + ".chip", chip));

        // A page under Settings, Extensions that the host draws from this list and stores in context.Settings.
        contributions.SettingsSchema(new SettingsSchema("hello.settings", "HELLO",
        [
            SettingDescriptor.Toggle(ShowFramesKey, "Show frame counts", true, "The tab names how many frames the open demo has."),
            SettingDescriptor.Choice(GreetingKey, "Greeting", "Hello", [new SettingChoice("Hello", "Hello"), new SettingChoice("Hi", "Hi")])
        ]));

        // Runs on every visit of a demo, but wants only the one the shell has open, so it rides the open's
        // parse and never makes the library read a demo for it.
        contributions.Pass(FrameCountPass.PassId, () => new FrameCountPass(services.GetRequiredService<HelloTabViewModel>(), context.Data));

        // The host runs the ruleset with the highlights on every demo while the extension is on and keeps its
        // table as library facts. extension.json lists it under "rulesets" too.
        contributions.Ruleset(new RulesetContribution(HelloFacts.RulesetName,
            () => typeof(HelloExtension).Assembly.GetManifestResourceStream("HelloExtension.kills.rules.yaml")!));

        // A job that names the demo runs on that demo's parse: the shell's when the demo is open. The chip
        // shows the demo greeted; it may be told from any thread.
        contributions.DemoAction(new DemoAction(ExtensionId + ".greet", "Say hello", "Greets this demo with its frame count",
            _ => true,
            path =>
            {
                int frames = 0;
                IJobHandle handle = context.Jobs.Enqueue(JobRequest.OnDemo("Hello: greet", path, job =>
                {
                    frames = job.Parsed.Frames.Count;
                    return Task.CompletedTask;
                }, new JobOptions(Priority: JobPriority.UserRequested)));
                handle.Completed += result =>
                {
                    bool read = result.Status == JobStatus.Completed;
                    services.GetRequiredService<HelloTabViewModel>().Greeted =
                        context.Settings.Get(GreetingKey, "Hello") + " "
                        + (read ? $"{Path.GetFileName(path)} ({frames} frames)" : Path.GetFileName(path));
                    chip.Greeted(Path.GetFileName(path));

                    // One id, so greeting again replaces the card rather than stacking another.
                    context.Notifications.Post(new Notification(GreetedNotification,
                        read ? NotificationSeverity.Success : NotificationSeverity.Warning,
                        read ? $"Greeted {Path.GetFileName(path)}" : $"Could not read {Path.GetFileName(path)}",
                        read ? $"{frames} frames." : "The greeting went out without a frame count.")
                    {
                        Action = new NotificationAction("Show", () => context.Shell.SelectTab("hello.tab")),
                        TimeToLive = TimeSpan.FromSeconds(30)
                    });
                };
            }));
        contributions.Playback(new HelloPlayback());
    }
}

/// <summary>The tab's state: the open demo, its frame count and the last greeting.</summary>
public sealed class HelloTabViewModel : IWorkspaceTabViewModel
{
    private readonly IExtensionContext _context;
    private volatile string? _openDemo;
    private string? _greeted;
    private int? _kills;

    public HelloTabViewModel(IExtensionContext context)
    {
        _context = context;
        _context.Shell.CurrentDemoChanged += () =>
        {
            _openDemo = _context.Shell.CurrentDemoPath;
            // The open's visit may have been planned before this ran: ask again, which runs on the parse the
            // shell holds and reads nothing.
            if (_openDemo is { } path && !HasFrameCount(path))
            {
                _context.Passes.Request(path);
            }

            ReadKills();
            Changed?.Invoke();
        };
        _context.Library.Changed += change =>
        {
            if (change.Path is null || IsOpenDemo(change.Path))
            {
                ReadKills();
            }
        };
        _context.Settings.Changed += _ => Changed?.Invoke();
        _context.Data.Changed += _ => Changed?.Invoke();
        // The user's accounts are the host's, edited under Settings, Identity; an extension only reads them.
        _context.Identity.Changed += () => Changed?.Invoke();
        _context.Library.Players.Changed += () => Changed?.Invoke();
    }

    /// <summary>The open demo, kept in memory so a pass can read it off the UI thread.</summary>
    public string? OpenDemo => _openDemo;

    public string? Greeted
    {
        get => _greeted;
        set
        {
            _greeted = value;
            Changed?.Invoke();
        }
    }

    public string Text
    {
        get
        {
            string? open = _context.Shell.CurrentDemoPath;
            string frames = open is not null && _context.Settings.Get(HelloExtension.ShowFramesKey, true)
                                             && FrameCountPass.Count(_context.Data, open) is { } count
                ? $" ({count} frames)"
                : "";
            string kills = _kills is { } total ? $" {total} kills." : "";
            string demos = You is { } you && _context.Library.Players.Account(you) is { } account
                ? $" ({account.DemoCount} demos)"
                : "";
            return $"Open demo: {open ?? "none"}{frames}.{kills} Last greeted: {Greeted ?? "nobody"}. You: {_context.Identity.ActiveAccount ?? "not set"}{demos}.";
        }
    }

    private ulong? You => ulong.TryParse(_context.Identity.ActiveAccount, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : null;

    /// <summary>Opens the user's active account in the host's Player Overview. False when the host could not.</summary>
    public bool OpenYou() => You is { } you && _context.Shell.OpenPlayerOverview(you);

    // The table is a file read, so it runs as a job; the status check before it reads only the index.
    private void ReadKills()
    {
        string? path = _openDemo;
        _kills = null;
        if (path is null || !_context.Library.Facts.IsCurrent(path, HelloFacts.Key))
        {
            return;
        }

        _ = _context.Jobs.RunAsync("Hello: read kills", _ =>
        {
            int? kills = HelloFacts.TotalKills(_context.Library.Facts, path);
            _context.Post(() =>
            {
                if (string.Equals(path, _openDemo, StringComparison.OrdinalIgnoreCase))
                {
                    _kills = kills;
                    Changed?.Invoke();
                }
            });
            return Task.CompletedTask;
        });
    }

    // A change names the path the library shows the demo at, and the shell may have opened another copy of it.
    private bool IsOpenDemo(string path) =>
        _openDemo is { } open
        && (string.Equals(path, open, StringComparison.OrdinalIgnoreCase)
            || _context.Library.Find(open)?.Locations.Contains(path, StringComparer.OrdinalIgnoreCase) == true);

    // The stamp comes from the store's index, so this opens no file and is safe on the UI thread.
    public bool HasFrameCount(string path) => FrameCountPass.Count(_context.Data, path) is not null;

    public event Action? Changed;

    public void OnActivated(IModuleContext context) => Changed?.Invoke();

    public void OnDeactivated()
    {
    }

    public object? SnapshotState() => null;

    public void RestoreState(object? state)
    {
    }
}

/// <summary>The extension's ruleset's output as the library keeps it.</summary>
public static class HelloFacts
{
    /// <summary>The extension's own name for its ruleset.</summary>
    public const string RulesetName = "kills";

    /// <summary>The table the ruleset declares: one row per player, the match's kills.</summary>
    public static FactKey Key { get; } = new(RulesetContribution.QualifiedId(HelloExtension.ExtensionId, RulesetName), "hello_kills");

    /// <summary>Every player's kills in the demo summed, or null when the library has not written the table.</summary>
    public static int? TotalKills(IAnalysisFacts facts, string demoPath) =>
        facts.TryGet(demoPath, Key) is { } table ? (int)table.Rows.Sum(r => r.Values.GetValueOrDefault("kills")?.AsNumber() ?? 0) : null;
}

/// <summary>
///     Counts the frames of the demo the shell has open, on whatever read its visit takes, and keeps the count
///     as the demo's own data: the next session finds it without reading the demo again, and so does a copy of
///     the demo at another path, since the data keys on the demo's content.
/// </summary>
internal sealed class FrameCountPass(HelloTabViewModel tab, IExtensionDemoData data) : IExtensionPass
{
    public const string PassId = "dev.example.hello.frames";

    private const string Facet = "frames";
    private const int Schema = 1;
    private const string Fingerprint = "frames-1";

    public string Id => PassId;

    public bool ReadsUserCommands => false;

    public PassRead Read => PassRead.Forward;

    /// <summary>The demo's frame count from the store's index, or null when it was never counted.</summary>
    public static int? Count(IExtensionDemoData data, string demoPath) =>
        data.Stamp(demoPath, Facet) is { } stamp && stamp.IsCurrent(Schema, Fingerprint) ? stamp.Count : null;

    public DemoInterest Interest(string demoPath) =>
        string.Equals(demoPath, tab.OpenDemo, StringComparison.OrdinalIgnoreCase) && Count(data, demoPath) is null
            ? DemoInterest.Yes
            : DemoInterest.No;

    public void Run(IPassContext context)
    {
        int frames = context.FrameCount;
        data.Write(context.DemoPath, new DemoDataWrite(Facet, Schema, Fingerprint,
            System.Text.Encoding.UTF8.GetBytes($"{{\"frames\":{frames}}}")) { Count = frames });
    }
}

internal sealed class HelloModule(Func<HelloTabViewModel> viewModel) : IWorkspaceModule
{
    public string Id => "dev.example.hello.tabs";

    public string DisplayName => "Hello";

    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = "hello.tab",
            Header = "Hello",
            Order = 50,
            FeatureId = HelloExtension.TabFeature,
            ViewModelFactory = viewModel,
            ViewFactory = () =>
            {
                HelloTabViewModel vm = viewModel();
                TextBlock text = new() { Text = vm.Text, Margin = new Avalonia.Thickness(16) };
                vm.Changed += () => text.Text = vm.Text;
                Button you = new() { Content = "Open me in Player Overview", Margin = new Avalonia.Thickness(16, 0) };
                you.Click += (_, _) => vm.OpenYou();
                return new StackPanel { Orientation = Orientation.Vertical, Children = { text, you } };
            }
        };

        // A map of its own: the host loads the map's art, the extension adds a layer by composition.
        yield return new WorkspaceTabDescriptor
        {
            TabId = "hello.map",
            Header = "Hello map",
            Order = 52,
            FeatureId = HelloExtension.TabFeature,
            ViewFactory = () =>
            {
                MapView map = new() { MapName = "de_mirage" };
                map.AddLayer("hello.origin", () => new HelloLayer("hello.origin"));
                return map;
            }
        };

        // A scene of its own: frames the extension builds, drawn the way the 2D Playback tab draws a demo,
        // with a layer and a pointer tool of the extension's over them.
        yield return new WorkspaceTabDescriptor
        {
            TabId = "hello.scene",
            Header = "Hello scene",
            Order = 54,
            FeatureId = HelloExtension.TabFeature,
            ViewFactory = () =>
            {
                HelloScene scene = new();
                SceneView view = new() { Source = scene };
                view.AddLayer("hello.rings", () => new HelloLayer("hello.rings"));
                view.AddTool(scene.Pin);
                view.SetActiveTool(scene.Pin.Kind);
                return view;
            }
        };
    }
}

/// <summary>
///     Draws a cross at the world origin and a ring round every living player. It reads only what it is handed:
///     the frame and the transform in the render context.
/// </summary>
internal sealed class HelloLayer(string id) : ISceneLayer
{
    private readonly SKPaint _paint = new() { Color = new SKColor(0xFF, 0xC8, 0x40), IsAntialias = true, IsStroke = true, StrokeWidth = 2 };

    public string Id => id;

    public LayerSlot Slot => LayerSlot.Overlay;

    public int Order => 0;

    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    public bool IsEnabled { get; set; } = true;

    public int ContentVersion => 0;

    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        (double x, double y) = ctx.Transform.WorldToScreen(0, 0);
        canvas.DrawLine((float)x - 8, (float)y, (float)x + 8, (float)y, _paint);
        canvas.DrawLine((float)x, (float)y - 8, (float)x, (float)y + 8, _paint);
        foreach (PlayerMarker marker in ctx.Frame.Markers)
        {
            if (marker.IsAlive && ctx.BelongsHere(marker.WorldZ))
            {
                (double px, double py) = ctx.Transform.WorldToScreen(marker.WorldX, marker.WorldY);
                canvas.DrawCircle((float)px, (float)py, 14, _paint);
            }
        }
    }

    public void Dispose() => _paint.Dispose();
}

/// <summary>
///     What the Hello scene tab draws: frames of the extension's own making rather than the open demo's, with
///     one marker the pin tool moves. A move publishes a new frame instead of changing the last one, which the
///     render thread may still be replaying.
/// </summary>
internal sealed class HelloScene : ISceneSource
{
    private const string Mirage = "de_mirage";

    // Read once per scene: Describe decodes nothing, but it answers null outside the app, where the map's
    // own radar rectangle stands in.
    private readonly WorldBounds _extent =
        MapAssets.Describe(Mirage)?.RadarBounds ?? new WorldBounds(-3230, -3410, 1860, 1680);

    public HelloScene()
    {
        Pin = new HelloPinTool(Publish);
        Frame = Build(new SKPoint(0, 0));
    }

    /// <summary>The tool that moves the marker; the view selects it by its kind.</summary>
    public HelloPinTool Pin { get; }

    public Scene2DFrame Frame { get; private set; }

    /// <summary>Null draws the grid. Art comes from a MapAssets.Acquire lease, held for as long as it draws.</summary>
    public IMapAsset? MapAsset => null;

    /// <summary>No ink, so the view mounts none of its drawing tools and the pin tool sees every press.</summary>
    public AnnotationSession? Ink => null;

    public event Action? FrameUpdated;

    // Called by the tool, which the view runs on the UI thread.
    private void Publish(SKPoint pin)
    {
        Frame = Build(pin);
        FrameUpdated?.Invoke();
    }

    private Scene2DFrame Build(SKPoint pin) => new()
    {
        Markers = [new PlayerMarker(0, 2, pin.X, pin.Y, 64f, 0f, RingState.Team, 1, "Hello", true)],
        Map = new SceneMapInfo { MapName = Mirage, ObservedBounds = _extent, NetworkedBounds = _extent }
    };
}

/// <summary>
///     Puts the scene's marker where the pointer pressed and keeps it under the pointer while the button is
///     down. A cancelled gesture puts it back: the router cancels when the active tool changes under an open
///     gesture, and SceneView.CancelGesture does it from a key handler of your own.
/// </summary>
internal sealed class HelloPinTool(Action<SKPoint> moved) : IPointerTool
{
    private SKPoint _pin;
    private SKPoint? _pressedFrom;

    /// <summary>Map and Token are the two kinds the scene does not register a tool of its own under.</summary>
    public ToolKind Kind => ToolKind.Map;

    public bool OnPressed(in ToolPointerEvent e, IToolServices s)
    {
        if (e.Pane is null || e.Button != ToolPointerButton.Left)
        {
            return false;
        }

        _pressedFrom = _pin;
        Move(e.World);
        return true;
    }

    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
        if (_pressedFrom is not null && e.Pane is not null)
        {
            Move(e.World);
        }
    }

    public void OnReleased(in ToolPointerEvent e, IToolServices s) => _pressedFrom = null;

    public void OnCancelled(IToolServices s)
    {
        if (_pressedFrom is { } back)
        {
            Move(back);
            _pressedFrom = null;
        }
    }

    private void Move(SKPoint world)
    {
        _pin = world;
        moved(world);
    }
}

/// <summary>The hub's two sections. Plain controls, so the sections need nothing from the container.</summary>
internal sealed class HelloHubModule : IWorkspaceModule
{
    public string Id => "dev.example.hello.hub";

    public string DisplayName => "Hello hub";

    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return Section("hello.hub.first", "First", 0);
        yield return Section("hello.hub.second", "Second", 1);
    }

    private static WorkspaceTabDescriptor Section(string id, string header, int order) => new()
    {
        TabId = id,
        Header = header,
        Order = order,
        HostId = HelloExtension.HubId,
        ViewFactory = () => new TextBlock { Text = $"{header} section", Margin = new Avalonia.Thickness(16) }
    };
}

/// <summary>A status chip that appears after the first greeting and names the demo greeted.</summary>
public sealed class HelloChip : IStatusChipSource
{
    private string? _greeted;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsShown => _greeted is not null;

    public string Label => $"Hello · {_greeted}";

    public StatusChipDotState DotState => StatusChipDotState.Good;

    public bool IsPulsing => false;

    public bool IsHollow => false;

    public string? Tooltip => "The last demo the Hello extension greeted";

    public ICommand? PrimaryAction => null;

    public object? FlyoutContent => null;

    /// <summary>Shows the chip for <paramref name="demo" />. Any thread: the host re-reads on the UI thread.</summary>
    public void Greeted(string demo)
    {
        _greeted = demo;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

internal sealed class HelloPlayback : IPlaybackContribution
{
    private readonly HelloSpanTrack _span = new();
    private IDisposable? _item;
    private IDisposable? _layer;
    private IDisposable? _menu;
    private IDisposable? _mode;
    private ILaneHandle? _lane;

    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        // Naming the action makes Shift+H run the button too, while the extension is on.
        _item = surface.AddToolbarItem(new ToolbarItem(HelloExtension.ExtensionId + ".where", "Where am I?",
            $"Logs the tick shown{surface.GestureHint(HelloExtension.WhereAction)}",
            moment => moment.Tick >= 0, HelloExtension.WhereAction));
        // Filed by the host as ext.dev.example.hello.rings, beside the tab's own layers. The name is
        // the label on the layer's own visibility row; nothing here remembers the user's choice.
        _layer = surface.AddLayer("rings", () => new HelloLayer("rings"), "Hello rings");

        // The behaviour reaches the handle through a lambda because the handle is what AddLane returns.
        _lane = surface.AddLane(_span, new HelloLane(_span, () => _lane));

        // One lane row is shared, and a press on it goes to the first lane that takes edits, so this one
        // takes them only while the extension's own mode is on.
        ModeToggle mode = new(HelloExtension.ExtensionId + ".span", "Hello span", "Mark a span on the Hello lane");
        mode.Changed += () => Editing(mode.IsOn);
        _mode = surface.AddModeToggle(mode);

        // A band-menu contributor is asked about every band on the timeline, so the lane's own are told
        // apart by the id the host filed the lane under, never the id the track asked for.
        _menu = surface.AddBandMenu(band => band.TrackId == _lane?.TrackId
            ? [new MenuEntry("Clear the Hello span", Clear)]
            : []);
    }

    public void Detach()
    {
        _menu?.Dispose();
        _mode?.Dispose();
        _lane?.Dispose();
        _item?.Dispose();
        _layer?.Dispose();
        _menu = null;
        _mode = null;
        _lane = null;
        _item = null;
        _layer = null;
    }

    // The handles are the lane's, the band is the track's, so both have to go.
    private void Clear()
    {
        _span.Set(null);
        if (_lane is { } lane)
        {
            lane.EditSpan = null;
        }
    }

    private void Editing(bool on)
    {
        if (_lane is { } lane)
        {
            lane.IsEditable = on;
            if (!on)
            {
                lane.EditSpan = null;
            }
        }
    }
}

/// <summary>
///     The Hello lane's one band: the span the user marked on it. A track is otherwise stateless about the
///     demo, everything coming from the data it is handed, and the host re-queries both lists on Changed.
/// </summary>
internal sealed class HelloSpanTrack : ITimelineTrack
{
    private (int Start, int End)? _span;

    public string Id => "span";

    public string DisplayName => "Hello span";

    public event Action? Changed;

    /// <summary>The lane is the extension's own, so no demo is without it.</summary>
    public bool IsAvailable(ITimelineData data) => true;

    public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) => [];

    public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data)
    {
        // The span came off the lane, so it is already in frame indices, but the demo opened after it may be
        // the shorter one. Argb 0 leaves the colour to the host.
        if (data is null || _span is not { } span || data.TotalFrames == 0)
        {
            return [];
        }

        int last = data.TotalFrames - 1;
        int start = Math.Clamp(span.Start, 0, last);
        return [new TimelineBand(Id, start, Math.Clamp(span.End, start, last), "Hello", "The span you marked", 0)];
    }

    /// <summary>Sets the span the band draws, or clears it with null.</summary>
    public void Set((int Start, int End)? span)
    {
        _span = span;
        Changed?.Invoke();
    }
}

/// <summary>
///     The Hello lane's presses: a press on the empty lane starts a span, and dragging its two handles moves
///     it. The host writes the dragged span onto the handle itself before it calls here.
/// </summary>
internal sealed class HelloLane(HelloSpanTrack track, Func<ILaneHandle?> lane) : ILaneBehaviour
{
    // Frame indices, not ticks: the lane's x axis is the frame.
    private const int StartingFrames = 128;

    public void OnLabelRequested(int frame)
    {
        if (lane() is { } handle)
        {
            handle.EditSpan = (frame, frame + StartingFrames);
        }

        track.Set((frame, frame + StartingFrames));
    }

    public void OnEditSpanDragged(int startFrame, int endFrame) => track.Set((startFrame, endFrame));
}
