using Avalonia.Controls;
using Avalonia.Layout;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.Sdk.Playback;
using DemoViewer.NET.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace HelloExtension;

/// <summary>A tab that shows the open demo, a Match Overview action and a 2D Playback toolbar button.</summary>
public sealed class HelloExtension : IExtension
{
    public const string ExtensionId = "dev.example.hello";
    public const string MasterSwitch = "pack.hello";
    public const string TabFeature = "tab.hello";

    public string Id => ExtensionId;

    public string FeatureId => MasterSwitch;

    public IEnumerable<ExtensionFeature> Features =>
    [
        new(MasterSwitch, ExtensionFeatureKind.Extension, "Hello", "A sample extension.", null, AudienceDefaults.Everyone),
        new(TabFeature, ExtensionFeatureKind.Tab, "Hello tab", "Shows the open demo.", MasterSwitch, AudienceDefaults.Everyone)
    ];

    public void Register(IServiceCollection services) =>
        services.AddSingleton(sp => new HelloTabViewModel(sp.GetExtensionContext(ExtensionId)));

    public void Contribute(IExtensionContributions contributions, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        IExtensionContext context = contributions.Context;
        contributions.Tabs(new HelloModule(services.GetRequiredService<HelloTabViewModel>));
        contributions.DemoAction(new DemoAction("hello.greet", "Say hello", "Logs a greeting for this demo",
            _ => true,
            path => _ = context.Jobs.RunAsync("Hello: greet", _ =>
            {
                context.Post(() => services.GetRequiredService<HelloTabViewModel>().Greeted = Path.GetFileName(path));
                return Task.CompletedTask;
            })));
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

internal sealed class HelloPlayback : IPlaybackContribution
{
    private IDisposable? _item;

    public void Attach(IPlaybackSurface surface, IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _item = surface.AddToolbarItem(new ToolbarItem("hello.where", "Where am I?", "Logs the tick shown",
            moment => moment.Tick >= 0));
    }

    public void Detach()
    {
        _item?.Dispose();
        _item = null;
    }
}
