#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Modules.RuleWorkbench;

/// <summary>
///     One selectable ruleset file in the Workbench's "Advanced Evaluate" multiselect — a
///     shipped or user <c>*.rules.yaml</c> the author can include in an evaluation.
/// </summary>
public sealed partial class EvaluableFile : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public required string FullPath { get; init; }

    /// <summary>Display label, e.g. <c>kast.rules.yaml (shipped)</c> or <c>draft.rules.yaml</c>.</summary>
    public required string Display { get; init; }

    public bool IsShipped { get; init; }
}
