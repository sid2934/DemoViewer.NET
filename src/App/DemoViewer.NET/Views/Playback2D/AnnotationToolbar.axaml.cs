#region

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

#endregion

namespace DemoViewer.NET.Views.Playback2D;

/// <summary>
///     The annotation toolbar. Purely declarative — every command and every piece of state lives on
///     <see cref="ViewModels.Playback2D.AnnotationsPanelViewModel" />, which is what lets the toolbar's
///     behaviour be tested without a window.
/// </summary>
public partial class AnnotationToolbar : UserControl
{
    /// <summary>Creates the toolbar.</summary>
    public AnnotationToolbar()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
