#region

using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.Controls.Stats;
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
    ///     Damage-strip deep link. The strip is one drawn control rather than a bar per round, so the tap
    ///     arrives as a position: <see cref="Sparkline.IndexAt" /> turns it into a series index and
    ///     <c>RoundNumbers</c> turns that back into the round the user actually clicked.
    ///     <para>
    ///         This replaced a <c>Polyline</c> whose <c>Points</c> binding needed a null-object, because
    ///         Avalonia's <c>PolylineGeometry</c> throws on a null point list DURING RENDER and the view
    ///         model pushes null mid-transition. The Sparkline treats a null series as an empty one, so
    ///         the null-object is gone with it.
    ///     </para>
    /// </summary>
    private void OnFormBarTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PlayerDetailsViewModel vm || sender is not Sparkline strip)
        {
            return;
        }

        int index = strip.IndexAt(e.GetPosition(strip));
        if (index >= 0 && index < vm.Form.RoundNumbers.Count)
        {
            vm.SelectRoundFromForm(vm.Form.RoundNumbers[index]);
        }
    }
}
