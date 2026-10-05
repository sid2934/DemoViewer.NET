using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.Sdk.Playback;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace HelloExtension;

/// <summary>
///     A tab that shows the open demo, a tab with a map of its own, a hub tab with two sections, a status chip,
///     a pass that counts the open demo's frames and keeps the count as the demo's own data, a ruleset whose
///     kills table the host keeps as library facts and the tab reads back, a settings page the host renders, a
///     Match Overview action that greets a demo from a job on its parse, and a 2D Playback toolbar button and
///     map layer.
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

    /// <summary>The hub tab's id, which its sections name as their host.</summary>
    public const string HubId = "hello.hub";

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
        services.AddSingleton(sp => new HelloTabViewModel(sp.GetExtensionContext(ExtensionId)));
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
        contributions.StatusChip(new StatusChipContribution("hello.chip", chip));

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
        contributions.DemoAction(new DemoAction("hello.greet", "Say hello", "Greets this demo with its frame count",
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
                    services.GetRequiredService<HelloTabViewModel>().Greeted =
                        context.Settings.Get(GreetingKey, "Hello") + " "
                        + (result.Status == JobStatus.Completed ? $"{Path.GetFileName(path)} ({frames} frames)" : Path.GetFileName(path));
                    chip.Greeted(Path.GetFileName(path));
                };
            }));
        contributions.Commands(Commands);
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
            if (change.Path is null || string.Equals(change.Path, _openDemo, StringComparison.OrdinalIgnoreCase))
            {
                ReadKills();
            }
        };
        _context.Settings.Changed += _ => Changed?.Invoke();
        _context.Data.Changed += _ => Changed?.Invoke();
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
            return $"Open demo: {open ?? "none"}{frames}.{kills} Last greeted: {Greeted ?? "nobody"}.";
        }
    }

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
///     Counts the frames of the demo the shell has open, on the parse its open already read, and keeps the count
///     as the demo's own data: the next session finds it without reading the demo again.
/// </summary>
internal sealed class FrameCountPass(HelloTabViewModel tab, IExtensionDemoData data) : IExtensionPass
{
    public const string PassId = "dev.example.hello.frames";

    private const string Facet = "frames";
    private const int Schema = 1;
    private const string Fingerprint = "frames-1";

    public string Id => PassId;

    public bool ReadsUserCommands => false;

    /// <summary>The demo's frame count from the store's index, or null when it was never counted.</summary>
    public static int? Count(IExtensionDemoData data, string demoPath) =>
        data.Stamp(demoPath, Facet) is { } stamp && stamp.IsCurrent(Schema, Fingerprint) ? stamp.Count : null;

    public DemoInterest Interest(string demoPath) =>
        string.Equals(demoPath, tab.OpenDemo, StringComparison.OrdinalIgnoreCase) && Count(data, demoPath) is null
            ? DemoInterest.Yes
            : DemoInterest.No;

    public void Run(IPassContext context)
    {
        int frames = context.Parsed.Frames.Count;
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
                return new StackPanel { Orientation = Orientation.Vertical, Children = { text } };
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
    private IDisposable? _item;
    private IDisposable? _layer;

    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        // Naming the action makes Shift+H run the button too, while the extension is on.
        _item = surface.AddToolbarItem(new ToolbarItem("hello.where", "Where am I?",
            $"Logs the tick shown{surface.GestureHint(HelloExtension.WhereAction)}",
            moment => moment.Tick >= 0, HelloExtension.WhereAction));
        // Filed by the host as ext.dev.example.hello.rings, beside the tab's own layers.
        _layer = surface.AddLayer("rings", () => new HelloLayer("rings"));
    }

    public void Detach()
    {
        _item?.Dispose();
        _layer?.Dispose();
        _item = null;
        _layer = null;
    }
}
