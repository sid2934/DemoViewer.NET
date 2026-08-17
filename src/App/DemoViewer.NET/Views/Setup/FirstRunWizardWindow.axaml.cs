#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.Setup;

/// <summary>Desktop host window for the first-run wizard (wraps <see cref="FirstRunWizardView" />).</summary>
public partial class FirstRunWizardWindow : Window
{
    /// <summary>Initializes a new <see cref="FirstRunWizardWindow" /> instance.</summary>
    public FirstRunWizardWindow()
    {
        InitializeComponent();
    }
}
