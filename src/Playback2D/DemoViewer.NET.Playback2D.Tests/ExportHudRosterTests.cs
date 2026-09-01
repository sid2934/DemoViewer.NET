#region

using System.Security.Cryptography;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The export HUD: the roster strip, the recomposed clock, and the team-coloured kill feed. Every
///     case here asserts <b>pixels</b>, since what matters is what a 720p export looks like, and a
///     layer that lays out correctly and paints in one colour would pass a geometry-only suite.
/// </summary>
public class ExportHudRosterTests
{
    // Big enough that a card is a card: the layer refuses panes it would swamp, which is deliberate and
    // is its own case below.
    private static readonly SKSizeI _size = new(800, 420);

    [Test]
    public async Task AnEmptyRoster_DrawsNothing()
    {
        StubHudDataSource data = new(ExportFixtures.Hud(0));

        // Same contract the kill feed has: a panel with nothing in it is chrome the video did not ask for.
        await Assert.That(Ink(Roster(data), default)).IsEqualTo(0);
    }

    [Test]
    public async Task ARosterOfSpectators_DrawsNothing()
    {
        // Team 1 is the spectator seat. Coaches and GOTV observers are roster rows with no side to line up
        // against an edge, and a column of them would be a scoreboard for people who are not playing.
        IReadOnlyList<HudPlayerRow> watchers =
        [
            new(0, 1, "OB", true, 100, 0, false, false, "—", 0, 0, 0, 0),
            new(1, 0, "CO", true, 100, 0, false, false, "—", 0, 0, 0, 0)
        ];

        await Assert.That(Ink(Roster(new StubHudDataSource(ExportFixtures.Hud(0, roster: watchers))), default))
            .IsEqualTo(0);
    }

    [Test]
    public async Task TheRoster_DrawsOnlyInTheTopBand()
    {
        StubHudDataSource data = new(ExportFixtures.Hud(0, roster: ExportFixtures.Roster()));

        // The compositor renders every layer once per band. Five cards repeated on each floor of a
        // two-level Nuke export would be ten players who are not in the match.
        await Assert.That(Ink(Roster(data), new SKRect(0, 0, 800, 210))).IsGreaterThan(0);
        await Assert.That(Ink(Roster(data), new SKRect(0, 210, 800, 420))).IsEqualTo(0);
        await Assert.That(Ink(Roster(data), default)).IsGreaterThan(0)
            .Because("a single-pane snapshot has a zero rectangle and IS the top band");
    }

    [Test]
    public async Task TheTwoSides_AreColouredAndSeatedOnOppositeEdges()
    {
        StubHudDataSource data = new(ExportFixtures.Hud(0, roster: ExportFixtures.Roster()));
        using SKBitmap bitmap = Render(Roster(data), default);

        // The side stripe is a solid fill, so the team token appears in the frame EXACTLY: no tolerance,
        // no hashing a picture and hoping the difference was the colour.
        (int leftT, int rightT) = Halves(bitmap, ScenePalette.Dark.TeamT);
        (int leftCt, int rightCt) = Halves(bitmap, ScenePalette.Dark.TeamCt);

        Console.WriteLine($"[roster] T px left={leftT} right={rightT} · CT px left={leftCt} right={rightCt}");

        await Assert.That(leftT).IsGreaterThan(0);
        await Assert.That(rightT).IsEqualTo(0).Because("T owns one edge of the frame, not both");
        await Assert.That(rightCt).IsGreaterThan(0);
        await Assert.That(leftCt).IsEqualTo(0);
    }

    [Test]
    public async Task ADeadPlayer_ReadsDifferentlyFromALivingOne()
    {
        IReadOnlyList<HudPlayerRow> living =
        [
            new(0, 2, "NE", true, 100, 100, true, false, "AK-47", 4150, 12, 7, 3)
        ];
        IReadOnlyList<HudPlayerRow> fallen =
        [
            living[0] with
            {
                IsAlive = false,
                Health = 0
            }
        ];

        // A card that vanished on death would take the round's most important fact off the screen, so the
        // difference has to be a *treatment*: a faded stripe, an empty bar, a rule through the tag.
        await Assert.That(Hash(living)).IsNotEqualTo(Hash(fallen));
        await Assert.That(Ink(Roster(new StubHudDataSource(ExportFixtures.Hud(0, roster: fallen))), default))
            .IsGreaterThan(0);
    }

    [Test]
    public async Task OnAPaneItWouldSwallow_TheRosterWithdraws()
    {
        StubHudDataSource data = new(ExportFixtures.Hud(0, roster: ExportFixtures.Roster()));

        // The export suite renders 64×48 fixtures, and a card is 96 px at its narrowest. Two columns of
        // them would BE the video. Yielding the frame to the map is the right answer, not scaling text
        // down until it is a smear.
        using CpuSurfaceProvider surfaces = new();
        using SKSurface surface = surfaces.CreateSurface(new SKSizeI(64, 48));
        surface.Canvas.Clear(ScenePalette.Dark.Background);

        using RosterLayer layer = Roster(data);
        SceneTime time = new(1000, 0, 0, 1 / 60.0, true);
        layer.Advance(in time, Scene2DFrame.Empty);
        layer.Render(surface.Canvas, Context(new SKSizeI(64, 48), default));

        await Assert.That(Painted(surface)).IsEqualTo(0);
    }

