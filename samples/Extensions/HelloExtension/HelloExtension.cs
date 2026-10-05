using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Layout;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.Sdk.Playback;
using DemoViewer.NET.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace HelloExtension;

/// <summary>
///     A tab that shows the open demo, a pass that counts the open demo's frames, a Match Overview action that
///     greets a demo from a job on its parse, and a 2D Playback toolbar button.
/// </summary>
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

        // Runs on every visit of a demo, but wants only the one the shell has open, so it rides the open's
        // parse and never makes the library read a demo for it.
        contributions.Pass(FrameCountPass.PassId, () => new FrameCountPass(services.GetRequiredService<HelloTabViewModel>()));

        // A job that names the demo runs on that demo's parse: the shell's when the demo is open.
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
                handle.Completed += result => services.GetRequiredService<HelloTabViewModel>().Greeted =
                    result.Status == JobStatus.Completed ? $"{Path.GetFileName(path)} ({frames} frames)" : Path.GetFileName(path);
            }));
        contributions.Playback(new HelloPlayback());
    }
}

/// <summary>The tab's state: the open demo, its frame count and the last greeting.</summary>
public sealed class HelloTabViewModel : IWorkspaceTabViewModel
{
    private readonly IExtensionContext _context;
    private readonly ConcurrentDictionary<string, int> _frames = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _openDemo;
    private string? _greeted;

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

            Changed?.Invoke();
        };
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
            string frames = open is not null && _frames.TryGetValue(open, out int count) ? $" ({count} frames)" : "";
            return $"Open demo: {open ?? "none"}{frames}. Last greeted: {Greeted ?? "nobody"}.";
        }
    }

    public bool HasFrameCount(string path) => _frames.ContainsKey(path);

    // Called from the pass on a queue thread; the tab hears it on the UI thread.
    public void SetFrameCount(string path, int frames)
    {
        _frames[path] = frames;
        _context.Post(() => Changed?.Invoke());
    }

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

/// <summary>Counts the frames of the demo the shell has open, on the parse its open already read.</summary>
internal sealed class FrameCountPass(HelloTabViewModel tab) : IExtensionPass
{
    public const string PassId = "dev.example.hello.frames";

    public string Id => PassId;

    public bool ReadsUserCommands => false;

    public DemoInterest Interest(string demoPath) =>
        string.Equals(demoPath, tab.OpenDemo, StringComparison.OrdinalIgnoreCase) && !tab.HasFrameCount(demoPath)
            ? DemoInterest.Yes
            : DemoInterest.No;

    public void Run(IPassContext context) => tab.SetFrameCount(context.DemoPath, context.Parsed.Frames.Count);
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
