#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.Playback2D;

/// <summary>
///     The 2D video-export pane. Named for the <c>ViewLocator</c>'s <c>…ViewModel</c> → <c>…View</c>
///     mapping, so <c>Playback2DExportDialogViewModel</c> resolves to it with no registration.
///     <para>
///         No code-behind beyond initialisation: the view-model reaches nothing that needs a visual tree,
///         and the export's progress lives on the shell's status chip rather than in this pane.
///     </para>
/// </summary>
public partial class Playback2DExportDialogView : UserControl
{
    /// <summary>Initializes a new <see cref="Playback2DExportDialogView" /> instance.</summary>
    public Playback2DExportDialogView() => InitializeComponent();
}
