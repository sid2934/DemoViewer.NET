#region

using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.Services.DemoProcessing;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionUpdateService" /> (strat-book-plugin.md §7.10) over a fake feed client and a temp
///     config root: every check outcome is a state, a download is verified before it is opened, unpacked
///     where nothing reads it, judged, and renamed into place only when every check passed; a refusal leaves
///     nothing behind; and every step runs as a user-priority queue item.
/// </summary>
public class ExtensionUpdateServiceTests
{
    private const string Id = FakeFeeds.Id;

    // ── Check ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Check_UpToDate_Available_NeedsNewerApp_Unreachable_Invalid_Unknown()
    {
        string root = NewRoot();
        try
        {
            FakeFeedClient client = new();
            ExtensionUpdateService service = NewService(root, client);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.0"), new FakeFeeds.Entry("0.9.0"));
            ExtensionUpdateState upToDate = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.1"), new FakeFeeds.Entry("1.2.0", RequiresHost: "^2.0"));
            ExtensionUpdateState available = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.2.0", RequiresHost: "^2.0"));
            ExtensionUpdateState needsApp = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.1.0", MinAppVersion: "0.9.5"));
            ExtensionUpdateState needsRelease = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = "{ nope";
            ExtensionUpdateState invalid = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json("net.demoviewer.pack.other", new FakeFeeds.Entry("9.0.0"));
            ExtensionUpdateState otherFeed = await service.CheckAsync(Id);

            client.FeedFailure = new HttpRequestException("offline");
            ExtensionUpdateState unreachable = await service.CheckAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(upToDate.Status).IsEqualTo(ExtensionUpdateStatus.UpToDate);
                await Assert.That(upToDate.Installed).IsEqualTo(SemVersion.Parse("1.0.0"));
                await Assert.That(upToDate.Latest!.Version).IsEqualTo(SemVersion.Parse("1.0.0"));
                await Assert.That(upToDate.Offered).IsNull();
                await Assert.That(upToDate.Name).IsEqualTo("Fake");
                await Assert.That(upToDate.CheckedAt).IsNotNull();

                await Assert.That(available.Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable);
                await Assert.That(available.Offered!.Version).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(available.Latest!.Version).IsEqualTo(SemVersion.Parse("1.2.0")).Because("the latest overall is reported beside the offered one");

                await Assert.That(needsApp.Status).IsEqualTo(ExtensionUpdateStatus.NeedsNewerApp);
                await Assert.That(needsApp.Offered).IsNull();
                await Assert.That(needsApp.Latest!.Version).IsEqualTo(SemVersion.Parse("1.2.0"));
                await Assert.That(needsApp.LatestCompatibility).IsTypeOf<PackCompatibility.HostContractMismatch>();

                await Assert.That(needsRelease.Status).IsEqualTo(ExtensionUpdateStatus.NeedsNewerApp);
                await Assert.That(needsRelease.LatestCompatibility).IsTypeOf<PackCompatibility.AppTooOld>();

                await Assert.That(invalid.Status).IsEqualTo(ExtensionUpdateStatus.FeedInvalid);
                await Assert.That(invalid.Error).IsEqualTo("the update feed could not be read");
                await Assert.That(invalid.LogDetail).Contains("not valid JSON");

                await Assert.That(otherFeed.Status).IsEqualTo(ExtensionUpdateStatus.FeedInvalid);
                await Assert.That(otherFeed.Error).IsEqualTo("the update feed is for another extension");

                await Assert.That(unreachable.Status).IsEqualTo(ExtensionUpdateStatus.FeedUnreachable);
                await Assert.That(unreachable.Error).IsEqualTo("the update feed could not be reached");
                await Assert.That(unreachable.LogDetail).IsEqualTo("offline");
                await Assert.That(unreachable.Installed).IsEqualTo(SemVersion.Parse("1.0.0")).Because("the running version is known even when the feed is not");

                await Assert.That(service.LastStates.Single().Status).IsEqualTo(ExtensionUpdateStatus.FeedUnreachable)
                    .Because("a per-pack check updates the remembered states");
            }

            ExtensionUpdateService noManifest = NewService(root, client, statuses: [StatusWithoutManifest(Id)]);
            IReadOnlyList<ExtensionUpdateState> all = await noManifest.CheckAsync();
            using (Assert.Multiple())
            {
                await Assert.That(all.Single().Status).IsEqualTo(ExtensionUpdateStatus.Unknown);
                await Assert.That(all.Single().Installed).IsNull();
                await Assert.That(all.Single().Name).IsEqualTo(Id);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // Decision 6 (§10) is one predicate: the default is the §7.7 check; either option replaces it here.
    [Test]
    public async Task Check_TheDecisionSixPredicate_IsTheOnlyGateOnWhatIsOffered()
    {
        string root = NewRoot();
        try
        {
            FakeFeedClient client = new();
            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.1"), new FakeFeeds.Entry("1.2.0", RequiresHost: "^2.0"));

            ExtensionUpdateState strict = await NewService(root, client, isOffered: (_, _) => false).CheckAsync(Id);
            ExtensionUpdateState loose = await NewService(root, client, isOffered: (_, _) => true).CheckAsync(Id);
            ExtensionUpdateState throwing = await NewService(root, client, isOffered: (_, _) => throw new InvalidOperationException("boom")).CheckAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(strict.Status).IsEqualTo(ExtensionUpdateStatus.NeedsNewerApp);
                await Assert.That(strict.Latest!.Version).IsEqualTo(SemVersion.Parse("1.2.0"));
                await Assert.That(loose.Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable);
                await Assert.That(loose.Offered!.Version).IsEqualTo(SemVersion.Parse("1.2.0"));
                await Assert.That(throwing.Status).IsEqualTo(ExtensionUpdateStatus.NeedsNewerApp).Because("a predicate that throws offers nothing");
                await Assert.That(ExtensionUpdateService.DefaultIsOffered(loose.Offered, Host)).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Download and stage ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Download_WrongShaOrSize_RefusedAndNothingStaged()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry wrongSha = new("1.0.1", zip, Sha256: new string('a', 64));
            FakeFeeds.Entry tooSmall = new("1.0.1", zip, Size: zip.Length - 1);
            FakeFeeds.Entry tooLarge = new("1.0.1", zip, Size: zip.Length + 1);
            FakeFeedClient client = new();
            client.Zips[wrongSha.Url] = zip;
            ExtensionUpdateService service = NewService(root, client);

            StageResult sha = await service.DownloadAndStageAsync(Entry(wrongSha));
            StageResult small = await service.DownloadAndStageAsync(Entry(tooSmall));
            StageResult large = await service.DownloadAndStageAsync(Entry(tooLarge));

            using (Assert.Multiple())
            {
                await Assert.That(sha.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(sha.Detail).IsEqualTo("the download does not match the feed's checksum");
                await Assert.That(small.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(small.Detail).IsEqualTo("the download failed");
                await Assert.That(large.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(large.Detail).IsEqualTo($"the download is {zip.Length} bytes; the feed said {zip.Length + 1}");
                await Assert.That(NothingStaged(root)).IsTrue();
                await Assert.That(service.PendingVersion(Id)).IsNull();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_ZipSlipEntry_Refused()
    {
        string root = NewRoot();
        try
        {
            string manifest = FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.1"));
            byte[][] evil =
            [
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("../escaped.txt", "x")),
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("sub/../../escaped.txt", "x")),
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("/etc/escaped.txt", "x")),
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("..\\escaped.txt", "x")),
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("C:\\escaped.txt", "x"))
            ];

            using (Assert.Multiple())
            {
                foreach (byte[] zip in evil)
                {
                    FakeFeeds.Entry entry = new("1.0.1", zip);
                    FakeFeedClient client = new();
                    client.Zips[entry.Url] = zip;
                    StageResult result = await NewService(root, client).DownloadAndStageAsync(Entry(entry));
                    await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Refused);
                    await Assert.That(result.Detail).Contains("escaped.txt");
                    await Assert.That(result.Detail).DoesNotContain(root).Because("Settings shows the detail; a path belongs in the log only");
                    await Assert.That(NothingStaged(root)).IsTrue();
                }

                await Assert.That(File.Exists(Path.Combine(root, "escaped.txt"))).IsFalse();
                await Assert.That(File.Exists(Path.Combine(root, "extensions", "escaped.txt"))).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_ManifestMissingOrMismatched_Refused()
    {
        string root = NewRoot();
        try
        {
            byte[] wrongVersion = Zip((ExtensionManifest.FileName, FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.2"))), ("Fake.dll", "x"));
            byte[] wrongId = Zip((ExtensionManifest.FileName, FakeFeeds.Manifest("net.demoviewer.pack.other", new FakeFeeds.Entry("1.0.1"))), ("Fake.dll", "x"));
            byte[] noManifest = Zip(("Fake.dll", "x"));
            byte[] nested = Zip(("inner/" + ExtensionManifest.FileName, FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.1"))), ("inner/Fake.dll", "x"));
            byte[] noAssembly = Zip((ExtensionManifest.FileName, FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.1"))));
            byte[] notAZip = "not a zip"u8.ToArray();

            (byte[] Zip, string Reason)[] cases =
            [
                (wrongVersion, $"the archive is {Id} 1.0.2, not {Id} 1.0.1"),
                (wrongId, $"the archive is net.demoviewer.pack.other 1.0.1, not {Id} 1.0.1"),
                (noManifest, $"the archive has no {ExtensionManifest.FileName} at its root"),
                (nested, $"the archive has no {ExtensionManifest.FileName} at its root"),
                (noAssembly, "the archive has no 'Fake.dll'"),
                (notAZip, "the archive could not be unpacked")
            ];

            using (Assert.Multiple())
            {
                foreach ((byte[] zip, string reason) in cases)
                {
                    FakeFeeds.Entry entry = new("1.0.1", zip);
                    FakeFeedClient client = new();
                    client.Zips[entry.Url] = zip;
                    StageResult result = await NewService(root, client).DownloadAndStageAsync(Entry(entry));
                    await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Refused);
                    await Assert.That(result.Detail).Contains(reason);
                    await Assert.That(NothingStaged(root)).IsTrue();
                }
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_Untrusted_RefusedAndTheStagingDirectoryRemoved()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new();
            client.Zips[entry.Url] = zip;
            RecordingTrust trust = new(false);
            ExtensionUpdateService service = NewService(root, client, trust);

            StageResult result = await service.DownloadAndStageAsync(Entry(entry));

            string extracted = Path.Combine(root, "extensions", ".staging", Id, "1.0.1");
            using (Assert.Multiple())
            {
                await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(result.Detail).IsEqualTo("the update is not signed by this app's publisher");
                await Assert.That(trust.Asked.Count).IsEqualTo(1);
                await Assert.That(trust.Asked[0].Directory).IsEqualTo(extracted).Because("the policy judges the unpacked copy before it is installed");
                await Assert.That(trust.Asked[0].Manifest.Version).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(Directory.Exists(extracted)).IsFalse();
                await Assert.That(NothingStaged(root)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_Success_StagesUnderTheVersionFolder_KeepsEveryFile_AndPendingRestartIsTrue()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1", ("Fake.xml", "<doc />"), ("de/", null), ("de/Fake.resources.dll", "res"));
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new();
            client.Zips[entry.Url] = zip;
            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(entry);
            RecordingTrust trust = new(true);
            ExtensionUpdateService service = NewService(root, client, trust);
            List<ExtensionDownloadProgress> progress = [];

            ExtensionUpdateState before = await service.CheckAsync(Id);
            StageResult result = await service.DownloadAndStageAsync(Entry(entry), new SyncProgress(progress));
            ExtensionUpdateState after = await service.CheckAsync(Id);

            string installed = Path.Combine(root, "extensions", Id, "1.0.1");
            using (Assert.Multiple())
            {
                await Assert.That(before.Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable);
                await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(result.Directory).IsEqualTo(installed);
                await Assert.That(Directory.Exists(installed)).IsTrue();
                await Assert.That(File.Exists(Path.Combine(installed, ExtensionManifest.FileName))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(installed, "Fake.dll"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(installed, "extension.sig"))).IsTrue().Because("the signature item 35 verifies travels intact");
                await Assert.That(File.Exists(Path.Combine(installed, "Fake.xml"))).IsTrue();
                await Assert.That(File.ReadAllText(Path.Combine(installed, "de", "Fake.resources.dll"))).IsEqualTo("res");
                await Assert.That(Directory.Exists(Path.Combine(root, "extensions", ".staging", Id))).IsFalse().Because("the staging folder is gone after the rename");
                await Assert.That(trust.Asked.Single().Directory).IsEqualTo(Path.Combine(root, "extensions", ".staging", Id, "1.0.1"));
                await Assert.That(progress.Count).IsGreaterThanOrEqualTo(2);
                await Assert.That(progress[^1]).IsEqualTo(new ExtensionDownloadProgress(zip.Length, zip.Length));
                await Assert.That(service.PendingVersion(Id)).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(service.PendingRestart).IsTrue();
                await Assert.That(after.Status).IsEqualTo(ExtensionUpdateStatus.PendingRestart);
                await Assert.That(after.Pending).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(after.Offered).IsNull();
            }

            // The loader finds exactly what was staged.
            ExtensionLoader.Discovery discovered = ExtensionLoader.Discover(Path.Combine(root, "extensions"));
            using (Assert.Multiple())
            {
                await Assert.That(discovered.Candidates.Select(c => c.Directory)).IsEquivalentTo([installed]);
                await Assert.That(discovered.Rejected).IsEmpty();
            }

            // A version newer than the staged one is offered over it.
            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(entry, new FakeFeeds.Entry("1.0.2"));
            ExtensionUpdateState newer = await service.CheckAsync(Id);
            using (Assert.Multiple())
            {
                await Assert.That(newer.Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable);
                await Assert.That(newer.Offered!.Version).IsEqualTo(SemVersion.Parse("1.0.2"));
                await Assert.That(newer.Pending).IsEqualTo(SemVersion.Parse("1.0.1"));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_ExistingTarget_IsANoOpSuccess()
    {
        string root = NewRoot();
        try
        {
            string installed = Path.Combine(root, "extensions", Id, "1.0.1");
            Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "marker.txt"), "mine");
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new();
            client.Zips[entry.Url] = zip;

            StageResult result = await NewService(root, client).DownloadAndStageAsync(Entry(entry));

            using (Assert.Multiple())
            {
                await Assert.That(result.Outcome).IsEqualTo(StageOutcome.AlreadyInstalled);
                await Assert.That(result.Directory).IsEqualTo(installed);
                await Assert.That(client.Downloads).IsEqualTo(0).Because("nothing is fetched for a version already on disk");
                await Assert.That(File.ReadAllText(Path.Combine(installed, "marker.txt"))).IsEqualTo("mine");
                await Assert.That(File.Exists(Path.Combine(installed, "Fake.dll"))).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_CancelledMidway_LeavesNoPartialFiles()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new()
            {
                HalfwayGate = new TaskCompletionSource()
            };
            client.Zips[entry.Url] = zip;
            ExtensionUpdateService service = NewService(root, client);
            using CancellationTokenSource cts = new();

            Task<StageResult> pending = service.DownloadAndStageAsync(Entry(entry), ct: cts.Token);
            await client.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string part = Path.Combine(root, "extensions", ".staging", Id, "1.0.1.zip.part");
            bool partExistedMidway = File.Exists(part);
            await cts.CancelAsync();
            StageResult result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

            using (Assert.Multiple())
            {
                await Assert.That(partExistedMidway).IsTrue().Because("the download was in flight when it was cancelled");
                await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Cancelled);
                await Assert.That(File.Exists(part)).IsFalse();
                await Assert.That(NothingStaged(root)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task EveryStep_RunsThroughTheQueue_AtUserPriority()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new();
            client.Zips[entry.Url] = zip;
            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(entry);
            RecordingQueue queue = new();
            ExtensionUpdateService service = NewService(root, client, queue: queue);

            await service.CheckAsync();
            await service.CheckAsync(Id);
            StageResult staged = await service.DownloadAndStageAsync(Entry(entry));
            await service.CleanupOnStartAsync();

            using (Assert.Multiple())
            {
                await Assert.That(staged.Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(queue.Requests.Count).IsEqualTo(4);
                await Assert.That(queue.Requests.Select(r => r.Title)).IsEquivalentTo(
                [
                    "Check for extension updates",
                    "Check for updates: Fake",
                    "Download extension update: Fake 1.0.1",
                    "Tidy staged extension updates"
                ]);
                await Assert.That(queue.Requests.Take(3).All(r => r.Priority == DemoJobPriority.UserRequested)).IsTrue()
                    .Because("the user pressed the button");
                await Assert.That(queue.Requests[3].Priority).IsEqualTo(DemoJobPriority.Background).Because("the startup sweep asked for nothing");
                await Assert.That(queue.Requests.All(r => r.Kind == QueueJobKind.ExtensionUpdate)).IsTrue();
                await Assert.That(queue.Requests.All(r => r.OwnerTag == ExtensionUpdateService.Owner)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Cleanup at start ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Cleanup_RemovesStagingAndSupersededVersions_KeepsTheRunningAndNewerOnes()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string staging = Path.Combine(extensions, ".staging", Id);
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "1.0.1.zip.part"), "half");
            Directory.CreateDirectory(Path.Combine(staging, "1.0.1"));
            File.WriteAllText(Path.Combine(staging, "1.0.1", "x"), "x");
            string older = StageVersion(extensions, "0.9.0");
            string running = StageVersion(extensions, "1.0.0");
            string newer = StageVersion(extensions, "1.0.1");
            string incompatible = StageVersion(extensions, "1.2.0", requiresHost: "^2.0");
            string otherPack = StageVersion(extensions, "0.1.0", id: "net.demoviewer.pack.other");
            string stray = Path.Combine(extensions, Id, "not-a-version");
            Directory.CreateDirectory(stray);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{}");

            ExtensionUpdateService service = NewService(root, new FakeFeedClient(),
                statuses: [Status(version: "1.0.0", source: new PackSource.Staged(running))]);
            service.Cleanup();

            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(Path.Combine(extensions, ".staging"))).IsFalse();
                await Assert.That(Directory.Exists(older)).IsFalse().Because("older than the running copy");
                await Assert.That(Directory.Exists(running)).IsTrue().Because("the running copy loaded from here");
                await Assert.That(Directory.Exists(newer)).IsTrue();
                await Assert.That(Directory.Exists(incompatible)).IsTrue().Because("it may load after an app update");
                await Assert.That(Directory.Exists(otherPack)).IsTrue().Because("not a declared extension; not ours to remove");
                await Assert.That(Directory.Exists(stray)).IsTrue().Because("no manifest, so no version to compare");
                await Assert.That(File.Exists(Path.Combine(root, "settings.json"))).IsTrue();
            }

            // With the bundled copy running at 1.0.0, the equal staged copy is superseded too.
            ExtensionUpdateService bundled = NewService(root, new FakeFeedClient(), statuses: [Status(version: "1.0.0")]);
            bundled.Cleanup();
            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(running)).IsFalse();
                await Assert.That(Directory.Exists(newer)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static ExtensionUpdateService NewService(string root, FakeFeedClient client, ITrustPolicy? trust = null,
        IDemoProcessingQueue? queue = null, IReadOnlyList<PackStatus>? statuses = null,
        Func<ExtensionFeedEntry, ExtensionHostInfo, bool>? isOffered = null) =>
        new(root, statuses ?? [Status()], Host, trust ?? TrustAll, client, FeedUrl, queue, isOffered);

    private static ExtensionFeedEntry Entry(FakeFeeds.Entry e) => ExtensionFeed.Parse(FakeFeeds.Json(e)).Entries.Single();

    private static string StageVersion(string extensions, string version, string id = Id, string requiresHost = "^1.0")
    {
        string dir = Path.Combine(extensions, id, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionManifest.FileName), FakeFeeds.Manifest(id, new FakeFeeds.Entry(version, RequiresHost: requiresHost)));
        File.WriteAllText(Path.Combine(dir, "Fake.dll"), "x");
        return dir;
    }

    // No file anywhere under extensions/: a refusal or a cancellation leaves at most empty folders.
    private static bool NothingStaged(string root)
    {
        string extensions = Path.Combine(root, "extensions");
        return !Directory.Exists(extensions) || !Directory.EnumerateFiles(extensions, "*", SearchOption.AllDirectories).Any();
    }

    private static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvupd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort
        }
    }

    // Progress<T> posts through the captured SynchronizationContext; this records inline.
    private sealed class SyncProgress(List<ExtensionDownloadProgress> sink) : IProgress<ExtensionDownloadProgress>
    {
        public void Report(ExtensionDownloadProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
