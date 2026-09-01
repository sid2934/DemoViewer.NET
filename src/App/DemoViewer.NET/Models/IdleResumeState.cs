namespace DemoViewer.NET.Models;

/// <summary>
///     The transient session state captured ONCE when the app enters idle mode, so the demo it closes can be
///     restored exactly on Resume. Held in memory only for the idle→resume round-trip (never persisted: the
///     durable cross-launch snapshot is <see cref="SessionPayload" />).
///     <para>
///         Capturing the resume position at idle-entry (rather than tracking it continuously) is deliberate:
///         the demo is being torn down anyway, so there is one clean moment to record where playback/analysis
///         sat. <see cref="ResumeFrameIndex" /> is a frame index into the reopened demo's frame list, the
///         playback clock's own unit, not a CS2 demo tick.
///     </para>
/// </summary>
/// <param name="DemoPath">Local filesystem path of the demo that was open, reopened verbatim on Resume.</param>
/// <param name="ResumeFrameIndex">The playback frame index to seek back to after reopening (-1 = none).</param>
/// <param name="ActiveTabId">The <c>WorkspaceTabDescriptor.TabId</c> that was selected, reselected on Resume.</param>
public sealed record IdleResumeState(
    string DemoPath,
    int ResumeFrameIndex,
    string? ActiveTabId);
