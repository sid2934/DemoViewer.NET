#region

using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The doubles the update tests share: a feed client that serves canned text and bytes and never touches
///     the network, a queue that records every submission and runs it inline, a zip builder, trust policies,
///     and a declared fake pack with a manifest.
/// </summary>
internal static class UpdateFixtures
{
    public static readonly ExtensionHostInfo Host = new(SemVersion.Parse("1.0.0"), SemVersion.Parse("0.9.0"), SemVersion.Parse("0.13.0-beta0001"));

    /// <summary>The feed URL the tests hand the service for every pack.</summary>
    public static Uri FeedUrl(string packId) => new($"https://example.invalid/feeds/{packId}/{ExtensionFeed.FileName}");

    /// <summary>A zip with the given entries, in order; a name ending in '/' is a directory entry.</summary>
    public static byte[] Zip(params (string Name, string? Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string? content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                if (content is null)
                {
                    continue;
                }

                using Stream body = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                body.Write(bytes, 0, bytes.Length);
            }
        }

        return stream.ToArray();
    }

    /// <summary>The flat layout the release workflow produces for <paramref name="version" />: manifest, assembly, signature, plus extras.</summary>
    public static byte[] ExtensionZip(string id, string version, params (string Name, string? Content)[] extras) =>
        Zip(
        [
            (ExtensionManifest.FileName, FakeFeeds.Manifest(id, new FakeFeeds.Entry(version))),
            ("Fake.dll", "not really an assembly"),
            ("extension.sig", "not really a signature"),
            .. extras
        ]);

    /// <summary>A declared pack at <paramref name="version" />, as <c>FeaturePacks.Statuses</c> would carry it.</summary>
    public static PackStatus Status(string id = FakeFeeds.Id, string version = "1.0.0", PackSource? source = null) =>
        new(new FakePack(id), FakeManifests.For(id, "Fake", version), PackCompatibility.Compatible.Instance, source);

    /// <summary>A declared pack whose manifest could not be read.</summary>
    public static PackStatus StatusWithoutManifest(string id) =>
        new(new FakePack(id), null, new PackCompatibility.ManifestInvalid("unreadable"));

    public static ITrustPolicy TrustAll { get; } = new RecordingTrust(true);

    public static ITrustPolicy TrustNothing { get; } = new RecordingTrust(false);

    /// <summary>Serves what the test puts in it; counts and gates downloads so a test can cancel one mid-way.</summary>
    public sealed class FakeFeedClient : IExtensionFeedClient
    {
        public Dictionary<Uri, string> Feeds { get; } = [];

        public Dictionary<Uri, byte[]> Zips { get; } = [];

        /// <summary>Thrown by every feed fetch when set.</summary>
        public Exception? FeedFailure { get; set; }

        /// <summary>When set, a download writes the first half, reports it, then waits here before the rest.</summary>
        public TaskCompletionSource? HalfwayGate { get; set; }

        /// <summary>Completes when a gated download has written its first half.</summary>
        public TaskCompletionSource Halfway { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FeedFetches { get; private set; }

        public int Downloads { get; private set; }

        public Task<string> GetFeedAsync(Uri url, CancellationToken ct)
        {
            FeedFetches++;
            if (FeedFailure is not null)
            {
                throw FeedFailure;
            }

            return Feeds.TryGetValue(url, out string? text)
                ? Task.FromResult(text)
                : throw new HttpRequestException($"404 for {url}");
        }

        public async Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<long>? progress, CancellationToken ct)
        {
            Downloads++;
            if (!Zips.TryGetValue(url, out byte[]? bytes))
            {
                throw new HttpRequestException($"404 for {url}");
            }

            if (bytes.LongLength > maxBytes)
            {
                throw new ExtensionDownloadException("the download is larger than the feed said");
            }

            int half = bytes.Length / 2;
            await destination.WriteAsync(bytes.AsMemory(0, half), ct);
            progress?.Report(half);
            if (HalfwayGate is { } gate)
            {
                Halfway.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }

            await destination.WriteAsync(bytes.AsMemory(half), ct);
            progress?.Report(bytes.Length);
        }
    }

    /// <summary>Records every submission and runs its body inline, so the recorded order is the real order.</summary>
    public sealed class RecordingQueue : IDemoProcessingQueue
    {
        public List<QueueJobRequest> Requests { get; } = [];

        public ReadOnlyObservableCollection<DemoQueueItem> Items { get; } = new([]);
        public int MaxConcurrency { get; set; } = 1;
        public int MaxQueueSize { get; set; } = 200;
        public bool BackgroundEnabled { get; set; } = true;
        public bool IsPaused => false;
        public int QueuedCount => 0;
        public int RunningCount => 0;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public event Action? CapacityAvailable
        {
            add { }
            remove { }
        }

        public int ActiveCount(QueueJobKind kind) => 0;

        public Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitJob(QueueJobRequest request)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return new InlineHandle(Task.Run(() => request.RunAsync(new Context())));
        }

        public IReadOnlyList<DemoQueueItemSnapshot> Snapshot() => [];

        public void RemoveByUser(Guid itemId)
        {
        }

        public void CancelOwned(string ownerTag, string path)
        {
        }

        public void CancelOwned(string ownerTag)
        {
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        private sealed class Context : IQueueJobContext
        {
            public CancellationToken CancellationToken => CancellationToken.None;

            public void Report(int done, int total, string? detail = null)
            {
            }

            public Task StepAsideAsync() => Task.CompletedTask;

            public void ReleaseSlot()
            {
            }
        }

        private sealed class InlineHandle(Task completion) : IDemoQueueHandle
        {
            public Guid Id { get; } = Guid.NewGuid();
            public DemoQueueItemState State => completion.IsCompleted ? DemoQueueItemState.Completed : DemoQueueItemState.Running;
            public Task Completion => completion;

            public void Cancel()
            {
            }
        }
    }

    /// <summary>Answers one way and remembers what it was asked about.</summary>
    public sealed class RecordingTrust(bool answer) : ITrustPolicy
    {
        public List<(string Directory, ExtensionManifest Manifest)> Asked { get; } = [];

        public bool IsTrusted(string directory, ExtensionManifest manifest)
        {
            lock (Asked)
            {
                Asked.Add((directory, manifest));
            }

            return answer;
        }
    }

    private sealed class FakePack(string id) : IFeaturePack
    {
        public string Id => id;
        public string FeatureId => "pack." + id;
        public ExtensionManifest Manifest => FakeManifests.For(id);
        public IEnumerable<FeatureDescriptor> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
        }
    }
}
