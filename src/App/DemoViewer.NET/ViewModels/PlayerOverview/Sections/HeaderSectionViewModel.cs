#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>Who the page is about: the subject's latest name, SteamID64 and demo count, and whether it is the user.</summary>
public sealed partial class HeaderSectionViewModel : PlayerOverviewSection
{
    [ObservableProperty]
    private string _demoCountText = "";

    [ObservableProperty]
    private bool _isMine;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _steamId64 = "";

    /// <param name="services">Unused until the header gains its actions.</param>
    public HeaderSectionViewModel(PlayerOverviewServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
    }

    /// <inheritdoc />
    public override string Id => "header";

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Player is not { } player)
        {
            IsVisible = false;
            return;
        }

        Name = player.Name.Length > 0 ? DisplayText.Sanitize(player.Name) : "Unnamed player";
        SteamId64 = player.SteamId64.ToString(CultureInfo.InvariantCulture);
        DemoCountText = player.DemoCount == 1 ? "1 demo" : string.Create(CultureInfo.InvariantCulture, $"{player.DemoCount} demos");
        IsMine = context.IsMine;
        IsVisible = true;
    }
}