    [Test]
    public async Task TheRosterReader_IsBorrowedThrough_NotCopied()
    {
        List<HudPlayerRow> roster = [.. ExportFixtures.Roster(2)];
        TimelineHudDataSource source = new([], 64, static _ => ClockReading.Unknown,
            rosterAt: _ => roster);

        // The same lifetime KillRows has: the snapshot hands back the frame source's own pooled list, so
        // an export pays nothing per frame to publish ten cards.
        await Assert.That(source.At(1000).Roster).IsSameReferenceAs(roster);
    }

    [Test]
    public async Task WithNoRosterReader_TheSnapshotHasNoCards()
    {
        TimelineHudDataSource source = new([], 64, static _ => ClockReading.Unknown);

        // The CLI and a fixture render both land here, and "no cards" must be an empty list rather than a
        // null the layer has to guard.
        await Assert.That(source.At(1000).Roster).IsNotNull();
        await Assert.That(source.At(1000).Roster.Count).IsEqualTo(0);
    }

    // ── the recomposed clock ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheScoreBoxes_CarryTheTeamTokens()
    {
        StubHudDataSource data = new(ExportFixtures.Hud(0));
        using ClockLayer layer = new(data, Bigger());
        using SKBitmap bitmap = Render(layer, default);

        // "T 7 : 5 CT" in one grey was arithmetic the viewer had to do. Each score now sits on its own
        // side's token, the SAME token the markers use, so there is one colour vocabulary in the frame.
        await Assert.That(Count(bitmap, ScenePalette.Dark.TeamT)).IsGreaterThan(0);
        await Assert.That(Count(bitmap, ScenePalette.Dark.TeamCt)).IsGreaterThan(0);
    }

    [Test]
    public async Task ADefuseInProgress_IsDrawn_AndItsOutcomeIsTheColour()
    {
        HudSnapshot idle = ExportFixtures.Hud(0, true);
        HudSnapshot winning = ExportFixtures.Hud(0, true, defusing: true);
        HudSnapshot losing = winning with
        {
            CountdownSeconds = 1.0
        };

        // DefuseInProgress and DefuseSeconds were in the snapshot and had never been drawn, the two
        // fields that decide the round. Drawn, and drawn in the colour of whoever wins the race, so the
        // subtraction happens on screen rather than in the viewer's head.
        await Assert.That(Hash(idle)).IsNotEqualTo(Hash(winning));
        await Assert.That(Hash(winning)).IsNotEqualTo(Hash(losing));

        using SKBitmap bitmap = Render(new ClockLayer(new StubHudDataSource(winning), Bigger()), default);
        await Assert.That(Count(bitmap, ScenePalette.Dark.BombDefuse)).IsGreaterThan(0)
            .Because("3.4 s of defuse against 34.5 s of fuse is a defuse that lands");
    }

    // ── the team-coloured kill feed ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task AKillFeedRow_ColoursAttackerAndVictimByTheirSides()
    {
        using SKBitmap bitmap = Feed(Kill(2, 3));

        await Assert.That(Count(bitmap, ScenePalette.Dark.TeamT)).IsGreaterThan(0);
        await Assert.That(Count(bitmap, ScenePalette.Dark.TeamCt)).IsGreaterThan(0);
    }

    [Test]
    public async Task SwappingTheSides_SwapsTheColours()
    {
        // Same names, same weapon, same modifiers: only the sides differ. Anything that passed by
        // accident (a hash of the text, a count of the ink) fails here.
        await Assert.That(Hash(Kill(2, 3)))
            .IsNotEqualTo(Hash(Kill(3, 2)));
    }

    [Test]
    public async Task AnUnknownSide_KeepsTheNeutralColour_AndKeepsItsRow()
    {
        using SKBitmap unknown = Feed(Kill(0, 0));

        // GOTV emits player_team only for the halftime swap, so a demo that cannot say who shot is a real
        // case and not a defensive one. It must cost the row its COLOUR, never its row.
        await Assert.That(Count(unknown, ScenePalette.Dark.TeamT)).IsEqualTo(0);
        await Assert.That(Count(unknown, ScenePalette.Dark.TeamCt)).IsEqualTo(0);
        await Assert.That(Count(unknown, new SKColor(0xFFF2F2F2))).IsGreaterThan(0)
            .Because("neutral is the colour the whole feed used to be");
    }

