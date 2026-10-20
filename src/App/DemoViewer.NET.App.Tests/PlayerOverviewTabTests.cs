#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.MatchMeta;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.PlayerOverview;
using DemoViewer.NET.Views.PlayerOverview.Sections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview tab shell: where it sits on the strip and when it is absent, its session blob, who it is
///     about as the user's accounts change, and the page-wide empty states. Every store is in memory and every
///     settings file in a throwaway folder.
/// </summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewTabTests
{
    private const ulong Me = 76561198000000001;
    private const ulong Alt = 76561198000000002;
    private const ulong Other = 76561198000000003;
    private const ulong Stranger = 76561198000000099;
    private static readonly DateTime Added = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
    private static readonly int[] NotAnObject = [1, 2];
    private static readonly string FacetStamp = StampedFacts.StampId(PlayerMatchFacet.RulesetId);

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvplayeroverview_" + Guid.NewGuid().ToString("N"));
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
            // best-effort cleanup
        }
    }

    private static string Id(ulong steamId64) => steamId64.ToString(CultureInfo.InvariantCulture);

    // Every demo has Me, Alt and Other on a side; the first `withStats` have their player_match facet written.
    private static DemoCacheStore Library(int demos, int withStats)
    {
        DemoCacheStore store = new(null);
        for (int i = 0; i < demos; i++)
        {
            DemoCacheRecord record = new()
            {
                Path = $"/d/{i}.dem",
                Size = 100,
                ModifiedTicks = 1,
                Map = "de_nuke",
                Players =
                [
                    new CachedPlayerInfo { Slot = 1, Name = "me", SteamId64 = Id(Me), Team = 2 },
                    new CachedPlayerInfo { Slot = 2, Name = "alt", SteamId64 = Id(Alt), Team = 3 },
                    new CachedPlayerInfo { Slot = 3, Name = "other", SteamId64 = Id(Other), Team = 3 }
                ],
                MatchMeta = new DemoMatchMeta { MatchTimeUtcTicks = (i + 1) * TimeSpan.TicksPerDay, MatchTimeSource = MatchTimeSource.ValveMatchInfo }
            };
            DemoCacheStore.StampHeader(record);
            DemoCacheStore.StampParse(record);
            if (i < withStats)
            {
                record.PackStamps.Add(new PackStamp(FacetStamp, StampedFacts.Schema, "fp") { ComputedAtTicks = 1 });
            }

            store.Upsert(record);
        }

        return store;
    }

    private static IdentityService Identity(params ulong[] accounts)
    {
        IdentityService identity = new(new SettingsService((string?)null));
        identity.SetAccounts(accounts.Select(a => new IdentityAccount(Id(a), null, Added)));
        return identity;
    }

    private static PlayerOverviewServices Services(PlayerDirectory directory, IdentityService identity, List<string?>? settingsOpened = null) =>
        new(directory, identity, null, _ => { }, _ => { }, (_, _) => { }, section => settingsOpened?.Add(section));

    // A shown tab over the store, so every change refreshes at once.
    private static PlayerOverviewTabViewModel Shown(DemoCacheStore store, IdentityService identity, FakeQueue? queue = null,
        List<string?>? settingsOpened = null)
    {
        PlayerOverviewTabViewModel vm = new(Services(new PlayerDirectory(store), identity, settingsOpened), queue);
        vm.OnActivated(null!);
        return vm;
    }

    // ── Strip, gating and the browser ────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Tab_SitsRightAfterStats_WhenTheShellHasADirectory()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me);
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(),
                players: new PlayerDirectory(Library(1, 0)), identity: identity);
            try
            {
                List<string> ids = [.. vm.Tabs.Select(t => t.TabId)];
                await Assert.That(vm.PlayerOverviewTab).IsNotNull();
                await Assert.That(ids.IndexOf(PlayerOverviewTabViewModel.TabId)).IsEqualTo(ids.IndexOf("builtin.stats") + 1);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Test]
    public async Task Tab_IsAbsent_WithoutADirectory()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty());
            try
            {
                await Assert.That(vm.PlayerOverviewTab).IsNull();
                await Assert.That(vm.Tabs.Any(t => t.TabId == PlayerOverviewTabViewModel.TabId)).IsFalse();
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Test]
    public async Task FeatureOverride_HidesTheTab()
    {
        string dir = NewTempDir();
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);
                svc.Write(s => s.UserCategory = UserCategory.Consumer);
                ServiceCollection services = new();
                services.Configure<AppSettings>(svc.Configuration);
                using ServiceProvider sp = services.BuildServiceProvider();
                using FeatureGate gate = new(sp.GetRequiredService<IOptionsMonitor<AppSettings>>(), false);
                using IdentityService identity = Identity(Me);
                MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(), gate: gate,
                    players: new PlayerDirectory(Library(1, 0)), identity: identity);
                try
                {
                    await Assert.That(vm.Tabs.Any(t => t.TabId == PlayerOverviewTabViewModel.TabId)).IsTrue()
                        .Because("the tab is on by default for every category");
                    svc.Write(s => s.Features.Overrides[PlayerOverviewTabViewModel.FeatureId] = false);
                    await Assert.That(vm.Tabs.Any(t => t.TabId == PlayerOverviewTabViewModel.TabId)).IsFalse();
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Test]
    public async Task BrowserGate_ClosesTheTab_DesktopKeepsIt()
    {
        await Assert.That(FeatureCatalog.All.Any(d => d.Id == PlayerOverviewTabViewModel.FeatureId)).IsTrue();
        await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds.Contains(PlayerOverviewTabViewModel.FeatureId)).IsTrue();
        using ShellModuleFeatureGate browser = new(null, () => true);
        using ShellModuleFeatureGate desktop = new(null, () => false);
        await Assert.That(browser.IsEnabled(PlayerOverviewTabViewModel.FeatureId)).IsFalse();
        await Assert.That(desktop.IsEnabled(PlayerOverviewTabViewModel.FeatureId)).IsTrue();
    }

    // ── Session ─────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Restore_DegradesToNothing_OnAMalformedBlob()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me);
            using PlayerOverviewTabViewModel vm = Shown(Library(2, 2), identity);
            JsonElement[] blobs =
            [
                JsonSerializer.SerializeToElement("this used to be a state blob"),
                JsonSerializer.SerializeToElement(NotAnObject),
                JsonSerializer.SerializeToElement(new { SubjectSteamId64 = 7, Pinned = "yes" }),
                JsonSerializer.SerializeToElement(new { SubjectSteamId64 = "not a number", Pinned = true }),
                JsonSerializer.SerializeToElement(new { SubjectSteamId64 = "0", Pinned = true })
            ];
            foreach (JsonElement blob in blobs)
            {
                vm.RestoreState(blob);
                await Assert.That(vm.SubjectSteamId64).IsEqualTo(Me).Because(blob.GetRawText());
                await Assert.That(vm.IsPinned).IsFalse();
            }

            vm.RestoreState(null);
            await Assert.That(vm.SubjectSteamId64).IsEqualTo(Me);
        });
    }

    [Test]
    public async Task Restore_ReopensAPinnedPlayer_AndFollowsTheActiveAccountOtherwise()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me);
            using PlayerOverviewTabViewModel pinned = Shown(Library(2, 2), identity);
            pinned.OpenPlayer(Other);
            JsonElement saved = JsonSerializer.SerializeToElement(pinned.SnapshotState());

            using PlayerOverviewTabViewModel restored = Shown(Library(2, 2), identity);
            restored.RestoreState(saved);
            await Assert.That(restored.SubjectSteamId64).IsEqualTo(Other);
            await Assert.That(restored.IsPinned).IsTrue();

            using PlayerOverviewTabViewModel following = Shown(Library(2, 2), identity);
            following.RestoreState(JsonSerializer.SerializeToElement(new PlayerOverviewSession(Id(Other), false)));
            await Assert.That(following.SubjectSteamId64).IsEqualTo(Me).Because("an unpinned subject follows the active account");
        });
    }

    // ── Subject rules ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Subject_FollowsTheActiveAccount_UntilTheUserOpensSomeoneElse()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me, Alt);
            using PlayerOverviewTabViewModel vm = Shown(Library(2, 2), identity);
            await Assert.That(vm.SubjectSteamId64).IsEqualTo(Me);

            identity.SetActive(Id(Alt));
            await Assert.That(vm.SubjectSteamId64).IsEqualTo(Alt).Because("an unpinned tab follows the active account");

            vm.OpenPlayer(Other);
            await Assert.That(vm.IsPinned).IsTrue();
            identity.SetActive(Id(Me));
            await Assert.That(vm.SubjectSteamId64).IsEqualTo(Other).Because("the user opened this player");

            vm.OpenPlayer(Me);
            await Assert.That(vm.IsPinned).IsFalse().Because("opening the active account is following it");
        });
    }

    [Test]
    public async Task Subject_RemovedFromAccounts_FallsBackToTheNewActive_ThenToNobody()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me, Alt);
            using PlayerOverviewTabViewModel vm = Shown(Library(2, 2), identity);
            vm.OpenPlayer(Alt);
            await Assert.That(vm.IsPinned).IsTrue();

            identity.SetAccounts([new IdentityAccount(Id(Me), null, Added)]);
            await Assert.That(vm.SubjectSteamId64).IsEqualTo(Me);
            await Assert.That(vm.IsPinned).IsFalse();

            identity.SetAccounts([]);
            await Assert.That(vm.SubjectSteamId64).IsNull();
            await Assert.That(vm.State).IsEqualTo(PlayerOverviewState.NoSubject);
        });
    }

    [Test]
    public async Task Subject_PinnedNonMeAccount_SurvivesAnAccountEdit()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me, Alt);
            using PlayerOverviewTabViewModel vm = Shown(Library(2, 2), identity);
            vm.OpenPlayer(Other);
            identity.SetAccounts([new IdentityAccount(Id(Alt), null, Added)]);
            await Assert.That(vm.SubjectSteamId64).IsEqualTo(Other);
        });
    }

    [Test]
    public async Task Switcher_ListsTheAccounts_AndSwitchingMakesOneActiveAndShowsIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me, Alt);
            using PlayerOverviewTabViewModel vm = Shown(Library(2, 2), identity);
            vm.OpenPlayer(Other);
            await Assert.That(vm.MeAccounts.Select(a => a.Title)).IsEquivalentTo(["me", "alt"]);
            await Assert.That(vm.MeAccounts.Single(a => a.IsActive).SteamId64).IsEqualTo(Id(Me));

            vm.SwitchAccountCommand.Execute(vm.MeAccounts.Single(a => a.SteamId64 == Id(Alt)));
            await Assert.That(identity.ActiveSteamId64).IsEqualTo(Id(Alt));
            await Assert.That(vm.SubjectSteamId64).IsEqualTo(Alt);
            await Assert.That(vm.IsPinned).IsFalse();
            await Assert.That(vm.MeAccounts.Single(a => a.IsActive).SteamId64).IsEqualTo(Id(Alt));
        });
    }

    // ── Empty states ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task EmptyStates_NameWhatIsMissing()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService nobody = Identity();
            List<string?> opened = [];
            using (PlayerOverviewTabViewModel empty = Shown(new DemoCacheStore(null), nobody))
            {
                await Assert.That(empty.State).IsEqualTo(PlayerOverviewState.NoPlayers);
                await Assert.That(empty.Detail).Contains("no demos with SteamIDs");
            }

            using (PlayerOverviewTabViewModel noSubject = Shown(Library(2, 2), nobody, settingsOpened: opened))
            {
                await Assert.That(noSubject.State).IsEqualTo(PlayerOverviewState.NoSubject);
                await Assert.That(noSubject.Detail).IsEqualTo("Search a player or set who you are.");
                await Assert.That(noSubject.ShowEmpty).IsTrue();
                noSubject.OpenIdentitySettingsCommand.Execute(null);
                await Assert.That(opened.Single()).IsEqualTo("SectionIdentity");
            }

            using IdentityService stranger = Identity(Stranger);
            using PlayerOverviewTabViewModel notFound = Shown(Library(2, 2), stranger);
            await Assert.That(notFound.State).IsEqualTo(PlayerOverviewState.SubjectNotFound);
            await Assert.That(notFound.Sections.Any(s => s.IsVisible)).IsFalse();
        });
    }

    [Test]
    public async Task Coverage_CountsTheSubjectsDemosWithStats_AndNamesTheQueueState()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me);
            FakeQueue queue = new() { QueuedCount = 3 };
            List<string?> opened = [];
            using PlayerOverviewTabViewModel vm = Shown(Library(5, 2), identity, queue, opened);

            await Assert.That(vm.State).IsEqualTo(PlayerOverviewState.Computing);
            await Assert.That(vm.Title).IsEqualTo("Stats for 2 of 5 demos");
            await Assert.That(vm.Detail).IsEqualTo("The library is computing the rest.");
            await Assert.That(vm.ShowQueueChip).IsTrue();
            await Assert.That(vm.QueueText).IsEqualTo("Processing · 3 in queue");
            await Assert.That(vm.ShowSections).IsTrue().Because("the stats that exist show beside the line");

            queue.SetPaused(true);
            await Assert.That(vm.State).IsEqualTo(PlayerOverviewState.Paused);

            queue.BackgroundEnabled = false;
            queue.Raise();
            await Assert.That(vm.State).IsEqualTo(PlayerOverviewState.ProcessingOff);
            await Assert.That(vm.Detail).StartsWith("3 demos have no player stats");
            vm.OpenProcessingSettingsCommand.Execute(null);
            await Assert.That(opened.Single()).IsEqualTo("SectionBackgroundProcessing");

            using PlayerOverviewTabViewModel ready = Shown(Library(3, 3), identity, queue);
            await Assert.That(ready.State).IsEqualTo(PlayerOverviewState.Ready);
            await Assert.That(ready.ShowBanner).IsFalse();
        });
    }

    [Test]
    public async Task HiddenTab_DefersItsRefresh_UntilShown()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me);
            DemoCacheStore store = Library(2, 0);
            using PlayerOverviewTabViewModel vm = new(Services(new PlayerDirectory(store), identity));
            await Assert.That(vm.Context).IsNull().Because("nothing is read before the tab is shown");

            vm.OnActivated(null!);
            await Assert.That(vm.Context!.WithStats).IsEqualTo(0);
            vm.OnDeactivated();

            DemoCacheRecord record = store.TryLoadRecord("/d/0.dem")!;
            record.PackStamps.Add(new PackStamp(FacetStamp, StampedFacts.Schema, "fp") { ComputedAtTicks = 1 });
            store.Upsert(record);
            await Assert.That(vm.Context!.WithStats).IsEqualTo(0);

            vm.OnActivated(null!);
            await Assert.That(vm.Context!.WithStats).IsEqualTo(1);
        });
    }

    // ── Sections and the view ───────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task View_RendersTheHeaderSection_ThroughTheViewLocator()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = Identity(Me);
            using PlayerOverviewTabViewModel vm = Shown(Library(2, 1), identity, new FakeQueue());
            HeaderSectionViewModel header = vm.Sections.OfType<HeaderSectionViewModel>().Single();
            await Assert.That(header.IsVisible).IsTrue();
            await Assert.That(header.Name).IsEqualTo("me");
            await Assert.That(header.IsMine).IsTrue();
            await Assert.That(header.DemoCountText).IsEqualTo("2 demos");

            Window window = new() { Width = 1200, Height = 700, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            await Assert.That(window.GetVisualDescendants().OfType<HeaderSectionView>().Count()).IsEqualTo(1);
            await Assert.That(vm.IsNarrow).IsFalse();
            window.Width = 700;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.IsNarrow).IsTrue();
            window.Close();
        });
    }

    // ── Search ───────────────────────────────────────────────────────────────────────────────────────────

    // Two accounts both called "Twin" (three demos and one), and Me as "me" in one more.
    private static DemoCacheStore TwinLibrary()
    {
        DemoCacheStore store = new(null);
        (string Path, ulong Id, string Name)[] rows =
        [
            ("/d/0.dem", Other, "Twin"), ("/d/1.dem", Other, "Twin"), ("/d/2.dem", Other, "Twin"),
            ("/d/3.dem", Alt, "Twin"), ("/d/4.dem", Me, "me")
        ];
        foreach ((string path, ulong id, string name) in rows)
        {
            DemoCacheRecord record = new()
            {
                Path = path, Size = 100, ModifiedTicks = 1, Map = "de_nuke",
                Players = [new CachedPlayerInfo { Slot = 1, Name = name, SteamId64 = Id(id), Team = 2 }]
            };
            DemoCacheStore.StampHeader(record);
            DemoCacheStore.StampParse(record);
            store.Upsert(record);
        }

        return store;
    }

    [Test]
    public async Task Search_PastedIdForms_OpenThatPlayerAtOnce()
    {
        using IdentityService identity = Identity(Me);
        using PlayerOverviewTabViewModel vm = Shown(TwinLibrary(), identity);
        foreach (string form in new[] { Id(Other), "https://steamcommunity.com/profiles/" + Id(Other), "[U:1:" + (Other - SteamIdParser.IndividualBase) + "]" })
        {
            vm.OpenPlayer(Me);
            vm.SearchText = form;
            await Assert.That(vm.SubjectSteamId64).IsEqualTo(Other).Because(form);
            await Assert.That(vm.IsPinned).IsTrue();
            await Assert.That(vm.SearchText).IsEqualTo("");
        }
    }

    [Test]
    public async Task Search_AName_ListsEachAccountMostDemosFirst_AndEnterOpensOnlyASingleOne()
    {
        using IdentityService identity = Identity(Me);
        using PlayerOverviewTabViewModel vm = Shown(TwinLibrary(), identity);
        vm.SearchText = "twin";
        await Assert.That(vm.Candidates.Select(c => c.SteamId64).ToArray()).IsEquivalentTo(new[] { Other, Alt });
        await Assert.That(vm.Candidates[0].Demos).IsEqualTo("3 demos");
        vm.SubmitSearchCommand.Execute(null);
        await Assert.That(vm.SubjectSteamId64).IsEqualTo(Me).Because("two candidates are not opened by Enter");

        vm.SearchText = "twin";
        vm.OpenCandidateCommand.Execute(vm.Candidates[1]);
        await Assert.That(vm.SubjectSteamId64).IsEqualTo(Alt);
        await Assert.That(vm.Candidates.Count).IsEqualTo(0);

        vm.SearchText = "me";
        vm.SubmitSearchCommand.Execute(null);
        await Assert.That(vm.SubjectSteamId64).IsEqualTo(Me);
    }

    [Test]
    public async Task Search_VanityUrl_SaysItIsOfflineAndMatchesTheName()
    {
        using IdentityService identity = Identity(Me);
        using PlayerOverviewTabViewModel vm = Shown(TwinLibrary(), identity);
        vm.SearchText = "https://steamcommunity.com/id/twin";
        await Assert.That(vm.SearchMessage).Contains("Online Connectivity");
        await Assert.That(vm.Candidates.Count).IsEqualTo(2);
        await Assert.That(vm.SubjectSteamId64).IsEqualTo(Me);
    }

    [Test]
    public async Task Search_NoMatch_SaysSo_AndClearingEmptiesTheList()
    {
        using IdentityService identity = Identity(Me);
        using PlayerOverviewTabViewModel vm = Shown(TwinLibrary(), identity);
        vm.SearchText = "nobody";
        await Assert.That(vm.HasSearchMessage).IsTrue();
        await Assert.That(vm.HasCandidates).IsFalse();
        vm.SearchText = "";
        await Assert.That(vm.HasSearchMessage).IsFalse();
    }

    [Test]
    public async Task Sections_EachHaveAViewOfTheSameName()
    {
        using IdentityService identity = Identity(Me);
        IReadOnlyList<PlayerOverviewSection> sections = PlayerOverviewSections.Create(Services(new PlayerDirectory(new DemoCacheStore(null)), identity),
            new PlayerOverviewFilterViewModel());
        await Assert.That(sections.Select(s => s.Id).Distinct().Count()).IsEqualTo(sections.Count);
        foreach (PlayerOverviewSection section in sections)
        {
            string view = section.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            await Assert.That(section.GetType().Assembly.GetType(view)).IsNotNull().Because(view);
        }
    }

    private sealed class FakeQueue : IDemoProcessingQueue
    {
        public ReadOnlyObservableCollection<DemoQueueItem> Items { get; } = new([]);
        public event Action? Changed;

        public event Action? CapacityAvailable
        {
            add { }
            remove { }
        }

        public int MaxConcurrency { get; set; } = 1;
        public int MaxQueueSize { get; set; } = 200;
        public bool BackgroundEnabled { get; set; } = true;
        public bool IsPaused { get; private set; }
        public int QueuedCount { get; set; }
        public int RunningCount { get; set; }

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            Raise();
        }

        public void Raise() => Changed?.Invoke();

        public int ActiveCount(QueueJobKind kind) => 0;

        public Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitVisit(DemoVisitRequest request) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitJob(QueueJobRequest request) => throw new NotSupportedException();

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

        public void Pause() => SetPaused(true);

        public void Resume() => SetPaused(false);
    }
}
