namespace DemoViewer.NET.Services.Generated;

/// <summary>
///     What the user has done with one machine-generated item: a proposal, a detected pattern, a suggestion, a
///     generated clip. The shared rule is in <c>docs/strat-book/generated-content.md</c>.
/// </summary>
public enum GeneratedState
{
    /// <summary>Nothing yet. The only state the inbox badge counts.</summary>
    New,

    /// <summary>Looked at and kept, nothing written anywhere else.</summary>
    Reviewed,

    /// <summary>Not wanted. Never offered again until restored, across re-derivations.</summary>
    Dismissed,

    /// <summary>Turned into user truth: a tag, a strat, a team change.</summary>
    Accepted
}

/// <summary>Which settled states an inbox shows besides <see cref="GeneratedState.New" />.</summary>
/// <param name="Reviewed">Show reviewed items.</param>
/// <param name="Dismissed">Show dismissed items.</param>
/// <param name="Accepted">Show accepted items.</param>
public readonly record struct GeneratedFilter(bool Reviewed, bool Dismissed, bool Accepted)
{
    /// <summary>New items only.</summary>
    public static GeneratedFilter NewOnly => default;

    /// <summary>Every state.</summary>
    public static GeneratedFilter All => new(true, true, true);

    public bool Shows(GeneratedState state) => state switch
    {
        GeneratedState.New => true,
        GeneratedState.Reviewed => Reviewed,
        GeneratedState.Dismissed => Dismissed,
        GeneratedState.Accepted => Accepted,
        _ => false
    };
}

/// <summary>How many items an inbox holds in each state.</summary>
public readonly record struct GeneratedCounts(int New, int Reviewed, int Dismissed, int Accepted)
{
    /// <summary>Everything that is not new, what a "show settled" toggle would bring back.</summary>
    public int Settled => Reviewed + Dismissed + Accepted;

    public int Total => New + Settled;

    public static GeneratedCounts Of(IEnumerable<GeneratedState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        int n = 0, r = 0, d = 0, a = 0;
        foreach (GeneratedState state in states)
        {
            switch (state)
            {
                case GeneratedState.New: n++; break;
                case GeneratedState.Reviewed: r++; break;
                case GeneratedState.Dismissed: d++; break;
                case GeneratedState.Accepted: a++; break;
            }
        }

        return new GeneratedCounts(n, r, d, a);
    }
}
