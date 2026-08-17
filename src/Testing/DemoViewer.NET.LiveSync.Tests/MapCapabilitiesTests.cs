#region

using Cs2VideoGenerator.Core;
using DemoViewer.NET.Services.LiveSync;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="LiveSyncService.MapCapabilities" /> — the single seam projecting wire
///     capability tokens onto the engine feature matrix. A swapped or dropped line here
///     silently downgrades every v1.1 session to v1.0 behavior while the whole suite stays green
///     (the mock advertises the full set, and the E2E passes on the v1.0 fallback too) — so the
///     mapping is pinned token-by-token: each token flips exactly its own flag.
/// </summary>
[Category("Unit")]
public class MapCapabilitiesTests
{
    private static readonly (string Token, Func<LiveSyncCapabilities, bool> Flag, string Name)[] _matrix =
    [
        (CsvgCapabilities.DemoStateEvents, c => c.DemoStateEvents, nameof(LiveSyncCapabilities.DemoStateEvents)),
        (CsvgCapabilities.CommandAck, c => c.CommandAck, nameof(LiveSyncCapabilities.CommandAck)),
        (CsvgCapabilities.SeekAck, c => c.SeekAck, nameof(LiveSyncCapabilities.SeekAck)),
        (CsvgCapabilities.TimescaleSet, c => c.TimescaleSet, nameof(LiveSyncCapabilities.TimescaleSet)),
        (CsvgCapabilities.DemoIdentity, c => c.DemoIdentity, nameof(LiveSyncCapabilities.DemoIdentity)),
        (CsvgCapabilities.EnginePauseDetection, c => c.EnginePauseDetection,
            nameof(LiveSyncCapabilities.EnginePauseDetection)),
        (CsvgCapabilities.LoadFailureDetection, c => c.LoadFailureDetection,
            nameof(LiveSyncCapabilities.LoadFailureDetection)),
        (CsvgCapabilities.SpectateBySteamId, c => c.SpectateBySteamId, nameof(LiveSyncCapabilities.SpectateBySteamId)),
        (CsvgCapabilities.UserDemoUi, c => c.UserDemoUi, nameof(LiveSyncCapabilities.UserDemoUi))
    ];

    [Test]
    public async Task EachToken_FlipsExactlyItsOwnFlag()
    {
        foreach ((string token, _, string flagName) in _matrix)
        {
            LiveSyncCapabilities mapped = LiveSyncService.MapCapabilities(new HashSet<string>
            {
                token
            });
            foreach ((string otherToken, Func<LiveSyncCapabilities, bool> flag, string otherName) in _matrix)
            {
                await Assert.That(flag(mapped)).IsEqualTo(otherToken == token)
                    .Because($"token '{token}' must set {flagName} and ONLY it (checked {otherName})");
            }
        }
    }

    [Test]
    public async Task EmptyTokenSet_IsTheV10Baseline()
    {
        LiveSyncCapabilities mapped = LiveSyncService.MapCapabilities(new HashSet<string>());
        await Assert.That(mapped).IsEqualTo(LiveSyncCapabilities.None);
        await Assert.That(mapped.IsV10Baseline).IsTrue();
    }

    [Test]
    public async Task UnknownTokens_AreIgnored()
    {
        LiveSyncCapabilities mapped = LiveSyncService.MapCapabilities(
            new HashSet<string>
            {
                "some-future-capability",
                CsvgCapabilities.SeekAck
            });
        await Assert.That(mapped.SeekAck).IsTrue();
        await Assert.That(mapped with
        {
            SeekAck = false
        }).IsEqualTo(LiveSyncCapabilities.None);
    }
}
