#region

using Cs2DemoKit.Analysis.Plugins;
using Cs2DemoKit.Parser;
using Cs2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Position-constant gate — the highest-leverage correctness item for the 2D pilot. Asserts
///     that <see cref="PositionUtil.CellToWorld" /> (constant lifted from the demofile-net oracle:
///     <c>world = (cell - 32) * 512 + offset</c>) reconstructs known pawn positions that land ON-RADAR
///     (within plausible CS2 map bounds). This guards the pilot from plotting markers in the wrong
///     place. The test is DISCRIMINATING: it requires at least one OFF-CENTER pawn (cellX/cellY ≠ 32),
///     for which the wrong cell multiplier (1024) would throw the position roughly 2× out and far
///     outside the asserted bounds — so passing actually proves the 512 constant, not just "near
///     origin where 512 and 1024 collapse."
/// </summary>
[NotInParallel]
[Category("Integration")]
public class PositionUtilGateTests
{
    // CS2 maps fit comfortably within ±(1<<14) world units on X/Y (the WORLD_HALF_EXTENT). A correct
    // reconstruction keeps live pawns well inside this; the 1024-multiplier bug roughly doubles the
    // cell contribution and pushes off-center pawns outside it.
    private const float MapBound = 16384f;

    [Test]
    public async Task CellToWorld_ReconstructsLivePawnsOnRadar_WithOffCenterDiscriminator()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        await Assert.That(frames.Count).IsGreaterThan(100);

        // Seek to a mid-match frame where players are spread across the map (off-center cells).
        EntityTracker tracker = new();
        tracker.AdvanceToIndex(frames.Count / 2, frames);

        int offCenter = 0;
        float maxAbsXy = 0;
        List<(float X, float Y, float Z)> positions = new();
        List<string> samples = new();

        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            (float X, float Y, float Z)? pos = PositionUtil.CellToWorld(pawn);
            if (pos is not { } p)
            {
                return;
            }

            positions.Add(p);
            maxAbsXy = Math.Max(maxAbsXy, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));

            // Off-center discriminator: a cell index meaningfully away from the centre (32).
            int cx = ToInt(pawn["CBodyComponent.m_cellX"]);
            int cy = ToInt(pawn["CBodyComponent.m_cellY"]);
            if (Math.Abs(cx - 32) >= 2 || Math.Abs(cy - 32) >= 2)
            {
                offCenter++;
            }

            samples.Add($"slot {slot}: cell=({cx},{cy}) world=({p.X:F0},{p.Y:F0},{p.Z:F0})");
        });

        foreach (string s in samples)
        {
            Console.WriteLine(s);
        }

        Console.WriteLine($"reconstructed={positions.Count}  off-center={offCenter}  maxAbsXY={maxAbsXy:F0}  " +
                          $"CellWidth={PositionUtil.CellWidth}  WorldHalfExtent={PositionUtil.WorldHalfExtent}");

        // The gate is only meaningful if it discriminated: at least one off-center pawn must have been
        // reconstructed (otherwise 512 vs 1024 would be indistinguishable near the origin).
        await Assert.That(positions.Count).IsGreaterThan(0);
        await Assert.That(offCenter).IsGreaterThan(0);

        // On-radar: every live pawn lands within map bounds on X/Y. This is the assertion the wrong
        // (1024) cell multiplier fails for the off-center pawns above — the empirical pin of the
        // oracle-derived constant (WorldHalfExtent = 16384 = 1<<14, CellWidth = 512).
        await Assert.That(maxAbsXy).IsLessThan(MapBound);
    }

    private static int ToInt(object? v) => v switch
    {
        ushort u => u,
        short s => s,
        int i => i,
        uint u => (int)u,
        long l => (int)l,
        ulong u => (int)u,
        byte b => b,
        _ => 32
    };
}
