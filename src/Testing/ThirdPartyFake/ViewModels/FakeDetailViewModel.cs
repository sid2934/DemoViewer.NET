using DemoViewer.NET.Extensions.Sdk.Ui;

namespace ThirdPartyFake.ViewModels;

/// <summary>Hosted in a bare ContentControl, so it shows only if the host finds its view by naming.</summary>
public sealed class FakeDetailViewModel : ExtensionViewModel
{
    private string _title = "Resolved from ThirdPartyFake.Views";

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Notes { get; } = "Drawn with the **host's** tokens and classes.\n\n- `TextMid` label\n- `ghost` and `primary` buttons";
}
