#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.Views.Diagnostics;

/// <summary>
///     Dev-only component gallery for the stats library (docs/ui/stats-components.md), hosted in the
///     Diagnostics tab and therefore already covered by the <c>tab.diagnostics</c> gate.
///     <para>
///         It builds its OWN view model rather than binding through <c>DiagnosticsTabViewModel</c>. The
///         gallery is a design surface with no product data behind it, so wiring it into the diagnostics
///         graph would put slider state on a view model that reports on the app, and would make the
///         panel impossible to drop later without editing that class.
///     </para>
/// </summary>
public partial class StatsGalleryView : UserControl
{
    /// <summary>Initializes the gallery and its self-owned view model.</summary>
    public StatsGalleryView()
    {
        InitializeComponent();
        DataContext = new StatsGalleryViewModel();
    }
}
