#region

using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionFeed" /> parsing (strat-book-plugin.md §7.10): every member read, entries ranked
///     highest first, unknown members ignored, and every way an entry can disagree with itself refused.
/// </summary>
public class ExtensionFeedTests
{
    [Test]
    public async Task Parse_ReadsEveryMember_AndRanksEntriesHighestFirst()
    {
        byte[] bytes = [1, 2, 3, 4];
        string json = FakeFeeds.Json(
            new FakeFeeds.Entry("1.0.1", bytes),
            new FakeFeeds.Entry("1.2.0", RequiresHost: "^2.0", MinAppVersion: "0.9.0"),
            new FakeFeeds.Entry("1.1.0-rc1"));

        ExtensionFeed feed = ExtensionFeed.Parse(json);

        using (Assert.Multiple())
        {
            await Assert.That(feed.Id).IsEqualTo(FakeFeeds.Id);
            await Assert.That(feed.Entries.Select(e => e.Version.ToString())).IsEquivalentTo(["1.2.0", "1.1.0-rc1", "1.0.1"]);
            await Assert.That(feed.Latest!.Version).IsEqualTo(SemVersion.Parse("1.2.0"));
            ExtensionFeedEntry first = feed.Entries.Single(e => e.Version.ToString() == "1.0.1");
            await Assert.That(first.Manifest.Id).IsEqualTo(FakeFeeds.Id);
            await Assert.That(first.Manifest.Version).IsEqualTo(first.Version);
            await Assert.That(first.Manifest.Assembly).IsEqualTo("Fake.dll");
            await Assert.That(first.Url.ToString()).IsEqualTo($"https://example.invalid/releases/{FakeFeeds.Id}-v1.0.1/Fake-1.0.1.zip");
            await Assert.That(first.Sha256).IsEqualTo("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a");
            await Assert.That(first.Size).IsEqualTo(4);
            await Assert.That(first.PublishedAt).IsEqualTo(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
            await Assert.That(feed.Entries.Single(e => e.Version.ToString() == "1.2.0").Manifest.MinAppVersion).IsEqualTo(SemVersion.Parse("0.9.0"));
        }
    }

    [Test]
    public async Task Parse_EmptyFeed_UnknownMembers_CommentsAndTrailingCommas_UpperCaseSha()
    {
        ExtensionFeed empty = ExtensionFeed.Parse($$"""{ "id": "{{FakeFeeds.Id}}", "entries": [], "generatedBy": "ci" }""");
        ExtensionFeed lenient = ExtensionFeed.Parse($$"""
            {
              // the workflow may add members this app does not know
              "id": "{{FakeFeeds.Id}}",
              "channel": "stable",
              "entries": [
                {
                  "version": "1.0.0",
                  "manifest": {{FakeFeeds.Manifest(FakeFeeds.Id, new FakeFeeds.Entry("1.0.0"))}},
                  "url": "https://example.invalid/a.zip",
                  "sha256": "{{new string('A', 64)}}",
                  "size": 10,
                  "notes": "n/a",
                },
              ],
            }
            """);

        using (Assert.Multiple())
        {
            await Assert.That(empty.Entries).IsEmpty();
            await Assert.That(empty.Latest).IsNull();
            await Assert.That(lenient.Entries.Count).IsEqualTo(1);
            await Assert.That(lenient.Entries[0].Sha256).IsEqualTo(new string('a', 64)).Because("the hash is normalised to lower case");
            await Assert.That(lenient.Entries[0].PublishedAt).IsNull();
        }
    }

    [Test]
    [Arguments("{ not json", "not valid JSON")]
    [Arguments("[]", "must be a JSON object")]
    [Arguments("{ \"entries\": [] }", "'id' is required")]
    [Arguments("{ \"id\": \"../x\", \"entries\": [] }", "reverse-DNS")]
    [Arguments("{ \"id\": \"net demoviewer\", \"entries\": [] }", "reverse-DNS")]
    [Arguments("{ \"id\": \"net.demoviewer.pack.fake\" }", "'entries' is required")]
    [Arguments("{ \"id\": \"net.demoviewer.pack.fake\", \"entries\": {} }", "'entries' is required")]
    [Arguments("{ \"id\": \"net.demoviewer.pack.fake\", \"entries\": [ 1 ] }", "Every entry must be a JSON object")]
    public async Task Parse_RefusesABrokenFeed(string json, string reason)
    {
        ExtensionFeedException ex = Assert.Throws<ExtensionFeedException>(() => ExtensionFeed.Parse(json));
        await Assert.That(ex.Message).Contains(reason);
    }

    [Test]
    public async Task Parse_RefusesAnEntryThatDisagreesWithItself()
    {
        string manifest = FakeFeeds.Manifest(FakeFeeds.Id, new FakeFeeds.Entry("1.0.0"));
        string sha = new('0', 64);

        static string Entry(string version, string manifest, string url, string sha, string size, string extra = "") =>
            $$"""{ "id": "net.demoviewer.pack.fake", "entries": [ { "version": "{{version}}", "manifest": {{manifest}}, "url": "{{url}}", "sha256": "{{sha}}", "size": {{size}}{{extra}} } ] }""";

        (string Json, string Reason)[] cases =
        [
            (Entry("1.0.0", manifest, "https://example.invalid/a.zip", sha, "10", ", \"publishedAt\": \"yesterday\""), "'publishedAt'"),
            (Entry("one", manifest, "https://example.invalid/a.zip", sha, "10"), "not a semantic version"),
            (Entry("1.0.1", manifest, "https://example.invalid/a.zip", sha, "10"), "the manifest says version 1.0.0"),
            (Entry("1.0.0", FakeFeeds.Manifest("net.demoviewer.pack.other", new FakeFeeds.Entry("1.0.0")), "https://example.invalid/a.zip", sha, "10"), "not 'net.demoviewer.pack.fake'"),
            (Entry("1.0.0", "{ \"id\": \"net.demoviewer.pack.fake\" }", "https://example.invalid/a.zip", sha, "10"), "'name'"),
            (Entry("1.0.0", "\"text\"", "https://example.invalid/a.zip", sha, "10"), "'manifest' is required"),
            (Entry("1.0.0", manifest, "http://example.invalid/a.zip", sha, "10"), "absolute https URL"),
            (Entry("1.0.0", manifest, "/a.zip", sha, "10"), "absolute https URL"),
            (Entry("1.0.0", manifest, "https://example.invalid/a.zip", "abc", "10"), "64 hex characters"),
            (Entry("1.0.0", manifest, "https://example.invalid/a.zip", sha, "0"), "positive integer"),
            (Entry("1.0.0", manifest, "https://example.invalid/a.zip", sha, "\"10\""), "positive integer"),
            (Entry("1.0.0", manifest, "https://example.invalid/a.zip", sha, "1.5"), "positive integer")
        ];

        using (Assert.Multiple())
        {
            foreach ((string json, string reason) in cases)
            {
                ExtensionFeedException ex = Assert.Throws<ExtensionFeedException>(() => ExtensionFeed.Parse(json));
                await Assert.That(ex.Message).Contains(reason);
            }

            string duplicate = FakeFeeds.Json(new FakeFeeds.Entry("1.0.0"), new FakeFeeds.Entry("1.0.0"));
            ExtensionFeedException dup = Assert.Throws<ExtensionFeedException>(() => ExtensionFeed.Parse(duplicate));
            await Assert.That(dup.Message).Contains("appears twice");
        }
    }
}
