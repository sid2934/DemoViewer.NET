#region

using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's five processing-queue job kinds: label, rank, light and owner. One array read
///     by both <see cref="StratBookPack.JobKinds" /> (the DI-free source <c>JobKindRegistry</c> builds
///     from) and <see cref="StratBookPack.Contribute" />, so the two channels cannot drift apart. Owner
///     strings match <see cref="StratBookLifecycle.OwnerTags" /> exactly, so <c>CancelOwned(owner)</c> and
///     a future owner column agree with the kind.
/// </summary>
internal static class StratBookJobKinds
{
    public static readonly JobKindDescriptor[] All =
    [
        new(QueueJobKind.StratMining, "mining", 2, false, "strat-mining"),
        new(QueueJobKind.StratPreview, "preview", 2, false, "strat-mining"),
        new(QueueJobKind.LineupClips, "clips", 3, false, "lineup-clips"),
        new(QueueJobKind.SuggestionsInbox, "suggestions", 2, false, "suggested-inbox"),
        new(QueueJobKind.TeamsCommand, "teams", 4, true, "teams")
    ];
}
