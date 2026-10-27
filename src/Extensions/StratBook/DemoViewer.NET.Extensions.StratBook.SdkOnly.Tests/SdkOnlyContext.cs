#region

using Avalonia.Input;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.Sdk.Review;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace SdkOnlyHost;

/// <summary>
///     A host assembled from the SDK's interfaces and nothing else: every switch off, no files, no queue.
///     That is the least an out-of-tree host owes an extension, and composing against it touches no disk
///     and no UI thread, so what it proves is the published surface rather than the app.
/// </summary>
internal sealed class SdkOnlyContext(string extensionId, string featureId) : IExtensionContext
{
    public string ExtensionId { get; } = extensionId;

    public string FeatureId { get; } = featureId;

    public HostInfo Host { get; } = new(new Version(2, 0, 0), null, false);

    public IExtensionFeatures Features { get; } = new EverythingOff();

    public IExtensionJobs Jobs { get; } = new NoQueue();

    public IExtensionPasses Passes { get; } = new NoPasses();

    public IExtensionShell Shell { get; } = new NoShell();

    public IExtensionStores Stores { get; } = new NoStores();

    public IExtensionSettings Settings { get; } = new InMemorySettings();

    public IExtensionDemoData Data { get; } = new NoDemoData();

    public IExtensionLibrary Library { get; } = new EmptyLibrary();

    public IExtensionNotifications Notifications { get; } = new NoNotifications();

    public IExtensionKeymap Keymap { get; } = new NoKeymap();

    public IExtensionIdentity Identity { get; } = new NoIdentity();

    public IExtensionReviewQueue Review { get; } = new EmptyReviewQueue();

    public ILogger CreateLogger(string category) => NullLogger.Instance;

    // No dispatcher here, so the caller's thread stands in for the UI thread.
    public void Post(Action action) => action();
}

/// <summary>Every feature answers off: what the user sees before switching the extension on.</summary>
internal sealed class EverythingOff : IExtensionFeatures
{
    public bool IsEnabled(string featureId) => false;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
///     A host with no processing queue, the browser's posture. Work is recorded and never run, so a
///     factory that queues during composition shows up as a recorded title instead of as side effects.
/// </summary>
internal sealed class NoQueue : IExtensionJobs
{
    private readonly List<string> _titles = [];

    public IReadOnlyList<string> Titles => _titles;

    public IJobHandle Enqueue(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _titles.Add(request.Title);
        return new RejectedJob();
    }

    public Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null)
    {
        _titles.Add(title);
        return Task.CompletedTask;
    }

    public void CancelAll() { }
}

internal sealed class RejectedJob : IJobHandle
{
    public JobStatus Status => JobStatus.Rejected;

    public Task<JobResult> Completion { get; } = Task.FromResult(new JobResult(JobStatus.Rejected));

    public event Action<JobResult>? Completed
    {
        add { }
        remove { }
    }

    public void Cancel() { }
}

internal sealed class NoPasses : IExtensionPasses
{
    private readonly List<string> _requested = [];

    public IReadOnlyList<string> Requested => _requested;

    public int RecheckCount { get; private set; }

    public void Request(string demoPath) => _requested.Add(demoPath);

    public void RecheckAll() => RecheckCount++;

    public bool IsBusy(string passId) => false;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}

internal sealed class NoShell : IExtensionShell
{
    public string? CurrentDemoPath => null;

    public CS2DemoKit.Parser.ParsedDemo? CurrentDemo => null;

    public string? CurrentDemoHash => null;

    public int CurrentTick => 0;

    public bool IsPlaying => false;

    public Task<bool> OpenDemoAsync(string path) => Task.FromResult(false);

    public void SeekToTick(int tick) { }

    public bool SelectTab(string tabId) => false;

    public void RevealInFileManager(string path) { }

    public event Action? CurrentDemoChanged
    {
        add { }
        remove { }
    }

    public event Action? PlayheadChanged
    {
        add { }
        remove { }
    }
}

/// <summary>
///     A host that hands out no folder and no file. Null is the documented "this host keeps nothing on
///     disk" answer, so an extension that cannot cope with it is broken on the browser head too.
/// </summary>
internal sealed class NoStores : IExtensionStores
{
    public IExtensionFolder? Open(string storeId) => null;

    public IExtensionFile? OpenFile(string storeId) => null;

    public IExtensionFolder? Own(StoreRoot root) => null;
}

