#region

using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using DemoViewer.NET.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Walks the shell over a copy of a real app-data folder and prints, per section, the time to open
///     it and the longest UI-thread stall that followed. <c>DV_AUDIT_CONFIG</c> names the source folder;
///     it is copied to temp first and background processing is switched off in the copy, so nothing is
///     parsed and the source is never written.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class UiThreadAuditTests
{
    private static Window? _window;

    [Test]
    public async Task EverySection_OverTheOwnersLibrary()
    {
        if (Environment.GetEnvironmentVariable("DV_AUDIT_CONFIG") is not { Length: > 0 } source || !Directory.Exists(source))
        {
            throw new SkipTestException("DV_AUDIT_CONFIG is not set");
        }

        string dir = Path.Combine(Path.GetTempPath(), $"dv-ui-audit-{Guid.NewGuid():N}");
        CopyTree(source, dir);
        string settingsFile = Path.Combine(dir, "settings.json");
        if (File.Exists(settingsFile) && JsonNode.Parse(File.ReadAllText(settingsFile)) is JsonObject settings)
        {
            settings["ProcessingQueue"] = new JsonObject { ["BackgroundProcessingEnabled"] = false, ["MaxQueueSize"] = 500, ["MaxConcurrency"] = 1 };
            settings["Idle"] = new JsonObject { ["Enabled"] = false };
            File.WriteAllText(settingsFile, settings.ToJsonString());
        }

        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                ServiceProvider provider = App.BuildServices(new DesktopWindowService(() => null));
                try
                {
                    MainViewModel vm = provider.GetRequiredService<MainViewModel>();
                    double build = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    Window window = new() { Width = 1440, Height = 900, Content = new MainView(), DataContext = vm };
                    window.Show();
                    _window = window;
                    Settle();
                    Report("startup", build, sw.Elapsed.TotalMilliseconds, await Watch(TimeSpan.FromSeconds(8)));

                    Console.WriteLine($"[ui-audit] library entries={provider.GetRequiredService<Modules.Library.DemoLibraryService>().Entries.Count} "
                                      + $"cache index={provider.GetRequiredService<Services.DemoCache.DemoCacheStore>().Index.Count}");
                    await Bursts(provider, vm, "library");
                    vm.TrySelectTab("stratbook.browser");
                    Settle();
                    await Bursts(provider, vm, "strats");

                    await GrenadeMerges(provider, vm);

                    List<string> ids = [.. vm.Tabs.Select(t => t.TabId)];
                    ids.AddRange(vm.StratBookHub().Sections.Sections.Select(s => s.TabId));
                    ids.AddRange(vm.LibraryTab.Sections.Sections.Select(s => s.TabId));
                    string[] skip = ["builtin.parser", "builtin.entity", "builtin.analysis", "builtin.diagnostics", "ruleworkbench.editor"];
                    foreach (string id in ids.Distinct().Where(i => !skip.Contains(i)))
                    {
                        // Twice: the first open pays first-use costs, the second is what a user sees on every switch.
                        for (int pass = 1; pass <= 2; pass++)
                        {
                            vm.TrySelectTab(pass == 1 ? "builtin.library" : ids[0]);
                            Settle();
                            await Watch(TimeSpan.FromMilliseconds(300));
                            sw.Restart();
                            bool selected = vm.TrySelectTab(id);
                            Settle();
                            double open = sw.Elapsed.TotalMilliseconds;
                            Stall stall = await Watch(TimeSpan.FromSeconds(pass == 1 ? 4 : 2));
                            Report($"{id} open#{pass}{(selected ? "" : " (absent)")}", 0, open, stall);
                        }

                        if (ids.Contains(id) && vm.StratBookHub().Sections.Sections.Concat(vm.LibraryTab.Sections.Sections).Concat(vm.Tabs)
                                .FirstOrDefault(t => t.TabId == id)?.TabViewModel is { } tabVm)
                        {
                            await Interact(id, tabVm);
                        }

                        ScrollViewer? scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
                            .Where(s => s.IsEffectivelyVisible && s.Extent.Height > s.Viewport.Height * 3)
                            .OrderByDescending(s => s.Extent.Height).FirstOrDefault();
                        if (scroller is not null)
                        {
                            double worst = 0;
                            Stopwatch total = Stopwatch.StartNew();
                            for (int step = 1; step <= 20; step++)
                            {
                                Stopwatch one = Stopwatch.StartNew();
                                scroller.Offset = new Vector(0, step * 300);
                                Settle();
                                worst = Math.Max(worst, one.Elapsed.TotalMilliseconds);
                            }

                            foreach (ItemsControl heavy in window.GetVisualDescendants().OfType<ItemsControl>()
                                         .Where(c => c.IsEffectivelyVisible)
                                         .Select(c => (Control: c, Count: c.GetVisualDescendants().Count()))
                                         .OrderByDescending(c => c.Count).Take(6).Select(c => c.Control))
                            {
                                Console.WriteLine($"[ui-audit]   {id} list {heavy.GetType().Name} items={heavy.ItemCount} "
                                                  + $"visuals={heavy.GetVisualDescendants().Count()} first={heavy.Items.Cast<object?>().FirstOrDefault()?.GetType().Name}");
                            }

                            Console.WriteLine($"[ui-audit] {id} scroll: extent={scroller.Extent.Height:F0}px "
                                              + $"avg={total.Elapsed.TotalMilliseconds / 20:F1}ms worst={worst:F0}ms "
                                              + $"visuals={window.GetVisualDescendants().Count()}");
                            double worstJump = 0;
                            for (int step = 1; step <= 10; step++)
                            {
                                Stopwatch one = Stopwatch.StartNew();
                                scroller.Offset = new Vector(0, scroller.Extent.Height * step / 10.0);
                                Settle();
                                worstJump = Math.Max(worstJump, one.Elapsed.TotalMilliseconds);
                            }

                            Console.WriteLine($"[ui-audit] {id} jump through the whole extent in tenths: worst={worstJump:F0}ms "
                                              + $"visuals={window.GetVisualDescendants().Count()}");
                            scroller.Offset = default;
                            Settle();
                        }
                    }

                    sw.Restart();
                    SettingsViewModel settingsVm = provider.GetRequiredService<Func<SettingsViewModel>>()();
                    Window settingsWindow = new() { Width = 900, Height = 800, Content = new SettingsView { DataContext = settingsVm } };
                    settingsWindow.Show();
                    Settle();
                    Report("settings open", 0, sw.Elapsed.TotalMilliseconds, await Watch(TimeSpan.FromSeconds(2)));
                    settingsWindow.Close();

                    // Arrow-keying down the library: each selection renders Match Overview from the demo's record.
                    vm.TrySelectTab("builtin.library");
                    Settle();
                    double worstSelect = 0, totalSelect = 0;
                    foreach (Modules.Library.DemoEntry entry in vm.LibraryTab.FilteredEntries.Take(20).ToList())
                    {
                        Stopwatch one = Stopwatch.StartNew();
                        vm.LibraryTab.SelectedEntry = entry;
                        Settle();
                        worstSelect = Math.Max(worstSelect, one.Elapsed.TotalMilliseconds);
                        totalSelect += one.Elapsed.TotalMilliseconds;
                    }

                    Console.WriteLine($"[ui-audit] library select x20 (Match Overview preview): avg={totalSelect / 20:F0}ms worst={worstSelect:F0}ms");

                    // Last, since it changes the copy: a rescan that finds 100 demos gone (a folder removed).
                    Modules.Library.DemoLibraryService library = provider.GetRequiredService<Modules.Library.DemoLibraryService>();
                    List<(string, long, DateTime)> primaries = [.. library.Entries.SkipLast(100).Select(e => (e.FilePath, e.FileSizeBytes, e.Modified))];
                    System.Reflection.MethodInfo reconcile = typeof(Modules.Library.DemoLibraryService)
                        .GetMethod("Reconcile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    sw.Restart();
                    reconcile.Invoke(library, [primaries, new Dictionary<string, IReadOnlyList<string>>(), new List<Modules.Library.DemoEntry>(), new List<Modules.Library.DemoEntry>()]);
                    double removeSync = sw.Elapsed.TotalMilliseconds;
                    Settle();
                    Report($"library rescan drops 100 of {primaries.Count + 100}", removeSync, sw.Elapsed.TotalMilliseconds, await Watch(TimeSpan.FromSeconds(1)));
                    window.Close();
                }
                finally
                {
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Section-specific steps a user takes right after opening, each timed with the stall that follows.
    private static async Task Interact(string id, object tabVm)
    {
        async Task Step(string name, Action act)
        {
            Stopwatch sw = Stopwatch.StartNew();
            act();
            double direct = sw.Elapsed.TotalMilliseconds;
            Settle();
            double ms = sw.Elapsed.TotalMilliseconds;
            Report($"{id} {name}", direct, ms, await Watch(TimeSpan.FromSeconds(1.5)));
        }

        switch (tabVm)
        {
            case DemoViewer.NET.Extensions.StratBook.ViewModels.UtilityBook.UtilityBookTabViewModel u:
                foreach (string map in u.Maps.ToList())
                {
                    await Step($"map {map}", () => u.SelectedMap = map);
                }

                await Step("index Changed", () => Raise(u, "_index"));
                break;
            case DemoViewer.NET.Extensions.StratBook.ViewModels.Situations.SituationsTabViewModel situations:
                // An empty draft matches every indexed round on the map: the largest result set a user can ask for.
                foreach (string map in situations.Canvas.Maps.Where(m => m is "de_mirage" or "de_ancient").ToList())
                {
                    situations.Canvas.Map = map;
                    Settle();
                    await Step($"search everything on {map}", () => situations.Canvas.SearchCommand.Execute(null));
                    Console.WriteLine($"[ui-audit] situations {map}: cards={situations.Results.Cards.Count}");
                    if (_window?.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible)
                            .MaxBy(v => v.Extent.Height) is { } page)
                    {
                        page.Offset = new Vector(0, 1000);
                        Settle();
                    }

                    if (_window?.CaptureRenderedFrame() is { } frame)
                    {
                        string png = Path.Combine(HeadlessSession.ArtifactDir, $"ui-audit-situations-{map}.png");
                        frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                        Console.WriteLine($"[ui-audit] {png}");
                    }
                }

                break;
            case DemoViewer.NET.Extensions.StratBook.ViewModels.Teams.TeamsTabViewModel teamsTab:
                DemoViewer.NET.Extensions.StratBook.ViewModels.Teams.TeamRow? most = teamsTab.Teams.MaxBy(t => t.DemoCount);
                Console.WriteLine($"[ui-audit] teams rows={teamsTab.Teams.Count} most={most?.DemoCount}");
                await Step("select the team with most demos", () => teamsTab.SelectedTeam = most);
                Console.WriteLine($"[ui-audit] teams demo rows={teamsTab.Demos.Count}");
                for (int i = 0; i < 3; i++)
                {
                    await Step($"teams Changed #{i + 1}", () => Raise(teamsTab, "_teams"));
                }

                if (most is not null)
                {
                    DemoViewer.NET.Extensions.StratBook.Services.Teams.TeamIdentityService identity = (DemoViewer.NET.Extensions.StratBook.Services.Teams.TeamIdentityService)typeof(DemoViewer.NET.Extensions.StratBook.ViewModels.Teams.TeamsTabViewModel)
                        .GetField("_teams", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(teamsTab)!;
                    string name = most.Team.Name;
                    await Step("rename (a UI command: recompute and both saves)", () => identity.Rename(most.Team.Id, name + " audit"));
                    await Step("rename back", () => identity.Rename(most.Team.Id, name));
                }

                break;
            case DemoViewer.NET.Extensions.StratBook.ViewModels.RoundTagger.TagMatrixTabViewModel matrix:
                await Step("rows by demo", () => matrix.RowAxis = DemoViewer.NET.Extensions.StratBook.Services.Tags.TagMatrixAxis.Demo);
                await Watch(TimeSpan.FromSeconds(3));
                Console.WriteLine($"[ui-audit] tag matrix status: {matrix.StatusLine}");
                break;
            case DemoViewer.NET.Extensions.StratBook.ViewModels.Dossier.DossierTabViewModel dossier:
                // The team with the most demos is the costliest to project.
                DemoViewer.NET.Extensions.StratBook.Services.Teams.TeamIdentityService teams = (DemoViewer.NET.Extensions.StratBook.Services.Teams.TeamIdentityService)typeof(DemoViewer.NET.Extensions.StratBook.ViewModels.Dossier.DossierTabViewModel)
                    .GetField("_teams", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(dossier)!;
                DemoViewer.NET.Extensions.StratBook.ViewModels.Dossier.DossierTeamRow? biggest = dossier.Teams.MaxBy(t => teams.SidesOf(t.Id).Count);
                Console.WriteLine($"[ui-audit] dossier teams={dossier.Teams.Count} biggest={biggest?.Name} demos={(biggest is null ? 0 : teams.SidesOf(biggest.Id).Count)}");
                await Step("select biggest team", () => dossier.SelectedTeam = biggest);
                Console.WriteLine($"[ui-audit] dossier sections: {string.Join(" | ", dossier.MapSections.Select(m => m.Header + (m.IsExpanded ? " (open)" : "")))}; notes: {dossier.NotesSection.Header}");
                Capture("ui-audit-dossier.png");
                await Step("expand all", () => dossier.ExpandAllCommand.Execute(null));
                await Step("collapse all", () => dossier.CollapseAllCommand.Execute(null));
                for (int i = 0; i < 3; i++)
                {
                    await Step($"teams Changed #{i + 1}", () => Raise(dossier, "_teams"));
                }

                break;
            case DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratBookTabViewModel s:
                await Step("detected view", () => s.IsDetectedView = true);
                await Step("book view", () => s.IsDetectedView = false);
                foreach (DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook.StratOwnerOption owner in s.Owners.ToList())
                {
                    s.SelectedOwner = owner;
                    Settle();
                    if (s.Strats.FirstOrDefault() is { } row)
                    {
                        await Step($"open strat {row}", () => s.SelectedStrat = row);
                        for (int i = 0; i < 3; i++)
                        {
                            await Step($"session Changed #{i + 1}", () => Raise(s, "Session", "Changed"));
                        }

                        s.SelectedStrat = null;
                        Settle();
                    }
                }

                break;
        }
    }

    // Store Changed events the way an indexing pass raises them: one per demo, each run as its own
    // dispatcher job. Prints the UI cost per event.
    private static async Task Bursts(ServiceProvider provider, MainViewModel vm, string label)
    {
        Services.DemoCache.DemoCacheStore cache = provider.GetRequiredService<Services.DemoCache.DemoCacheStore>();
        string[] paths = [.. cache.Index.Take(40).Select(e => e.Path)];
        object[] stores =
        [
            cache,
            provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Services.Teams.TeamIdentityService>(),
            provider.GetRequiredService<Modules.Library.DemoLibraryService>(),
            provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Services.RoundIndex.SituationIndex>(),
            provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndex>(),
            provider.GetRequiredService<Services.DemoProcessing.DemoProcessingQueue>()
        ];
        foreach (object store in stores)
        {
            if (store.GetType().GetField("Changed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(store) is not Delegate d)
            {
                continue;
            }

            int n = paths.Length;
            double worst = 0;
            Stopwatch total = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                Stopwatch one = Stopwatch.StartNew();
                d.DynamicInvoke(d.Method.GetParameters().Length == 0 ? [] : [paths[i]]);
                Settle();
                worst = Math.Max(worst, one.Elapsed.TotalMilliseconds);
            }

            double ms = total.Elapsed.TotalMilliseconds;
            Stall after = await Watch(TimeSpan.FromSeconds(1));
            Console.WriteLine($"[ui-audit] burst {label} {store.GetType().Name}.Changed x{n}: per-event={ms / n:F1}ms worst={worst:F0}ms "
                              + $"total={ms:F0}ms handlers={d.GetInvocationList().Length} tail-stall={after.WorstMs:F0}ms");
            foreach (Delegate handler in d.GetInvocationList())
            {
                Stopwatch h = Stopwatch.StartNew();
                for (int i = 0; i < 5; i++)
                {
                    handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [paths[i]]);
                }

                double direct = h.Elapsed.TotalMilliseconds / 5;
                Settle();
                double withJobs = h.Elapsed.TotalMilliseconds / 5;
                if (withJobs >= 1)
                {
                    Console.WriteLine($"[ui-audit]   handler {handler.Method.DeclaringType?.FullName}.{handler.Method.Name}: "
                                      + $"direct={direct:F1}ms withPosted={withJobs:F1}ms");
                }
            }
        }
    }

    // A background grenade walk finishing, the way the evaluator announces it: from a worker, through the
    // marshal the composition root gave it. The Utility Book is open on the demo's map.
    private static async Task GrenadeMerges(ServiceProvider provider, MainViewModel vm)
    {
        DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndex grenades = provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndex>();
        DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndexEvaluator evaluator = provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndexEvaluator>();
        Stopwatch wait = Stopwatch.StartNew();
        while (!grenades.WhenLoaded.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Watch(TimeSpan.FromMilliseconds(200));
        }

        Console.WriteLine($"[ui-audit] grenade index ready after {wait.ElapsedMilliseconds}ms more; demos={grenades.DemoCount}");
        vm.TrySelectTab("utilitybook.browser");
        Settle();
        DemoViewer.NET.Extensions.StratBook.ViewModels.UtilityBook.UtilityBookTabViewModel? utility = vm.StratBookHub().Sections.Sections
            .First(s => s.TabId == "utilitybook.browser").TabViewModel as DemoViewer.NET.Extensions.StratBook.ViewModels.UtilityBook.UtilityBookTabViewModel;
        for (int i = 0; i < 50 && utility?.Maps.Count == 0; i++)
        {
            await Watch(TimeSpan.FromMilliseconds(100));
        }

        string map = utility?.Maps.Contains("de_mirage") == true ? "de_mirage" : utility?.Maps.FirstOrDefault() ?? "";
        if (utility is not null)
        {
            utility.SelectedMap = map;
        }

        Settle();
        await Watch(TimeSpan.FromMilliseconds(500));
        Services.DemoCache.DemoCacheStore cache = provider.GetRequiredService<Services.DemoCache.DemoCacheStore>();
        string[] paths = [.. cache.Index.Where(e => string.Equals(e.Map, map, StringComparison.OrdinalIgnoreCase)).Take(10).Select(e => e.Path)];
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        Action<Action> post = (Action<Action>)typeof(DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndexEvaluator).GetField("_post", Private)!.GetValue(evaluator)!;
        Delegate? indexed = (Delegate?)typeof(DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndexEvaluator).GetField("Indexed", Private)!.GetValue(evaluator);
        double worst = 0, blocked = 0;
        foreach (string path in paths)
        {
            await Task.Run(() => post(() => indexed?.DynamicInvoke(path)));
            Stall stall = await Watch(TimeSpan.FromMilliseconds(600));
            worst = Math.Max(worst, stall.WorstMs);
            blocked += stall.TotalBlockedMs;
        }

        Console.WriteLine($"[ui-audit] grenade walk finished x{paths.Length} on {map}, Utility open: worstStall={worst:F0}ms blocked={blocked:F0}ms");
        vm.TrySelectTab("builtin.library");
        Settle();
        worst = 0;
        blocked = 0;
        foreach (string path in paths)
        {
            await Task.Run(() => post(() => indexed?.DynamicInvoke(path)));
            Stall stall = await Watch(TimeSpan.FromMilliseconds(600));
            worst = Math.Max(worst, stall.WorstMs);
            blocked += stall.TotalBlockedMs;
        }

        Console.WriteLine($"[ui-audit] grenade walk finished x{paths.Length} on {map}, Utility hidden: worstStall={worst:F0}ms blocked={blocked:F0}ms");

        Stopwatch save = Stopwatch.StartNew();
        grenades.LineupStore.Save();
        Console.WriteLine($"[ui-audit] lineup store save (what a minted anchor costs whoever queries): {save.ElapsedMilliseconds}ms");
    }

    private static void Capture(string name)
    {
        if (_window?.CaptureRenderedFrame() is { } frame)
        {
            string png = Path.Combine(HeadlessSession.ArtifactDir, name);
            frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Console.WriteLine($"[ui-audit] {png}");
        }
    }

    // Fires a store's Changed event the way a background write would.
    private static void Raise(object owner, string field, string evt = "Changed")
    {
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        object? store = owner.GetType().GetField(field, Any)?.GetValue(owner) ?? owner.GetType().GetProperty(field, Any)?.GetValue(owner);
        if (store?.GetType().GetField(evt, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(store) is Delegate d)
        {
            d.DynamicInvoke(d.Method.GetParameters().Length == 0 ? [] : new object?[d.Method.GetParameters().Length]);
        }
    }

    // What the user waits for, from the click until the result is applied, with the work on the pool (as
    // before it moved into the queue) and in the queue: a Dossier team, a Utility map (also while a lineup
    // save holds the light lane), and an empty Situations search.
    [Test]
    public async Task TimeToResult_PoolAgainstQueue_OverTheOwnersLibrary()
    {
        if (Environment.GetEnvironmentVariable("DV_AUDIT_CONFIG") is not { Length: > 0 } source || !Directory.Exists(source))
        {
            throw new SkipTestException("DV_AUDIT_CONFIG is not set");
        }

        string dir = Path.Combine(Path.GetTempPath(), $"dv-ui-ttr-{Guid.NewGuid():N}");
        CopyTree(source, dir);
        string settingsFile = Path.Combine(dir, "settings.json");
        if (JsonNode.Parse(File.ReadAllText(settingsFile)) is JsonObject settings)
        {
            settings["ProcessingQueue"] = new JsonObject { ["BackgroundProcessingEnabled"] = false, ["MaxQueueSize"] = 500, ["MaxConcurrency"] = 1 };
            settings["Idle"] = new JsonObject { ["Enabled"] = false };
            File.WriteAllText(settingsFile, settings.ToJsonString());
        }

        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceProvider provider = App.BuildServices(new DesktopWindowService(() => null));
                try
                {
                    MainViewModel vm = provider.GetRequiredService<MainViewModel>();
                    Window window = new() { Width = 1440, Height = 900, Content = new MainView(), DataContext = vm };
                    window.Show();
                    DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndex grenades = provider.GetRequiredService<DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook.GrenadeIndex>();
                    await Until(() => grenades.WhenLoaded.IsCompleted, 60_000);
                    await Watch(TimeSpan.FromSeconds(3));
                    Services.DemoProcessing.IDemoProcessingQueue queue = provider.GetRequiredService<Services.DemoProcessing.IDemoProcessingQueue>();

                    foreach (bool bypass in (bool[])[true, false, true, false])
                    {
                        Services.DemoProcessing.QueueWork.Bypass = bypass;
                        string mode = bypass ? "pool " : "queue";

                        vm.TrySelectTab("dossier.browser");
                        Settle();
                        DemoViewer.NET.Extensions.StratBook.ViewModels.Dossier.DossierTabViewModel dossier = (DemoViewer.NET.Extensions.StratBook.ViewModels.Dossier.DossierTabViewModel)vm.StratBookHub().Sections.Sections
                            .First(s => s.TabId == "dossier.browser").TabViewModel!;
                        DemoViewer.NET.Extensions.StratBook.ViewModels.Dossier.DossierTeamRow team = dossier.Teams[0];
                        dossier.SelectedTeam = dossier.Teams.First(t => t != team);
                        await Until(() => !dossier.Openings.IsBuilding && !dossier.PostPlant.IsBuilding && !dossier.Situational.IsBuilding
                                          && dossier.HeatmapTask.IsCompleted, 30_000);
                        double dossierMs = await Timed(() => dossier.SelectedTeam = team,
                            () => !dossier.Openings.IsBuilding && !dossier.PostPlant.IsBuilding && !dossier.Situational.IsBuilding
                                  && dossier.HeatmapTask.IsCompleted);

                        vm.TrySelectTab("utilitybook.browser");
                        Settle();
                        DemoViewer.NET.Extensions.StratBook.ViewModels.UtilityBook.UtilityBookTabViewModel utility = (DemoViewer.NET.Extensions.StratBook.ViewModels.UtilityBook.UtilityBookTabViewModel)vm.StratBookHub().Sections.Sections
                            .First(s => s.TabId == "utilitybook.browser").TabViewModel!;
                        await Until(() => utility.Maps.Count > 1, 10_000);
                        int applied = 0;
                        utility.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(utility.Groups))
                            {
                                applied++;
                            }
                        };
                        string a = utility.Maps.Contains("de_mirage") ? "de_mirage" : utility.Maps[0];
                        string b = utility.Maps.Contains("de_nuke") ? "de_nuke" : utility.Maps[1];
                        utility.SelectedMap = a;
                        await Watch(TimeSpan.FromMilliseconds(300));
                        int before = applied;
                        double mapMs = await Timed(() => utility.SelectedMap = b, () => applied > before);
                        Task save = Services.DemoProcessing.QueueWork.Run(bypass ? null : queue, Services.DemoProcessing.QueueJobKind.StoreSave,
                            "Save: grenade lineups", "audit", _ => grenades.LineupStore.Save(), key: "save:grenade-lineups");
                        await Task.Delay(30);
                        before = applied;
                        double mapWithSaveMs = await Timed(() => utility.SelectedMap = a, () => applied > before);
                        await save;

                        vm.TrySelectTab("situations.search");
                        Settle();
                        DemoViewer.NET.Extensions.StratBook.ViewModels.Situations.SituationsTabViewModel situations = (DemoViewer.NET.Extensions.StratBook.ViewModels.Situations.SituationsTabViewModel)vm.StratBookHub().Sections.Sections
                            .First(s => s.TabId == "situations.search").TabViewModel!;
                        situations.Canvas.Map = situations.Canvas.Maps.Contains("de_ancient") ? "de_ancient" : situations.Canvas.Maps[0];
                        Settle();
                        double searchMs = await Timed(() => situations.Canvas.SearchCommand.Execute(null),
                            () => situations.Results.BatchTask.IsCompleted && situations.Results.Count > 0);

                        Console.WriteLine($"[ui-audit] time to result ({mode}): dossier select={dossierMs:F0}ms "
                                          + $"utility map={mapMs:F0}ms, with a lineup save running={mapWithSaveMs:F0}ms "
                                          + $"situations search to filled={searchMs:F0}ms ({situations.Results.Count} cards)");
                    }

                    Services.DemoProcessing.QueueWork.Bypass = false;
                    window.Close();
                }
                finally
                {
                    Services.DemoProcessing.QueueWork.Bypass = false;
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task Until(Func<bool> done, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Settle();
            await Task.Delay(2);
        }
    }

    private static async Task<double> Timed(Action act, Func<bool> done)
    {
        Stopwatch sw = Stopwatch.StartNew();
        act();
        await Until(done, 60_000);
        return sw.Elapsed.TotalMilliseconds;
    }

    // Work moved into the queue must not wait behind it: time from submit to done for a section build with
    // the queue idle, beside a background job, beside a parse, and behind a background light item; and the
    // UI thread's cost of the queue's own bookkeeping over a burst of builds.
    [Test]
    public async Task QueueLatency_AndItsUiCost()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using Services.HeavyJobGate gate = new();
            using ManualResetEventSlim release = new();
            using Services.DemoProcessing.DemoProcessingQueue queue = new(gate, a => Dispatcher.UIThread.Post(a), _ =>
            {
                release.Wait(TimeSpan.FromSeconds(20));
                return SyntheticParsedDemo.Create();
            }, _ => SyntheticParsedDemo.Create(), () => Task.CompletedTask);

            async Task<double> Build(Services.DemoProcessing.DemoJobPriority priority = Services.DemoProcessing.DemoJobPriority.Background)
            {
                Stopwatch sw = Stopwatch.StartNew();
                Task done = Services.DemoProcessing.QueueWork.Run(queue, Services.DemoProcessing.QueueJobKind.SectionCompute,
                    "probe", "audit", _ => Thread.Sleep(5), priority);
                while (!done.IsCompleted)
                {
                    Settle();
                    await Task.Delay(1);
                }

                return sw.Elapsed.TotalMilliseconds;
            }

            double idle = await Build();
            using SemaphoreSlim jobGate = new(0);
            Task job = Services.DemoProcessing.QueueWork.Run(queue, Services.DemoProcessing.QueueJobKind.Extension, "clips", "audit",
                _ => jobGate.Wait(TimeSpan.FromSeconds(20), CancellationToken.None));
            double besideJob = await Build();
            jobGate.Release();
            await job;
            queue.SubmitBackground(new Services.DemoProcessing.DemoProcessingRequest("/d/x.dem", "audit",
                Services.DemoProcessing.DemoJobPriority.Background, 0, _ => { }));
            await Task.Delay(100);
            double besideParse = await Build();
            release.Set();
            using SemaphoreSlim lightGate = new(0);
            Task slowLight = Services.DemoProcessing.QueueWork.Run(queue, Services.DemoProcessing.QueueJobKind.SectionCompute, "slow",
                "audit", ct =>
                {
                    lightGate.Wait(TimeSpan.FromSeconds(1), ct);
                    ct.ThrowIfCancellationRequested();
                });
            await Task.Delay(50);
            double userBehindLight = await Build(Services.DemoProcessing.DemoJobPriority.UserRequested);
            lightGate.Release();
            await slowLight;

            Stopwatch burst = Stopwatch.StartNew();
            double worst = 0;
            List<Task> builds = [.. Enumerable.Range(0, 40).Select(i => Services.DemoProcessing.QueueWork.Run(queue,
                Services.DemoProcessing.QueueJobKind.SectionCompute, "burst", "audit", _ => Thread.Sleep(2), key: "burst:" + (i % 8)))];
            while (builds.Any(b => !b.IsCompleted))
            {
                Stopwatch one = Stopwatch.StartNew();
                Settle();
                worst = Math.Max(worst, one.Elapsed.TotalMilliseconds);
                await Task.Delay(1);
            }

            Console.WriteLine($"[ui-audit] queue time-to-result: idle={idle:F0}ms besideBackgroundJob={besideJob:F0}ms "
                              + $"besideParse={besideParse:F0}ms userBehindSlowLightItem={userBehindLight:F0}ms; "
                              + $"burst of 40 section builds: {burst.ElapsedMilliseconds}ms, worst UI pump {worst:F1}ms");
        });
    }

    [Test]
    public async Task StoreLoadCosts_OverTheOwnersLibrary()
    {
        if (Environment.GetEnvironmentVariable("DV_AUDIT_CONFIG") is not { Length: > 0 } source || !Directory.Exists(source))
        {
            throw new SkipTestException("DV_AUDIT_CONFIG is not set");
        }

        // Read only: every constructor here loads, none saves.
        Stopwatch sw = Stopwatch.StartNew();
        Services.DemoCache.DemoCacheStore cache = new(Path.Combine(source, "cache"));
        double cacheMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        using Modules.Library.DemoLibraryService library = new(a => a(), Path.Combine(source, "library.json"));
        double libraryMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        string teamsRoot = Path.Combine(Path.GetTempPath(), $"dv-teams-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(teamsRoot, "cache"));
        File.Copy(Path.Combine(source, "teams.json"), Path.Combine(teamsRoot, "teams.json"));
        File.Copy(Path.Combine(source, "cache", "team-index.json"), Path.Combine(teamsRoot, "cache", "team-index.json"));
        sw.Restart();
        DemoViewer.NET.Extensions.StratBook.Services.Teams.TeamIdentityService teams = new(
            new DemoViewer.NET.Extensions.StratBook.Services.Teams.TeamIdentityFiles(Path.Combine(teamsRoot, "teams.json"),
                DemoViewer.NET.Extensions.StratBook.StoredFile.At(Path.Combine(teamsRoot, "cache", "team-index.json"))),
            cache.Library());
        double teamsCtor = sw.Elapsed.TotalMilliseconds;
        Directory.Delete(teamsRoot, true);
        Console.WriteLine($"[ui-audit] store loads: cache index={cacheMs:F0}ms ({cache.Index.Count} rows) "
                          + $"library.json={libraryMs:F0}ms ({library.Entries.Count}) teams={teamsCtor:F0}ms");
        await Task.CompletedTask;
    }

    internal readonly record struct Stall(double WorstMs, double TotalBlockedMs, int Over50);

    /// <summary>
    ///     Pumps the dispatcher for <paramref name="span" /> and times each pump: a pump runs every queued
    ///     job and one layout and render pass, so the longest one is the longest stall a user would feel.
    /// </summary>
    internal static async Task<Stall> Watch(TimeSpan span)
    {
        double worst = 0, blocked = 0;
        int over = 0;
        Stopwatch total = Stopwatch.StartNew();
        while (total.Elapsed < span)
        {
            Stopwatch one = Stopwatch.StartNew();
            Settle();
            double ms = one.Elapsed.TotalMilliseconds;
            worst = Math.Max(worst, ms);
            if (ms > 50)
            {
                over++;
                blocked += ms;
            }

            await Task.Delay(10);
        }

        return new Stall(worst, blocked, over);
    }

    internal static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Report(string step, double build, double open, Stall stall) =>
        Console.WriteLine($"[ui-audit] {step}: sync={build:F0}ms open={open:F0}ms worstStall={stall.WorstMs:F0}ms "
                          + $"stalls>50ms={stall.Over50} blocked={stall.TotalBlockedMs:F0}ms");

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, file);
            if (rel.StartsWith("logs", StringComparison.Ordinal) || rel.StartsWith("lineup-clips", StringComparison.Ordinal))
            {
                continue;
            }

            string target = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
