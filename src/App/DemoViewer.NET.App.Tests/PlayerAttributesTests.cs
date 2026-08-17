#region

using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Gates for the attributes-panel row's derived display properties. Pure — no Avalonia, no
///     demo. The deep field-population path (HP/armour/weapon/cash from a real pawn/controller) is covered
///     by the headless smoke test against a real demo.
/// </summary>
public class PlayerAttributesTests
{
    [Test]
    public async Task TeamClassAndLabel_TrackTeam()
    {
        // IsT / IsCt drive the theme-aware team-chip colour CLASS in the view (Pb2dTeamT / Pb2dTeamCt token);
        // neutral (spectator/unassigned) is neither class → the default neutral token.
        PlayerAttributes a = new(0);

        a.Team = 2; // T
        await Assert.That(a.TeamLabel).IsEqualTo("T");
        await Assert.That(a.IsT).IsTrue();
        await Assert.That(a.IsCt).IsFalse();

        a.Team = 3; // CT
        await Assert.That(a.TeamLabel).IsEqualTo("CT");
        await Assert.That(a.IsCt).IsTrue();
        await Assert.That(a.IsT).IsFalse();

        a.Team = 0; // unassigned / spectator → neither class
        await Assert.That(a.TeamLabel).IsEqualTo("—");
        await Assert.That(a.IsT).IsFalse();
        await Assert.That(a.IsCt).IsFalse();
    }

    [Test]
    public async Task Defaults_AreEmDash_NeverCrashOnMissing()
    {
        // A freshly-created row (no data yet) shows placeholders, never throws.
        PlayerAttributes a = new(3);
        await Assert.That(a.Health).IsEqualTo("—");
        await Assert.That(a.Armor).IsEqualTo("—");
        await Assert.That(a.ActiveWeapon).IsEqualTo("—");
        await Assert.That(a.Cash).IsEqualTo("—");
        await Assert.That(a.HasLivePawn).IsFalse();
    }

    [Test]
    public async Task TeamChanged_RaisesDerivedPropertyNotifications()
    {
        PlayerAttributes a = new(0);
        List<string> raised = new();
        a.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        a.Team = 3;

        await Assert.That(raised).Contains(nameof(PlayerAttributes.IsT));
        await Assert.That(raised).Contains(nameof(PlayerAttributes.IsCt));
        await Assert.That(raised).Contains(nameof(PlayerAttributes.TeamLabel));
    }
}