internal sealed class InMemorySettings : IExtensionSettings
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public T Get<T>(string key, T fallback) =>
        _values.TryGetValue(key, out object? stored) && stored is T typed ? typed : fallback;

    public void Set<T>(string key, T value) => _values[key] = value;

    public bool Remove(string key) => _values.Remove(key);

    public event Action<string>? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
///     Per-demo data the host does not keep. <see cref="IsAvailable" /> false is what the browser head
///     answers, and an extension is expected to fall back to session state.
/// </summary>
internal sealed class NoDemoData : IExtensionDemoData
{
    public bool IsAvailable => false;

    public DemoDataStamp? Stamp(string demoPath, string facet) => null;

    public IReadOnlyList<DemoDataStamp> Stamps(string facet) => [];

    public byte[]? Read(string demoPath, string facet, int schema, string? fingerprint, string? part = null) => null;

    public DemoDataRecord? ReadAny(string demoPath, string facet, string? part = null) => null;

    public DemoDataStamp? Write(string demoPath, DemoDataWrite write) => null;

    public void MarkFailed(string demoPath, string facet) { }

    public void ClearFailed(string demoPath, string facet) { }

    public void Invalidate(string facet, string? demoPath = null) { }

    public void SetCount(string demoPath, string facet, int count) { }

    public void Delete(string demoPath, string facet) { }

    public event Action<string?>? Changed
    {
        add { }
        remove { }
    }
}

internal sealed class EmptyLibrary : IExtensionLibrary
{
    public IReadOnlyList<LibraryDemo> Demos => [];

    public LibraryDemo? Find(string path) => null;

    public LibraryDemo? FindBySha256(string sha256) => null;

    public LibraryPage Query(LibraryQuery query) => new([], 0);

    public Task<LibraryDemoDetail?> GetDetailAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<LibraryDemoDetail?>(null);

    public IAnalysisFacts Facts { get; } = new NoFacts();

    public event Action<LibraryChange>? Changed
    {
        add { }
        remove { }
    }
}

internal sealed class NoFacts : IAnalysisFacts
{
    public IReadOnlyList<FactKey> Declared => [];

    public FactStatus Status(string demoPath, FactKey key) => FactStatus.Absent;

    public bool IsCurrent(string demoPath, FactKey key) => false;

    public FactTable? TryGet(string demoPath, FactKey key) => null;

    public IReadOnlyList<LibraryHighlight> Highlights(string demoPath) => [];

    public IRoundFacts RoundFacts { get; } = new NoRoundFacts();
}

internal sealed class NoRoundFacts : IRoundFacts
{
    public int Schema => 0;

    public RoundFactsRows? TryGet(string demoPath) => null;

    public RoundFacts? RoundAt(string demoPath, int frameClockTick) => null;

    public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];

    public event Action<string>? Updated
    {
        add { }
        remove { }
    }
}

internal sealed class NoNotifications : IExtensionNotifications
{
    private readonly List<string> _posted = [];

    public IReadOnlyList<string> Posted => _posted;

    public void Post(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _posted.Add(notification.Id);
    }

    public void Dismiss(string id) { }
}

internal sealed class NoKeymap : IExtensionKeymap
{
    public IReadOnlyList<KeymapBinding> Bindings => [];

    public IReadOnlyList<KeyGesture> ShellReserved => [];

    public IReadOnlyList<KeyGesture> BrowserReserved => [];

    public string? ActionFor(string scope, Key key, KeyModifiers modifiers) => null;

    public string GestureText(string actionId) => "";

    public event Action? Changed
    {
        add { }
        remove { }
    }
}

internal sealed class NoIdentity : IExtensionIdentity
{
    public IReadOnlyList<string> Accounts => [];

    public string? ActiveAccount => null;

    public bool IsMine(string? steamId64) => false;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}

internal sealed class EmptyReviewQueue : IExtensionReviewQueue
{
    public IReadOnlyList<ReviewClip> Entries => [];

    public int ClipCount => 0;

    public int UnreviewedCount => 0;

    public string? FileProblem => null;

    public int Add(IEnumerable<ReviewClip> clips, string? sectionTitle = null, string sectionSubtitle = "") => 0;

    public void AddSection(string title) { }

    public int Remove(IEnumerable<Guid> ids) => 0;

    public void MoveBy(Guid id, int delta) { }

    public void Clear() { }

    public int SetReviewed(IEnumerable<Guid> ids, bool reviewed) => 0;

    public IReadOnlyList<ReviewClip> ClipsUnder(Guid cardId) => [];

    public bool SetTitle(Guid id, string title) => false;

    public bool SetNote(Guid id, string note) => false;

    public bool SetQuestion(Guid id, string question) => false;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
