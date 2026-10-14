#region

using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;

/// <summary>
///     Suggested Tags' thrower fix-up on the rules run: every molotov and decoy projectile on the frame it left,
///     for <see cref="ProjectileThrowerMatch" /> to name the thrower of an <c>inferno_startburn</c> or
///     <c>decoy_started</c> the wire leaves without one (#56, #59). Puts a <see cref="List{T}" /> of
///     <see cref="RemovedProjectile" /> in read order.
/// </summary>
public sealed class ThrowerCollector : IFrameCollector
{
    /// <summary>The collector's id, which Suggested Tags reads the rows by.</summary>
    public const string CollectorId = "stratbook.suggestedtags.thrower";

    /// <summary>Bumped when the rows it puts change.</summary>
    public const int Version = 1;

    // Only the two classes whose detonation events lack a thrower slot.
    private static readonly string[] Classes = [GrenadeProjectileClasses.Molotov, GrenadeProjectileClasses.Decoy];

    private readonly List<RemovedProjectile> _removed = [];

    /// <summary>The registration: no providers, the molotov and decoy projectile classes.</summary>
    public static CollectorContribution Contribution { get; } =
        new(CollectorId, SuggestedTagsService.EvaluatorId, Version, [], Classes, () => new ThrowerCollector());

    /// <inheritdoc />
    public void OnFrame(IFrameReadView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        foreach (ObservedProjectile row in view.Removed)
        {
            if (Array.IndexOf(Classes, row.ClassName) < 0)
            {
                continue;
            }

            _removed.Add(new RemovedProjectile(view.Tick, row.ClassName, row.ThrowerSlot, row.Position));
        }
    }

    /// <inheritdoc />
    public void Finish(ICollectorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Put(_removed);
    }
}
