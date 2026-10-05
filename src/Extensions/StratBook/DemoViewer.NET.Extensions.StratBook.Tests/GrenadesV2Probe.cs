#region

using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.ViewModels.UtilityBook;
using DemoViewer.NET.Views.UtilityBook;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Grenades v2 probe: the Utility Book over a copy of a real cache,
///     rendered with today's grid grouping and with the density grouping. Skips unless <c>GV2_CACHE</c>
///     names a cache root copy; never point it at the live cache, the store writes its index.
/// </summary>
[NotInParallel]
public class GrenadesV2Probe
{
    private const string Map = "de_mirage";

    [Test]
    [Category("Probe")]
    [Category("Render")]
    [Category("Integration")]
    [Arguments(GrenadeGrouping.Grid, "before")]
    [Arguments(GrenadeGrouping.Lineups, "after")]
    public async Task TheUtilityMap_OnARealCache(GrenadeGrouping grouping, string name)
    {
        string root = Environment.GetEnvironmentVariable("GV2_CACHE") ?? "";
        if (root.Length == 0 || !Directory.Exists(root))
        {
            throw new SkipTestException("GV2_CACHE is not set to a cache copy");
        }

        string map = Environment.GetEnvironmentVariable("GV2_MAP") is { Length: > 0 } m ? m : Map;
        StringBuilder report = new();
        await HeadlessSession.RunOnUi(() =>
        {
            DemoCacheStore cache = new(root);
            using GrenadeIndex index = new(cache, new AssetZonePlaceResolverSource()) { Grouping = grouping };
            long before = GC.GetTotalMemory(true);
            index.Load();
            long retained = GC.GetTotalMemory(true) - before;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            int clustered = index.Query(new GrenadeQuery(map)).Count;
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  index retained {retained / 1048576.0:0.0} MB after load; one all-kinds query on {map}: {clustered} clusters in {watch.ElapsedMilliseconds} ms");
            using UtilityBookTabViewModel vm = new(index, isBrowser: false);
            vm.SelectedMap = map;
            report.AppendLine(CultureInfo.InvariantCulture, $"{name} {map}: {vm.FooterLine} | {vm.HiddenLine}");
            IReadOnlyList<IndexedGrenade> smokes = index.Rows(new GrenadeQuery(map, new HashSet<GrenadeKind> { GrenadeKind.Smoke }));
            // What the map showed before the landing group key fix: one group per kind and seed cell, the last one written wins.
            List<GrenadeCluster> worthy = [.. index.Query(vm.CurrentQuery()!).Where(c => c.Lineups.Any(l => l.Throws.Count >= UtilityBookTabViewModel.LineupMinThrows))];
            Dictionary<string, GrenadeCluster> byCell = new(StringComparer.Ordinal);
            foreach (GrenadeCluster c in worthy)
            {
                byCell[string.Create(CultureInfo.InvariantCulture, $"{c.Kind}:{c.Cell}")] = c;
            }

            int Shown(GrenadeCluster c) => c.Lineups.Where(l => l.Throws.Count >= UtilityBookTabViewModel.LineupMinThrows).Sum(l => l.Throws.Count);
            GrenadeCluster? lost = worthy.Where(c => !byCell.ContainsValue(c)).OrderByDescending(Shown).FirstOrDefault();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  groups with a lineup >= 2: {worthy.Count}, throws {worthy.Sum(Shown)}; vm groups {vm.Groups.Count}; old seed-cell key keeps {byCell.Count} groups, {byCell.Values.Sum(Shown)} throws; largest lost: {(lost is null ? "none" : $"{LineupClipPlanner.Title(lost)} {Shown(lost)} throws at ({lost.Landing.X:0},{lost.Landing.Y:0})")}");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  index demos {index.DemoCount} grenades {index.GrenadeCount}; {map} smokes {smokes.Count} from {smokes.Select(s => s.Demo.Path).Distinct().Count()} demos");
            foreach (LandingGroup group in vm.Groups.Take(25))
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"  {group.Title,-40} at ({group.Landing.X:0},{group.Landing.Y:0},{group.Landing.Z:0}) throws {group.ThrowCount} lineups {group.Lineups.Count} top [{string.Join(",", group.Lineups.Take(6).Select(l => l.Throws.Count))}]");
            }

            UtilityBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1400, Height = 1000, Content = view };
            window.Show();
            Capture(window, $"gv2-{map}-{name}-map.png");

            string? focus = Environment.GetEnvironmentVariable("GV2_FOCUS");
            LandingGroup? target = focus is { Length: > 0 }
                ? vm.Groups.FirstOrDefault(g => g.Title.Contains(focus, StringComparison.OrdinalIgnoreCase))
                : vm.Groups.Count > 0 ? vm.Groups[0] : null;
            if (target is not null)
            {
                vm.FocusLanding(target.Id);
                Capture(window, $"gv2-{map}-{name}-focus.png");
                report.AppendLine(CultureInfo.InvariantCulture, $"  focus: {vm.FocusLine}");
                vm.ClickThrow(UtilityBookTabViewModel.LineupKey(target.Lineups[0]));
                if (vm.Detail is { } card)
                {
                    Capture(window, $"gv2-{map}-{name}-card.png");
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"  card: {card.StyleLine} | {card.UsedLine} | {card.TechniquesLine} | {card.ConsoleText} | first: {card.Instances.FirstOrDefault()?.PlayerText}");
                }
            }

            window.Close();
            return Task.CompletedTask;
        });

        Console.WriteLine(report.ToString());
        await File.WriteAllTextAsync(Path.Combine(HeadlessSession.ArtifactDir, $"gv2-{map}-{name}.txt"), report.ToString());
    }

    private static void Capture(Window window, string file)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        if (window.CaptureRenderedFrame() is { } frame)
        {
            frame.Save(Path.Combine(HeadlessSession.ArtifactDir, file), new PngBitmapEncoderOptions());
        }
    }
}
