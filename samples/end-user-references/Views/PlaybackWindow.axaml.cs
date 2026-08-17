#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.Views;

public partial class PlaybackWindow : Window
{
    public PlaybackWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as PlaybackViewModel)?.Detach();
        base.OnClosed(e);
    }
}
