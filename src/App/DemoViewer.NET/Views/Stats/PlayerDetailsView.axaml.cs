#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.Views.Stats;

/// <summary>
///     Player-details dashboard view. Hosted as an inline
///     overlay inside <see cref="StatsTabView" />, never a separate Window (WASM parity).
///     Code-behind exists only for the form-card round deep-link (design P-3 interaction):
///     tapping a damage bar jumps to the Rounds sub-section with that round highlighted.
/// </summary>
public partial class PlayerDetailsView : UserControl
{
    /// <summary>Initializes the view.</summary>
    public PlayerDetailsView()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     Null-object for the kills sparkline's Points binding: when <c>Form</c> is null
    ///     mid-transition the binding pushes null, and Avalonia's PolylineGeometry constructor
    ///     throws on a null point list DURING RENDER, crashing whatever compositor commit
    ///     happens to run (observed as an unrelated headless test failing on this view's
    ///     teardown state). Bound via <c>TargetNullValue</c> in the AXAML.
    /// </summary>
    public static Points EmptyPoints { get; } = [];

    private void OnFormBarTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PlayerDetailsViewModel vm && sender is Control { DataContext: FormBar bar })
        {
            vm.SelectRoundFromForm(bar.Round);
        }
    }
}
