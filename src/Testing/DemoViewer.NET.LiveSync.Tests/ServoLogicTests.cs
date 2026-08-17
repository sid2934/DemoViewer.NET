#region

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="ServoLogic" /> DriftServo threshold battery: locked band does
///     nothing (but restores 1× after bending), the servo band bends speed within [0.75, 1.5],
///     and only past ±128 does the expensive hard resync fire.
/// </summary>
[Category("Unit")]
public class ServoLogicTests
{
    [Test]
    public async Task LockedBand_DoesNothing_UnlessServoWasEngaged()
    {
        await Assert.That(ServoLogic.Decide(0, false).Kind)
            .IsEqualTo(ServoLogic.Correction.None);
        await Assert.That(ServoLogic.Decide(8, false).Kind)
            .IsEqualTo(ServoLogic.Correction.None);
        await Assert.That(ServoLogic.Decide(-8, true).Kind)
            .IsEqualTo(ServoLogic.Correction.RestoreSpeed);
        await Assert.That(ServoLogic.Decide(-8, true).Speed).IsEqualTo(1.0);
    }

    [Test]
    public async Task ServoBand_BendsSpeed_TowardCs2Clock_Clamped()
    {
        // DV behind (positive error) → speed up; ahead → slow down.
        (ServoLogic.Correction kind, double speed) = ServoLogic.Decide(64, false);
        await Assert.That(kind).IsEqualTo(ServoLogic.Correction.AdjustSpeed);
        await Assert.That(speed).IsEqualTo(1.25);

        await Assert.That(ServoLogic.Decide(-64, false).Speed).IsEqualTo(0.75);
        // Clamps: err=128 → 1.5; err=-128 would be 0.5 → clamped 0.75.
        await Assert.That(ServoLogic.Decide(128, false).Speed).IsEqualTo(1.5);
        await Assert.That(ServoLogic.Decide(-128, false).Speed).IsEqualTo(0.75);
    }

    [Test]
    public async Task BeyondServoBand_HardResync()
    {
        await Assert.That(ServoLogic.Decide(129, true).Kind)
            .IsEqualTo(ServoLogic.Correction.HardResync);
        await Assert.That(ServoLogic.Decide(-5000, false).Kind)
            .IsEqualTo(ServoLogic.Correction.HardResync);
    }
}
