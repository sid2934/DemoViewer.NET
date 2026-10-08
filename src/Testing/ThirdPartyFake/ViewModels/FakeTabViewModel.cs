using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.Sdk.Ui;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Modules.Abstractions;

namespace ThirdPartyFake.ViewModels;

/// <summary>The tab: a status chip, a key/value table and a nested detail resolved by naming.</summary>
public sealed class FakeTabViewModel : ExtensionViewModel, IWorkspaceTabViewModel
{
    private int _activations;

    public StatusChipViewModel Status { get; } = new()
    {
        DotState = StatusChipDotState.Good,
        Label = "Fake · ready",
        Tooltip = "The third-party fake's own chip"
    };

    public IReadOnlyList<KvpRow> Rows { get; } =
    [
        new("Extension", FakeExtension.ExtensionId, false, null),
        new("Namespace", typeof(FakeTabViewModel).Namespace!, false, null),
        new("Activations", "0", true, "-")
    ];

    public FakeDetailViewModel Detail { get; } = new();

    public int Activations
    {
        get => _activations;
        private set => SetProperty(ref _activations, value);
    }

    public void OnActivated(IModuleContext context) => Activations++;

    public void OnDeactivated()
    {
    }

    public object? SnapshotState() => null;

    public void RestoreState(object? state)
    {
    }
}
