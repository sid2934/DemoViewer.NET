#region

using Cs2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Deterministic pure-controller gate for the <c>_applying</c> re-entrancy guard
///     — the exact mechanism the Pause snap relies on. The guard's job: when the fan-out body (or the
///     Pause snap's light fan-out) assigns SelectedFrame, the setter echoes back into
///     <see cref="PlaybackController.SeekToFrame" />; that re-entry must be ABSORBED so a single move
///     never double-fires the heavy discrete fan-out (<see cref="PlaybackController.ApplySeek" />).
///     <para>
///         No demo / async / dispatcher: counter delegates simulate the echo by re-entering
///         <c>SeekToFrame</c> from inside <c>ApplySeek</c>. The guard makes the inner call a no-op, so
///         the heavy fan-out runs exactly once. This is the bug-in-its-purest-form that the Pause snap
///         guard (same mechanism) prevents.
///     </para>
/// </summary>
[NotInParallel]
public class PlaybackControllerPauseGuardTests
{
    [Test]
    public async Task SeekToFrame_SelectedFrameEcho_DoesNotDoubleFireHeavyFanOut()
    {
        PlaybackController controller = new();
        controller.LoadDemo(MakeFrames(100), 64);

        int applySeekCount = 0;
        PlaybackController c = controller;

        // ApplySeek IS the heavy discrete fan-out. In the real shell it sets SelectedFrame, whose
        // setter echoes back into SeekToFrame. Simulate that echo here: re-enter SeekToFrame from
        // inside ApplySeek. The guard must absorb the re-entry (no second ApplySeek).
        controller.ApplySeek = idx =>
        {
            applySeekCount++;
            c.SeekToFrame(idx); // the SelectedFrame= echo
        };

        controller.SeekToFrame(10);

        // Exactly ONE heavy fan-out despite the re-entrant echo — the guard worked.
        await Assert.That(applySeekCount).IsEqualTo(1);
        await Assert.That(controller.CurrentFrameIndex).IsEqualTo(10);
    }

    private static List<DemoFrame> MakeFrames(int count)
    {
        List<DemoFrame> frames = new(count);
        for (int i = 0; i < count; i++)
        {
            frames.Add(new DemoFrame
            {
                Command = "DEM_Packet",
                FrameNumber = i,
                ServerTick = i,
                HeaderLength = 0,
                RawLength = 0,
                RawStart = 0,
                IsCompressed = false
            });
        }

        return frames;
    }
}
