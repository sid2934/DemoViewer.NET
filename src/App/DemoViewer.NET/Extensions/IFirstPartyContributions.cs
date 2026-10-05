#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     A processing-queue job kind's label, scheduling rank, light-slot flag and owner tag, resolved by
///     <see cref="JobKindRegistry" />. The kind's identity stays the <see cref="QueueJobKind" /> enum
///     member (what a job carries); this is only its metadata.
/// </summary>
/// <param name="Kind">The kind this describes.</param>
/// <param name="Label">Row label in the queue UI.</param>
/// <param name="Rank">Scheduling rank among kinds; lower runs first.</param>
/// <param name="IsLight">True when the job needs no heavy slot and may run beside a parse.</param>
/// <param name="ExtensionKind">For <see cref="QueueJobKind.Extension" />, the extension's kind id; null for a core kind.</param>
public sealed record JobKindDescriptor(QueueJobKind Kind, string Label, int Rank, bool IsLight, string? ExtensionKind = null);

/// <summary>A pass a pack adds to every demo's visit, ordered after the pass ids it reads from.</summary>
/// <param name="Id">
///     The id the built pass reports through <see cref="IDemoPass.Id" />. Declared up front so the run order
///     can be sorted and validated without constructing a disabled pack's passes.
/// </param>
/// <param name="Factory">Builds or resolves the pass; invoked only while the owning pack is enabled.</param>
/// <param name="After">Pass ids whose writes this one reads in the same visit.</param>
public sealed record PassContribution(string Id, Func<IDemoPass> Factory, IReadOnlyList<string> After);

/// <summary>One record pass a pack registered.</summary>
/// <param name="Id">The pass's id.</param>
/// <param name="Factory">Builds or returns the pass.</param>
/// <param name="Guard">The pack's guard, which the host runs the pass under.</param>
internal sealed record RecordPassContribution(string Id, Func<IExtensionRecordPass> Factory, ExtensionGuard Guard);

/// <summary>An extension's Match Overview action and the feature it shows under.</summary>
public sealed record GatedDemoAction(DemoAction Action, string FeatureId)
{
    /// <summary>
    ///     Runs a host handler for <see cref="DemoAction.Changed" /> on the UI thread, since an extension may
    ///     call <see cref="DemoAction.NotifyChanged" /> from any thread. Null runs it on the raising thread.
    /// </summary>
    public Action<Action>? ToUiThread { get; init; }
}

/// <summary>
///     What a first-party extension can contribute beyond the SDK: surfaces whose types are the app's own and
///     not part of the public contract. The host's contribution collector implements it; a first-party
///     extension reaches it by casting the <see cref="IExtensionContributions" /> it is handed.
/// </summary>
public interface IFirstPartyContributions : IExtensionContributions
{
}

/// <summary>
///     Shell state a first-party extension reads that the SDK does not publish: whether a Live Sync session or
///     a reel render holds the machine, which an export must not start beside. Resolved from the container.
/// </summary>
public interface IFirstPartyShellState
{
    /// <summary>True while a Live Sync session is connected to the game.</summary>
    bool IsLiveSyncSessionActive { get; }

    /// <summary>True while a reel render is running.</summary>
    bool IsReelJobRunning { get; }
}

/// <summary>
///     The status-strip chip of a first-party export job. The host owns the chip: shown while the job runs
///     or until the user dismisses its result, and only while <c>featureId</c> is on.
/// </summary>
public interface IFirstPartyExportChips
{
    /// <summary>Mounts an export job's status under <paramref name="chipId" />, replacing what was mounted there.</summary>
    /// <param name="chipId">The chip's id. One chip per id.</param>
    /// <param name="featureId">The feature the chip shows under.</param>
    /// <param name="status">The status the export job publishes.</param>
    void Mount(string chipId, string featureId, Playback2DExportStatusViewModel status);
}
