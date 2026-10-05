namespace DemoViewer.NET.Extensions.StratBook.Services.Generated;

/// <summary>
///     What the user has done with one machine-generated item: a proposal, a detected pattern, a suggestion. There is no reviewed state: that belongs to
///     Review clips, which the user sends.
/// </summary>
public enum GeneratedState
{
    /// <summary>Nothing yet. The only state an inbox shows by default and its badge counts.</summary>
    New,

    /// <summary>Not wanted. Never offered again, across re-derivations, until restored.</summary>
    Dismissed,

    /// <summary>Turned into user truth: a tag, a strat, a team change.</summary>
    Accepted
}

/// <summary>The one visibility rule every inbox of generated items follows.</summary>
public static class GeneratedInbox
{
    /// <summary>The toggle's text, with the count of settled items it would bring back.</summary>
    public static string SettledLabel(int settled) => $"Show settled ({settled})";

    /// <summary>New items always; dismissed and accepted ones only with "Show settled" on.</summary>
    public static bool Shows(GeneratedState state, bool showSettled) => state == GeneratedState.New || showSettled;
}

/// <summary>How many items an inbox holds in each state.</summary>
public readonly record struct GeneratedCounts(int New, int Dismissed, int Accepted)
{
    /// <summary>What "Show settled" brings back.</summary>
    public int Settled => Dismissed + Accepted;

    public int Total => New + Settled;

    public static GeneratedCounts Of(IEnumerable<GeneratedState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        int n = 0, d = 0, a = 0;
        foreach (GeneratedState state in states)
        {
            switch (state)
            {
                case GeneratedState.New: n++; break;
                case GeneratedState.Dismissed: d++; break;
                case GeneratedState.Accepted: a++; break;
            }
        }

        return new GeneratedCounts(n, d, a);
    }
}
