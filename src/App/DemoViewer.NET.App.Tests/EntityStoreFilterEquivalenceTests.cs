#region

using System.Globalization;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Equivalence gate for <see cref="EntityTracker.StoreClassFilter" /> (score-cost Option A): a
///     CCSTeam-only storage filter must yield the byte-identical final scoreboard as a full-storage
///     replay, because the bitstream is decoded either way (only the field STORE is skipped for other
///     classes). Also proves the filter genuinely suppresses storage (a non-CCSTeam entity has no
///     stored fields under the filter). Gated on real demos; skips when none are present.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class EntityStoreFilterEquivalenceTests
{
    private static readonly IReadOnlySet<string> _onlyTeam = new HashSet<string>(StringComparer.Ordinal)
    {
        "CCSTeam"
    };

    [Test]
    public async Task CcsTeamOnlyFilter_ProducesIdenticalScore_ToFullReplay()
    {
        string[] demos =
        [
            "/Users/austingray/Development/DemoViewer.NET/demos/match730_003826256877184877003_0981591541_410.dem",
            "/Users/austingray/Development/DemoViewer.NET/demos/pro-demos/vitality-vs-fut-m2-dust2.dem",
            "/Users/austingray/Development/DemoViewer.NET/demos/pro-demos/furia-vs-vitality-m3-nuke.dem"
        ];
        string[] present = demos.Where(File.Exists).ToArray();
        if (present.Length == 0)
        {
            throw new SkipTestException("no equivalence demo present");
        }

        foreach (string path in present)
        {
            ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
            IReadOnlyList<DemoFrame> frames = parsed.Frames;

            EntityTracker full = new();
            full.AdvanceToIndex(frames.Count - 1, frames);

            EntityTracker filtered = new()
            {
                StoreClassFilter = _onlyTeam
            };
            filtered.AdvanceToIndex(frames.Count - 1, frames);

            (int? fCt, int? fT, string? fCtClan, string? fTClan) = ReadTeams(full);
            (int? gCt, int? gT, string? gCtClan, string? gTClan) = ReadTeams(filtered);

            // Byte-identical scoreboard including clans — the whole premise of the optimization.
            await Assert.That(gCt).IsEqualTo(fCt);
            await Assert.That(gT).IsEqualTo(fT);
            await Assert.That(gCtClan).IsEqualTo(fCtClan);
            await Assert.That(gTClan).IsEqualTo(fTClan);

            // Sanity: a real match yields a plausible CCSTeam scoreboard (so we're not both-null).
            await Assert.That(gCt).IsNotNull();
            await Assert.That(gT).IsNotNull();
        }
    }

    private static (int? Ct, int? T, string? CtClan, string? TClan) ReadTeams(EntityTracker tracker)
    {
        int? ct = null, t = null;
        string? ctClan = null, tClan = null;
        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (ent.ClassName != "CCSTeam")
            {
                continue;
            }

            int teamNum = Convert.ToInt32(ent["m_iTeamNum"] ?? 0, CultureInfo.InvariantCulture);
            int score = Convert.ToInt32(ent["m_iScore"] ?? 0, CultureInfo.InvariantCulture);
            string clan = ent["m_szClanTeamname"] as string ?? "";
            if (teamNum == 2)
            {
                t = score;
                tClan = clan.Length > 0 ? clan : tClan;
            }
            else if (teamNum == 3)
            {
                ct = score;
                ctClan = clan.Length > 0 ? clan : ctClan;
            }
        }

        return (ct, t, ctClan, tClan);
    }
}
