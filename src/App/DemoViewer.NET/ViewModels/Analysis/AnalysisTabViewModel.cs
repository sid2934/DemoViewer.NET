#region

using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Models;
using DemoViewer.NET.ViewModels.Common;

#endregion

namespace DemoViewer.NET.ViewModels.Analysis;

/// <summary>
///     Coordinator for the Analysis Engine tab. Wraps the existing
///     <see cref="AnalysisViewModel" /> (the rule-evaluator driver) and owns the
///     seek coordination between Analysis and the frame list.
/// </summary>
/// <remarks>Initializes a new <see cref="AnalysisTabViewModel" /> instance.</remarks>
public sealed class AnalysisTabViewModel(FrameNavigationViewModel navigation) : ObservableObject
{
    /// <summary>The rule-evaluator driver. Existing UI binds to <c>AnalysisTab.Analysis.*</c>.</summary>
    public AnalysisViewModel Analysis { get; } = new();

    /// <summary>
    ///     True while an analysis-originated seek is in flight. The frame-list's
    ///     <c>OnSelectedFrameChanged</c> handler reads this to avoid round-tripping
    ///     the seek back into <see cref="AnalysisViewModel.SeekToFirstMessageOfFrame" />.
    /// </summary>
    public bool IsFrameSeekSuppressed { get; private set; }

    /// <summary>Navigation.</summary>
    public FrameNavigationViewModel Navigation { get; } = navigation;

    /// <summary>No durable Analysis-tab state to restore. Intentional no-op.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Uniform per-tab session contract; instance method by design.")]
    public void RestoreState(TabSessionState s)
    {
    }

    // ── Session state ─────────────────────────────────────────

    /// <summary>
    ///     The Analysis tab has no durable per-tab selection of its own — the active frame is
    ///     shell-driven and the rule results are recomputed on every load — so the snapshot is empty.
    ///     The method exists for a uniform per-tab persistence contract, hence the
    ///     stateless body (CA1822 suppressed deliberately — keeping it an instance method matches the
    ///     other tabs and lets the shell call <c>AnalysisTab.SnapshotState()</c> uniformly).
    /// </summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Uniform per-tab session contract; instance method by design.")]
    public TabSessionState SnapshotState() => new(
        null,
        null,
        false);

    /// <summary>
    ///     Run an analysis-originated frame seek with re-entry guard. The host
    ///     supplies <paramref name="apply" /> to perform the actual <c>SelectedFrame</c>
    ///     + <c>SeekControls.SetCurrentFrame</c> writes — this VM only owns the flag.
    /// </summary>
    internal void RunSuppressedFrameSeek(Action apply)
    {
        IsFrameSeekSuppressed = true;
        try
        {
            apply();
        }
        finally
        {
            IsFrameSeekSuppressed = false;
        }
    }
}
