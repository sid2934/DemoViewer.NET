#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.Services.RoundFacts;

/// <summary>
///     The engine-backed row source, parked. The <c>round_facts</c> ruleset needs six engine surfaces
///     (a team subject on the forward path, game-rules providers, a real round end on the GOTV
///     profiles, frame-clock ticks, team money, plant site) that are filed upstream as CS2DemoKit #54
///     and have not shipped in the pinned release, so this source evaluates nothing: it reports every
///     column unavailable and writes no rows, which leaves every consumer exactly where it was. When
///     the pin bumps, this class evaluates the ruleset through <c>DemoAnalysis</c> on the forward path
///     and reads the <c>round_facts</c> table into a <see cref="RoundFactsTable" />; nothing above the
///     seam changes.
/// </summary>
public sealed class EngineRoundFactsRowSource : IRoundFactsRowSource
{
    /// <summary>The one line the log carries while the engine work is outstanding.</summary>
    public const string WaitingDiagnostic =
        "round_facts: no rows; the engine surfaces the ruleset reads are waiting on CS2DemoKit #54";

    /// <inheritdoc />
    public RoundFactsTable Rows(ParsedDemo parsed) => RoundFactsTable.Unavailable(WaitingDiagnostic);
}
