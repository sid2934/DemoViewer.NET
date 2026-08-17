#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     VS Code-style docked Output panel. DataContext is an
///     <see cref="ViewModels.Diagnostics.OutputPanelViewModel" />; channels surface
///     unknown-message-type warnings, decode errors, tracker errors, and build/test output.
/// </summary>
public partial class OutputPanel : UserControl
{
    /// <summary>Initializes a new <see cref="OutputPanel" /> instance.</summary>
    public OutputPanel() => InitializeComponent();
}
