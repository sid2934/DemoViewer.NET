#region

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

#endregion

namespace DemoViewer.NET.Views.RoundTagger;

/// <summary>Review mode's panel: the Suggested / Labels toggle, the shared editor and the Labels list.</summary>
public partial class ReviewPanelView : UserControl
{
    public ReviewPanelView() => AvaloniaXamlLoader.Load(this);
}
