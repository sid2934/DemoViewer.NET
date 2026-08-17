#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.Settings;

/// <summary>Desktop host window for the Settings screen (wraps <see cref="SettingsView" />).</summary>
public partial class SettingsWindow : Window
{
    /// <summary>Initializes a new <see cref="SettingsWindow" /> instance.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }
}
