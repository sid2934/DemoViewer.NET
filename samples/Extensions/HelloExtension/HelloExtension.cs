using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.Sdk.Playback;
using DemoViewer.NET.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace HelloExtension;

/// <summary>
///     A tab that shows the open demo, a hub tab with two sections, a status chip, a Match Overview action and
///     a 2D Playback toolbar button.
/// </summary>
public sealed class HelloExtension : IExtension
{
    public const string ExtensionId = "dev.example.hello";
    public const string MasterSwitch = "pack.hello";
    public const string TabFeature = "tab.hello";

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
        contributions.DemoAction(new DemoAction("hello.greet", "Say hello", "Logs a greeting for this demo",
            _ => true,
            path => _ = context.Jobs.RunAsync("Hello: greet", _ =>
            {
                context.Post(() =>
                {
                    services.GetRequiredService<HelloTabViewModel>().Greeted = Path.GetFileName(path);
                    chip.Greeted(Path.GetFileName(path));
                });
                return Task.CompletedTask;
            })));
        contributions.Commands(Commands);
        contributions.Playback(new HelloPlayback());
    }
}

/// <summary>The tab's state: the open demo and the last greeting.</summary>
public sealed class HelloTabViewModel : IWorkspaceTabViewModel
{
    private readonly IExtensionContext _context;

    public HelloTabViewModel(IExtensionContext context)
    {
        _context = context;
        _context.Shell.CurrentDemoChanged += () => Changed?.Invoke();
    }

    public string? Greeted { get; set; }

    public string Text => $"Open demo: {_context.Shell.CurrentDemoPath ?? "none"}. Last greeted: {Greeted ?? "nobody"}.";

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
    private IDisposable? _item;

    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        // Naming the action makes Shift+H run the button too, while the extension is on.
        _item = surface.AddToolbarItem(new ToolbarItem("hello.where", "Where am I?",
            $"Logs the tick shown{surface.GestureHint(HelloExtension.WhereAction)}",
            moment => moment.Tick >= 0, HelloExtension.WhereAction));
    }

    public void Detach()
    {
        _item?.Dispose();
        _item = null;
    }
}
