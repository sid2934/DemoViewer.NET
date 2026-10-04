#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions.Updates;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionFeedMerge" /> (strat-book-plugin.md §7.11): the rules item 37's release
///     workflow leans on to update the rolling feed without a hand-rolled script knowing semver. Every
///     fragment here is the shape <c>pack-extension.sh</c> actually emits (one bare entry object, not a
///     whole feed), produced the same way the script's own reference fixture is: through
///     <see cref="FakeFeeds" />.
/// </summary>
public class ExtensionPackagingTests
{
    [Test]
    public async Task Merge_NoExistingFeed_CreatesOneWithTheSingleEntry()
    {
        string merged = ExtensionFeedMerge.Merge(null, EntryJson("1.0.0"), FakeFeeds.Id, allowDowngrade: false);

        ExtensionFeed feed = ExtensionFeed.Parse(merged);
        using (Assert.Multiple())
        {
            await Assert.That(feed.Id).IsEqualTo(FakeFeeds.Id);
            await Assert.That(feed.Entries.Count).IsEqualTo(1);
            await Assert.That(feed.Latest!.Version.ToString()).IsEqualTo("1.0.0");
        }
    }

    [Test]
    public async Task Merge_BlankExistingFeed_IsTreatedTheSameAsNoFeed()
    {
        string merged = ExtensionFeedMerge.Merge("   ", EntryJson("1.0.0"), FakeFeeds.Id, allowDowngrade: false);
        ExtensionFeed feed = ExtensionFeed.Parse(merged);
        await Assert.That(feed.Entries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Merge_NewerVersion_GoesOnTop()
    {
        string existing = FakeFeeds.Json(new FakeFeeds.Entry("1.0.0"), new FakeFeeds.Entry("0.9.0"));
        string merged = ExtensionFeedMerge.Merge(existing, EntryJson("1.1.0"), FakeFeeds.Id, allowDowngrade: false);

        ExtensionFeed feed = ExtensionFeed.Parse(merged);
        await Assert.That(feed.Entries.Select(e => e.Version.ToString())).IsEquivalentTo(["1.1.0", "1.0.0", "0.9.0"]);
    }

    [Test]
    public async Task Merge_SameVersionAsAnExistingEntry_ReplacesItInPlace_NoDowngradeFlagNeeded()
    {
        string existing = FakeFeeds.Json(
            new FakeFeeds.Entry("2.0.0"),
            new FakeFeeds.Entry("1.0.0", Sha256: new string('a', 64)));

        // Replacing 1.0.0 (not the latest) with a corrected build never needs --allow-downgrade: it is
        // not publishing anything older than 2.0.0, it is correcting a line that already shipped.
        string merged = ExtensionFeedMerge.Merge(existing, EntryJson("1.0.0", sha256: new string('b', 64)), FakeFeeds.Id, allowDowngrade: false);

        ExtensionFeed feed = ExtensionFeed.Parse(merged);
        using (Assert.Multiple())
        {
            await Assert.That(feed.Entries.Count).IsEqualTo(2);
            await Assert.That(feed.Entries.Select(e => e.Version.ToString())).IsEquivalentTo(["2.0.0", "1.0.0"]);
            await Assert.That(feed.Entries.Single(e => e.Version.ToString() == "1.0.0").Sha256).IsEqualTo(new string('b', 64));
        }
    }

    [Test]
    public async Task Merge_VersionBelowCurrentLatest_IsRefused_UnlessAllowDowngrade()
    {
        string existing = FakeFeeds.Json(new FakeFeeds.Entry("2.0.0"));

        ExtensionFeedMergeException ex = Assert.Throws<ExtensionFeedMergeException>(
            () => ExtensionFeedMerge.Merge(existing, EntryJson("1.5.0"), FakeFeeds.Id, allowDowngrade: false));
        await Assert.That(ex.Message).Contains("not newer than the current latest 2.0.0");

        string merged = ExtensionFeedMerge.Merge(existing, EntryJson("1.5.0"), FakeFeeds.Id, allowDowngrade: true);
        ExtensionFeed feed = ExtensionFeed.Parse(merged);
        await Assert.That(feed.Entries.Select(e => e.Version.ToString())).IsEquivalentTo(["2.0.0", "1.5.0"]);
    }

    [Test]
    public async Task Merge_EntryIdDoesNotMatchTheFeed_IsRefused()
    {
        string existing = FakeFeeds.Json(new FakeFeeds.Entry("1.0.0"));
        string otherEntry = EntryJson("1.1.0", id: "net.demoviewer.pack.other");

        ExtensionFeedMergeException ex = Assert.Throws<ExtensionFeedMergeException>(
            () => ExtensionFeedMerge.Merge(existing, otherEntry, FakeFeeds.Id, allowDowngrade: false));
        await Assert.That(ex.Message).Contains("not '" + FakeFeeds.Id + "'");
    }

    [Test]
    public async Task Merge_ExistingFeedForAnotherId_IsRefused()
    {
        string existing = FakeFeeds.Json("net.demoviewer.pack.other", new FakeFeeds.Entry("1.0.0"));

        ExtensionFeedMergeException ex = Assert.Throws<ExtensionFeedMergeException>(
            () => ExtensionFeedMerge.Merge(existing, EntryJson("1.1.0"), FakeFeeds.Id, allowDowngrade: false));
        await Assert.That(ex.Message).Contains("the existing feed is for 'net.demoviewer.pack.other'");
    }

    // The bare entry object pack-extension.sh writes to disk for the workflow to merge: FakeFeeds.Json
    // wraps it in a whole feed, so this pulls the one entry back out rather than hand-writing a second,
    // divergent fixture shape.
    private static string EntryJson(string version, string id = FakeFeeds.Id, string? sha256 = null)
    {
        FakeFeeds.Entry entry = new(version, Sha256: sha256);
        JsonNode feed = JsonNode.Parse(FakeFeeds.Json(id, entry))!;
        return feed["entries"]![0]!.ToJsonString();
    }
}
