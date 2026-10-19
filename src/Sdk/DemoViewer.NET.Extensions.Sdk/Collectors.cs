using System.Globalization;
using System.Numerics;
using System.Text;
using CS2DemoKit.Parser;
using CS2OpenSchema.Protos;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     A per-frame reader that rides the rules run the host already makes of a demo, registered with
///     <see cref="IExtensionContributions.Collector" />. It reads no demo of its own: the host declares its
///     providers and projectile classes on the run, calls <see cref="IFrameCollector.OnFrame" /> once per frame,
///     and hands its <see cref="IFrameCollector.Finish" /> result to the pass that owns it through
///     <see cref="IPassContext.Collected{T}" />.
/// </summary>
/// <param name="Id">Starts with the extension's id and a dot; the id the owning pass reads the result by.</param>
/// <param name="PassId">The <see cref="IExtensionPass.Id" /> that reads the result. The collector runs only on a visit that pass is on.</param>
/// <param name="Version">Bumped when what the collector writes changes; part of <see cref="Stamp" />.</param>
/// <param name="Providers">Per-player provider names the collector reads, from the engine's catalog (<c>entity.pawn.pos_x</c> ...).</param>
/// <param name="ProjectileClasses">Grenade projectile classes the collector reads (<c>CSmokeGrenadeProjectile</c> ...).</param>
/// <param name="Create">A fresh collector for one run.</param>
public sealed record CollectorContribution(
    string Id,
    string PassId,
    int Version,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> ProjectileClasses,
    Func<IFrameCollector> Create)
{
    /// <summary>
    ///     True when the collector reads <see cref="IFrameReadView.Command" />. The run then rebuilds the players'
    ///     commands, and a read that streams the demo decodes them; a collector that does not declare this gets
    ///     a throw from the read.
    /// </summary>
    public bool ReadsUserCommands { get; init; }

    /// <summary>
    ///     Game event names the collector reads off <see cref="IFrameReadView.Frame" />. A read that streams the
    ///     demo decodes only the events the rules subscribe to, plus these; a held parse carries every event.
    /// </summary>
    public IReadOnlyList<string> GameEvents { get; init; } = [];

    /// <summary>
    ///     What output written from this collector is current under: its version, its declared reads and the
    ///     engine build. A pass folds it into its own fingerprint, so a changed read set or engine re-runs it.
    /// </summary>
    public string Stamp
    {
        get
        {
            StringBuilder text = new();
            text.Append(Id).Append(";v=").Append(Version.ToString(CultureInfo.InvariantCulture));
            text.Append(";p=").AppendJoin(',', Providers.Order(StringComparer.Ordinal));
            text.Append(";c=").AppendJoin(',', ProjectileClasses.Order(StringComparer.Ordinal));
            if (ReadsUserCommands)
            {
                text.Append(";u=1");
            }

            if (GameEvents.Count > 0)
            {
                text.Append(";g=").AppendJoin(',', GameEvents.Order(StringComparer.Ordinal));
            }

            text.Append(";e=").Append(typeof(DemoFrame).Assembly.GetName().Version?.ToString() ?? "");
            return text.ToString();
        }
    }
}

/// <summary>One run's reader, made by <see cref="CollectorContribution.Create" />. Called on the evaluation thread only.</summary>
public interface IFrameCollector
{
    /// <summary>
    ///     One frame, in read order. The view is valid only during this call; copy what you keep. A throw ends
    ///     this collector for the run and fails its pass's read; other collectors and the run go on.
    /// </summary>
    void OnFrame(IFrameReadView view);

    /// <summary>After the last frame: put the result the owning pass reads. Not called when the run fails or a frame threw.</summary>
    void Finish(ICollectorSink sink);
}

/// <summary>Where <see cref="IFrameCollector.Finish" /> puts its result.</summary>
public interface ICollectorSink
{
    /// <summary>The result the owning pass gets from <see cref="IPassContext.Collected{T}" />. The last put wins.</summary>
    void Put<T>(T result) where T : class;
}

/// <summary>A declared provider resolved once through <see cref="IFrameReadView.Provider" />, for reads without the name lookup.</summary>
/// <param name="Name">The provider's catalog name.</param>
/// <param name="Index">The host's index for it; meaningful only to the view that resolved it.</param>
public readonly record struct ObservedRead(string Name, int Index);

