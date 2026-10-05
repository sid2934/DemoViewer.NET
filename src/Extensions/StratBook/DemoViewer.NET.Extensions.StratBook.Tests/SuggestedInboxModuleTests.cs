#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Suggested section's badge: recomputed from the demo index on every <c>cache.Changed</c> while
///     the section's gate is on, left alone while it is off, re-read live so a mid-session toggle needs no
///     restart.
/// </summary>
public class SuggestedInboxModuleTests
{
    private static DemoCacheStore CacheWithPending(string path, int pending)
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord(path));
        cache.UpdateExisting(path, r => r.SetSuggestionCount(pending));
        return cache;
    }

    [Test]
    public async Task WithThePackOff_TheBadge_IsNeverRecomputed()
    {
        DemoCacheStore cache = CacheWithPending("/d/a.dem", 3);
        SuggestedInboxModule module = new(() => throw new InvalidOperationException("never built here"), () => false, cache);
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        await Assert.That(tab.Badge).IsNull().Because("the initial read is skipped while the pack is off");

        cache.UpdateExisting("/d/a.dem", r => r.SetSuggestionCount(9));
        await Assert.That(tab.Badge).IsNull().Because("cache.Changed must not recompute it while the pack is off");
    }

    [Test]
    public async Task WithThePackOn_TheBadge_FollowsTheIndex()
    {
        DemoCacheStore cache = CacheWithPending("/d/a.dem", 0);
        SuggestedInboxModule module = new(() => throw new InvalidOperationException("never built here"), () => true, cache);
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        await Assert.That(tab.Badge).IsNull();

        cache.UpdateExisting("/d/a.dem", r => r.SetSuggestionCount(5));
        await Assert.That(tab.Badge).IsEqualTo("5");
    }

    [Test]
    public async Task TheGatesOwnChanged_ClearsTheBadgeGoingOff_AndRecomputesGoingOn()
    {
        DemoCacheStore cache = CacheWithPending("/d/a.dem", 4);
        FakeGate gate = new();
        SuggestedInboxModule module = new(() => throw new InvalidOperationException("never built here"),
            () => gate.IsEnabled(SuggestedInboxModule.TabFeatureId), cache, gate);
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        await Assert.That(tab.Badge).IsEqualTo("4");

        gate.Answers[SuggestedInboxModule.TabFeatureId] = false;
        gate.RaiseChanged();
        await Assert.That(tab.Badge).IsNull().Because("the gate's own Changed clears a stale count going off");

        cache.UpdateExisting("/d/a.dem", r => r.SetSuggestionCount(9));
        await Assert.That(tab.Badge).IsNull().Because("cache.Changed still does nothing while off");

        gate.Answers[SuggestedInboxModule.TabFeatureId] = true;
        gate.RaiseChanged();
        await Assert.That(tab.Badge).IsEqualTo("9")
            .Because("the gate's own Changed recomputes going on, without waiting for a cache write");
    }

    private sealed class FakeGate : IFeatureGate
    {
        public Dictionary<string, bool> Answers { get; } = new(StringComparer.Ordinal);

        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) => !Answers.TryGetValue(featureId, out bool value) || value;

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
