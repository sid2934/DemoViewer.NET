#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook.Services.Provenance;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     Which demos clustering may found teams from. Queue groups in Valve matchmaking and FACEIT are too
///     loose for Valve's roster rules (a trio with rotating fills reads as fragments and stand-ins), so
///     those sides only ever match a team the user owns; team play (officials, scrims, pro demos) is
///     clustered as before. The inputs are the header, the clan tags and the user's provenance pin, never
///     the provenance heuristic, which itself reads the clustering result.
/// </summary>
public static class TeamSourcePolicy
{
    /// <summary>
    ///     The source kind a demo is treated as: the kind the cache stored, else the engine classifier over
    ///     the server name, with a FACEIT server name read as FACEIT. The engine returns <c>Unknown</c> for
    ///     FACEIT server names, so the name check is the one that fires in practice.
    /// </summary>
    /// <param name="storedKind">The cache row's <c>DemoSourceKind</c> by name, or null for an old row.</param>
    /// <param name="server">The cached server name.</param>
    public static DemoSourceKind EffectiveKind(string? storedKind, string? server)
    {
        DemoSourceKind kind = Enum.TryParse(storedKind, out DemoSourceKind stored)
            ? stored
            : DemoSourceClassifier.Classify(server ?? "", "", "", 0).SourceKind;
        if (kind is DemoSourceKind.Unknown or DemoSourceKind.Custom
            && server is not null && server.Contains("faceit", StringComparison.OrdinalIgnoreCase))
        {
            return DemoSourceKind.Faceit;
        }

        return kind;
    }

    /// <summary>Queue play: Valve matchmaking and FACEIT.</summary>
    /// <param name="kind">The effective kind.</param>
    public static bool IsMatchmaking(DemoSourceKind kind) => kind is DemoSourceKind.GotvMatchmaking or DemoSourceKind.Faceit;

    /// <summary>
    ///     Whether clustering may found and grow teams from this demo. A provenance pin decides when the
    ///     user set one; else queue play is untracked unless both sides carry a clan tag (a league or a
    ///     tournament run on those servers).
    /// </summary>
    /// <param name="sourceKind">The effective kind by name, or null when unknown.</param>
    /// <param name="bothClanTags">Both end-of-demo sides carried a clan tag.</param>
    /// <param name="provenancePin">The user's provenance label on the demo, or null.</param>
    public static bool AutoTracks(string? sourceKind, bool bothClanTags, string? provenancePin)
    {
        if (provenancePin is DemoProvenanceLabel.Official or DemoProvenanceLabel.Scrim or DemoProvenanceLabel.OurScrim)
        {
            return true;
        }

        if (provenancePin is DemoProvenanceLabel.Matchmaking)
        {
            return false;
        }

        if (bothClanTags)
        {
            return true;
        }

        return !(Enum.TryParse(sourceKind, out DemoSourceKind kind) && IsMatchmaking(kind));
    }
}