/// <summary>
///     The state at the end of one frame, as the rules run folded it.
///     <para>
///         A slot read returns the last value the slot ever had, and nothing is ever cleared: a player who died,
///         left or has no pawn this frame still reads the values from before. A present value does not mean the
///         slot has a pawn on this frame, and a health or position read cannot tell a dead player from a live one.
///     </para>
/// </summary>
public interface IFrameReadView
{
    /// <summary>The frame just read.</summary>
    DemoFrame Frame { get; }

    /// <summary>The frame's index in read order, from zero; the index into the parse's frames on a retained read.</summary>
    int FrameIndex { get; }

    /// <summary>The frame clock: <c>Frame.GameTick ?? Frame.ServerTick</c>.</summary>
    int Tick { get; }

    /// <summary>Resolves a declared provider once. Throws for a name the collector did not declare.</summary>
    ObservedRead Provider(string providerName);

    /// <summary>A declared provider's value for a slot, boxed, or null when the slot never had one.</summary>
    object? Read(string providerName, int playerSlot);

    /// <summary>An int or bool provider's value (a bool reads as 0 or 1). False when the slot never had one.</summary>
    bool TryReadInt(ObservedRead provider, int playerSlot, out int value);

    /// <summary>A float provider's value. False when the slot never had one.</summary>
    bool TryReadFloat(ObservedRead provider, int playerSlot, out float value);

    /// <summary>A string provider's value. False when the slot never had one.</summary>
    bool TryReadString(ObservedRead provider, int playerSlot, out string? value);

    /// <summary>The slot's controller team as last seen (2 T, 3 CT), or -1.</summary>
    int Team(int playerSlot);

    /// <summary>
    ///     The slot's latest user command as of the end of this frame, or null before its first. One frame's
    ///     commands collapse to the last one; the instance is the engine's baseline for the next delta, so read
    ///     it and never change it. Throws unless a collector on the run declared
    ///     <see cref="CollectorContribution.ReadsUserCommands" />.
    /// </summary>
    CSGOUserCmdPB? Command(int playerSlot);

    /// <summary>Every live projectile of a declared class, ascending entity index. Empty when none was declared.</summary>
    IReadOnlyList<ObservedProjectile> Projectiles { get; }

    /// <summary>Projectiles of a declared class that appeared on this frame.</summary>
    IReadOnlyList<ObservedProjectile> Created { get; }

    /// <summary>Projectiles of a declared class that were live last frame and are gone on this one, with their last-seen state.</summary>
    IReadOnlyList<ObservedProjectile> Removed { get; }
}

/// <summary>One grenade projectile on one frame.</summary>
/// <param name="EntityIndex">The projectile's entity index.</param>
/// <param name="Serial">The entity serial; with the index, one projectile's identity.</param>
/// <param name="ClassName">The projectile class.</param>
/// <param name="ThrowerSlot">The thrower's player slot, held from its first resolution, or -1.</param>
/// <param name="Team">The projectile's team (2 T, 3 CT), or -1 when unseen.</param>
/// <param name="Position">World position, or null when unseen.</param>
/// <param name="InitialPosition">Where it was thrown from, when networked.</param>
/// <param name="InitialVelocity">Its velocity at the throw, when networked.</param>
/// <param name="Bounces">Bounces so far.</param>
/// <param name="SmokeEffectTickBegin">A smoke's cloud start server tick once it pops; 0 while it flies and for every other class.</param>
/// <param name="SmokeDetonationPosition">Where a smoke popped, when networked.</param>
/// <param name="IsIncendiary">A molotov projectile: true for an incendiary, false for a molotov. Null for other classes and when unseen.</param>
public readonly record struct ObservedProjectile(
    int EntityIndex,
    int Serial,
    string ClassName,
    int ThrowerSlot,
    int Team,
    Vector3? Position,
    Vector3? InitialPosition,
    Vector3? InitialVelocity,
    int Bounces,
    int SmokeEffectTickBegin,
    Vector3? SmokeDetonationPosition,
    bool? IsIncendiary)
{
    /// <summary>
    ///     <c>m_nExplodeEffectTickBegin</c> on the server-boot clock: 0 while the grenade flies, the tick its
    ///     explosion began once an HE detonates. Null when unseen.
    /// </summary>
    public int? ExplodeEffectTickBegin { get; init; }
}
