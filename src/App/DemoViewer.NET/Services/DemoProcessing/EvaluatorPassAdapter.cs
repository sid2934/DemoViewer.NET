#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     An <see cref="IDemoEvaluator" /> as a pass: <see cref="IDemoEvaluator.Wants" /> answers yes,
///     <see cref="IDemoEvaluator.WantsAfterUpstream" /> answers "once upstream has run", the needs come
///     from <see cref="IDemoEvaluator.ForwardFor" /> and <see cref="IDemoEvaluator.ReadsUserCommands" />,
///     and the run dispatches to <see cref="IDemoEvaluator.Evaluate" /> or
///     <see cref="IDemoEvaluator.EvaluateForward" /> by the read the visit made.
/// </summary>
public sealed class EvaluatorPassAdapter : IDemoPass, IPassScheduling
{
    /// <param name="evaluator">The evaluator.</param>
    /// <param name="after">The pass ids it runs after, as registered.</param>
    public EvaluatorPassAdapter(IDemoEvaluator evaluator, IReadOnlyList<string> after)
    {
        Evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        After = after ?? throw new ArgumentNullException(nameof(after));
    }

    /// <summary>The wrapped evaluator.</summary>
    public IDemoEvaluator Evaluator { get; }

    /// <inheritdoc />
    public string Id => Evaluator.Id;

    /// <inheritdoc />
    public IReadOnlyList<string> After { get; }

    /// <inheritdoc />
    public PassNeeds Needs(VisitedDemo demo) =>
        Evaluator.ForwardFor(demo.Path) is { } forward
            ? new PassNeeds(ParseMode.Forward, forward, Evaluator.ReadsUserCommands)
            : new PassNeeds(ParseMode.Retained, ForwardNeeds.None, Evaluator.ReadsUserCommands);

    /// <inheritdoc />
    public PassInterest Interest(VisitedDemo demo, PassLevel level)
    {
        if (Evaluator.Wants(demo.Path))
        {
            return PassInterest.Yes;
        }

        return Evaluator.WantsAfterUpstream(demo.Path) ? PassInterest.IfUpstreamRuns : PassInterest.No;
    }

    /// <inheritdoc />
    public void Run(PassInput input)
    {
        if (input.Forward is { } forward)
        {
            Evaluator.EvaluateForward(input.Demo.Path, forward);
        }
        else
        {
            Evaluator.Evaluate(input.Demo.Path, input.Retained!);
        }
    }

    /// <inheritdoc />
    public void OnFailed(VisitedDemo demo, Exception failure) => Evaluator.OnFailed(demo.Path);

    /// <summary>The level the evaluator asks for a demo at, from <see cref="IDemoEvaluator.PriorityFor" />.</summary>
    public PassLevel LevelFor(VisitedDemo demo) =>
        Evaluator.PriorityFor(demo.Path) >= DemoJobPriority.UserRequested ? PassLevel.UserRequested : PassLevel.Background;

    /// <summary>The evaluator's <see cref="IDemoEvaluator.OrderHint" />.</summary>
    public long OrderHint(VisitedDemo demo) => Evaluator.OrderHint(demo.Path);

    /// <summary>
    ///     A factory handing out one adapter per evaluator instance: the registry calls the factory on every
    ///     resolve, and a visit tells the same pass apart from another by reference.
    /// </summary>
    /// <param name="factory">Builds or returns the evaluator.</param>
    /// <param name="after">The pass ids it runs after.</param>
    public static Func<IDemoPass> Cached(Func<IDemoEvaluator> factory, IReadOnlyList<string> after)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(after);
        object gate = new();
        EvaluatorPassAdapter? cached = null;
        return () =>
        {
            IDemoEvaluator inner = factory();
            lock (gate)
            {
                if (cached is null || !ReferenceEquals(cached.Evaluator, inner))
                {
                    cached = new EvaluatorPassAdapter(inner, after);
                }

                return cached;
            }
        };
    }
}
