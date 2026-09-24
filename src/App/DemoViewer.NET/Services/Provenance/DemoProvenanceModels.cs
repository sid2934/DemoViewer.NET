#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Services.Provenance;

/// <summary>
///     The label vocabulary (plan §3 Demo Provenance Labels, overview correction 24): four values and
///     "unlabeled", which is the absence of one. Strings, not an enum, because the Strat Record Panel keys
///     <c>ByProvenance</c> on the value and a tag document may carry it verbatim.
/// </summary>
public static class DemoProvenanceLabel
{
    /// <summary>A match between two tagged teams.</summary>
    public const string Official = "official";

    /// <summary>A practice match our side played, against nobody the library knows.</summary>
    public const string Scrim = "scrim";

    /// <summary>A practice match our team played against a team the library knows.</summary>
    public const string OurScrim = "our scrim";

    /// <summary>A Valve matchmaking game our side played.</summary>
    public const string Matchmaking = "matchmaking";

    /// <summary>The four values, in the order the Library card's menu lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Official, Scrim, OurScrim, Matchmaking];

    /// <summary>Whether a string is one of the four values (exact, lower case).</summary>
    public static bool IsKnown(string label) => All.Contains(label, StringComparer.Ordinal);
}

/// <summary>Where a demo's label came from.</summary>
public enum ProvenanceOrigin
{
    /// <summary>No override and the heuristic had nothing to say: unlabeled.</summary>
    None,

    /// <summary>The heuristic default.</summary>
    Heuristic,

    /// <summary>The user pinned it on the Library card.</summary>
    Override
}

/// <summary>One demo's resolved provenance: the label in force, the default it would carry without a pin, and which won.</summary>
/// <param name="DemoPath">The demo.</param>
/// <param name="Sha256">Its content hash, or null before tier 2 hashed it.</param>
/// <param name="Label">The label in force, or null for unlabeled.</param>
/// <param name="Default">The heuristic's answer, or null; equal to <paramref name="Label" /> unless overridden.</param>
/// <param name="Source">Which of the two the label came from.</param>
public sealed record DemoProvenance(string DemoPath, string? Sha256, string? Label, string? Default, ProvenanceOrigin Source)
{
    public bool IsOverride => Source == ProvenanceOrigin.Override;
}

/// <summary>What the heuristic reads: two cache fields and two Team Identity facts, nothing else.</summary>
/// <param name="BothClanTags">Both end-of-demo sides carried a <c>m_szClanTeamname</c>.</param>
/// <param name="SourceKind">The engine classifier's verdict on the demo header.</param>
/// <param name="OurSideSource">Which rule resolved our side, <see cref="Teams.OurSideSource.None" /> when none did.</param>
/// <param name="OpponentAssigned">The side that is not ours belongs to a team in <c>teams.json</c>.</param>
public sealed record ProvenanceInputs(bool BothClanTags, DemoSourceKind SourceKind, OurSideSource OurSideSource, bool OpponentAssigned)
{
    public bool OurSideResolved => OurSideSource != OurSideSource.None;
}

/// <summary>
///     The heuristic default (team-identity.md §3.10 with correction 24), one pure function so every
///     branch is a fixture. In order:
///     <list type="number">
///         <item>clan tags on both sides: <c>official</c>, whoever we are;</item>
///         <item>our side is a roster of the us team (or an override naming it) and the other side is a
///         team the library knows: <c>our scrim</c>;</item>
///         <item>our side resolved and the header says Valve matchmaking: <c>matchmaking</c>;</item>
///         <item>our side resolved, tagless, not matchmaking: <c>scrim</c>;</item>
///         <item>else unlabeled.</item>
///     </list>
///     Steps 3 and 4 do not require our side to come from the me accounts: the owner's own roster is a
///     team once it recurs (so its demos resolve through <c>Team</c>, not <c>Me</c>) and in matchmaking
///     its opponents never recur (§3.3), so reading "me-resolved" literally would leave every demo of
///     that roster unlabeled while the solo queues beside them read <c>matchmaking</c>.
/// </summary>
public static class DemoProvenanceHeuristic
{
    /// <summary>The default label for the inputs, or null for unlabeled.</summary>
    /// <param name="inputs">The inputs.</param>
    public static string? Default(ProvenanceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.BothClanTags)
        {
            return DemoProvenanceLabel.Official;
        }

        if (inputs.OurSideSource is OurSideSource.Team or OurSideSource.Override && inputs.OpponentAssigned)
        {
            return DemoProvenanceLabel.OurScrim;
        }

        if (!inputs.OurSideResolved)
        {
            return null;
        }

        return inputs.SourceKind == DemoSourceKind.GotvMatchmaking ? DemoProvenanceLabel.Matchmaking : DemoProvenanceLabel.Scrim;
    }
}
