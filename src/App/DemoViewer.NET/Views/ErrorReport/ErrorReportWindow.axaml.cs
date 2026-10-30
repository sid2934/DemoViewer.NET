#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Views.ErrorReport;

/// <summary>The "Report Analysis Errors" window: a frame around <see cref="ErrorReportView" />.</summary>
public partial class ErrorReportWindow : Window
{
    /// <summary>Initializes a new <see cref="ErrorReportWindow" /> instance.</summary>
    public ErrorReportWindow()
    {
        InitializeComponent();
    }
}
