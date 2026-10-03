#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.StratBook;

/// <summary>
///     Create Strat From Round's review pane. Named for the <c>ViewLocator</c>'s <c>…ViewModel</c> → <c>…View</c>
///     mapping, so <c>CreateStratDialogViewModel</c> resolves to it with no registration. No code-behind: the walk,
///     the rebuilds and the save are all the view-model's.
/// </summary>
public partial class CreateStratDialogView : UserControl
{
    /// <summary>Initializes a new <see cref="CreateStratDialogView" /> instance.</summary>
    public CreateStratDialogView() => InitializeComponent();
}
