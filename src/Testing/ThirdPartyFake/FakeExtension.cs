using Avalonia.Controls;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ThirdPartyFake.ViewModels;

namespace ThirdPartyFake;

/// <summary>One tab whose content is a bare <see cref="ContentControl" /> over its view model.</summary>
public sealed class FakeExtension : IExtension
{
    public const string ExtensionId = "dev.example.thirdpartyfake";
    public const string MasterSwitch = "pack.thirdpartyfake";
    public const string TabFeature = "tab.thirdpartyfake";

    public string Id => ExtensionId;

    public string FeatureId => MasterSwitch;

    public IEnumerable<ExtensionFeature> Features =>
    [
        new(MasterSwitch, ExtensionFeatureKind.Extension, "Third Party Fake", "A test extension.", null, AudienceDefaults.Everyone),
        new(TabFeature, ExtensionFeatureKind.Tab, "Fake tab", "Shows the UI kit.", MasterSwitch, AudienceDefaults.Everyone)
    ];

    public void Register(IServiceCollection services) => services.AddSingleton<FakeTabViewModel>();

    public void Contribute(IExtensionContributions contributions, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        contributions.Tabs(new FakeModule(services.GetRequiredService<FakeTabViewModel>));
    }
}

internal sealed class FakeModule(Func<FakeTabViewModel> viewModel) : IWorkspaceModule
{
    public string Id => "dev.example.thirdpartyfake.tabs";

    public string DisplayName => "Third Party Fake";

    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = "thirdpartyfake.tab",
            Header = "Fake",
            Order = 90,
            FeatureId = FakeExtension.TabFeature,
            ViewModelFactory = viewModel,
            ViewFactory = () => new ContentControl { Content = viewModel() }
        };
    }
}