    [Test]
    public async Task TheRowsText_IsUnchangedByTheSplitIntoColouredRuns()
    {
        KillFeedRow row = new(1000, "neo", "trinity", "smith", "awp",
            true, true, true, true,
            true, true, true,
            2, 3);

        // Colour is a rendering concern. Splitting the line into three runs must not change one character
        // of what a row SAYS. Playback2DKillFeedTests compares this text against the XAML feed's.
        string text = KillFeedLayer.Format(row);
        Console.WriteLine($"[killfeed] {text}");

        await Assert.That(text).StartsWith("neo");
        await Assert.That(text).EndsWith("smith");
        await Assert.That(text).Contains("+trinity");
        await Assert.That(text).Contains("awp");
        await Assert.That(text).Contains("HS");
        await Assert.That(text).Contains("→");

        // Every character in a row must have a glyph in the ONE embedded face. Inter Regular has no
        // U+26A1 and no U+2731, and both were being drawn: a .notdef box in the corner of every exported
        // frame that carried a flash assist or a blind kill.
        await Assert.That(text).DoesNotContain("⚡");
        await Assert.That(text).DoesNotContain("✱");
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────

    private static RosterLayer Roster(IHudDataSource data) => new(data);

    // The shipped size renders 14 px glyphs whose only fully-covered pixels are stems; doubling it gives
    // solid glyph cores, so a colour assertion can be exact instead of needing an arbitrary tolerance.
    private static HudStyle Bigger() => new HudStyle() with
    {
        FontSizePx = 28f
    };

    private static KillFeedRow Kill(int attacker, int victim) =>
        new(1000, "neo", null, "smith", "awp", false, false, false, false, false, false, false,
            attacker, victim);

    private static SKBitmap Feed(KillFeedRow row) =>
        Render(new KillFeedLayer(new StubHudDataSource(
            ExportFixtures.Hud(0) with
            {
                KillRows = new[]
                {
                    row
                }
            }), Bigger()), default);

    private static string Hash(KillFeedRow row)
    {
        using SKBitmap bitmap = Feed(row);
        return Digest(bitmap);
    }

    private static string Hash(HudSnapshot snapshot)
    {
        using SKBitmap bitmap = Render(new ClockLayer(new StubHudDataSource(snapshot), Bigger()), default);
        return Digest(bitmap);
    }

    private static string Hash(IReadOnlyList<HudPlayerRow> roster)
    {
        using SKBitmap bitmap = Render(Roster(new StubHudDataSource(
            ExportFixtures.Hud(0, roster: roster))), default);
        return Digest(bitmap);
    }

    private static string Digest(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToHexString(SHA256.HashData(data.ToArray()));
    }

    private static int Ink(ISceneLayer layer, SKRect paneRect)
    {
        using (layer)
        {
            using SKBitmap bitmap = Draw(layer, _size, paneRect);
            SKColor background = ScenePalette.Dark.Background;
            int painted = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y) != background)
                    {
                        painted++;
                    }
                }
            }

            return painted;
        }
    }

    private static SKBitmap Render(ISceneLayer layer, SKRect paneRect)
    {
        using (layer)
        {
            return Draw(layer, _size, paneRect);
        }
    }

    private static SKBitmap Draw(ISceneLayer layer, SKSizeI size, SKRect paneRect)
    {
        using CpuSurfaceProvider surfaces = new();
        using SKSurface surface = surfaces.CreateSurface(size);
        surface.Canvas.Clear(ScenePalette.Dark.Background);

        SceneTime time = new(1000, 0, 0, 1 / 60.0, true);
        layer.Advance(in time, Scene2DFrame.Empty);
        layer.Render(surface.Canvas, Context(size, paneRect));

        using SKImage image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static int Painted(SKSurface surface)
    {
        using SKImage image = surface.Snapshot();
        using SKBitmap bitmap = SKBitmap.FromImage(image);

        SKColor background = ScenePalette.Dark.Background;
        int painted = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != background)
                {
                    painted++;
                }
            }
        }

        return painted;
    }

    private static int Count(SKBitmap bitmap, SKColor color)
    {
        int found = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == color)
                {
                    found++;
                }
            }
        }

        return found;
    }

    private static (int Left, int Right) Halves(SKBitmap bitmap, SKColor color)
    {
        int left = 0, right = 0;
        int middle = bitmap.Width / 2;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != color)
                {
                    continue;
                }

                if (x < middle)
                {
                    left++;
                }
                else
                {
                    right++;
                }
            }
        }

        return (left, right);
    }

    private static SceneRenderContext Context(SKSizeI size, SKRect paneRect)
    {
        // PaneBounds is pane-LOCAL and always origin-zero; the pane's ViewportRect is what says which band
        // this is. A default rectangle is the un-banded single-pane render.
        float width = paneRect.Width > 0 ? paneRect.Width : size.Width;
        float height = paneRect.Height > 0 ? paneRect.Height : size.Height;

        return new SceneRenderContext(Scene2DFrame.Empty, default,
            ViewportTransform.Fit(size.Width, size.Height, -100, -100, 100, 100),
            new SKRect(0, 0, width, height), 0, 0, 0,
            RenderPurpose.Export, ScenePalette.Dark, 1f)
        {
            Pane = new LevelPaneSnapshot(default, 0,
                new MapLevel
                {
                    Id = default,
                    Name = "l",
                    ZMin = 0,
                    ZMax = 100
                },
                ViewportTransform.Fit(size.Width, size.Height, -100, -100, 100, 100), paneRect, 0)
        };
    }
}
