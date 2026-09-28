#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.ViewModels.UtilityBook;
using DemoViewer.NET.Views.UtilityBook;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Utility Book's position card shows the position's clip: read when the card opens, played frame by
///     frame, released when it closes. Over a copy of a real cache (<c>GV2_CACHE</c>) and a folder of real
///     clips (<c>GV2_CLIPS</c>, only read); the pair is copied into a temp folder under its lineup and
///     technique name, the rename Lineup Clip Render does on its next plan.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class UtilityCardClipTests
{
    [Test]
    public async Task ThePositionCard_PlaysItsClip_AndReleasesItOnClose()
    {
        string root = Environment.GetEnvironmentVariable("GV2_CACHE") ?? "";
        string source = Environment.GetEnvironmentVariable("GV2_CLIPS") ?? "";
        if (!Directory.Exists(root) || !Directory.Exists(source))
        {
            throw new SkipTestException("GV2_CACHE and GV2_CLIPS are not set to copies");
        }

        string clips = Directory.CreateTempSubdirectory("dv-card-clips-").FullName;
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                DemoCacheStore cache = new(root);
                using GrenadeIndex index = new(cache, new AssetZonePlaceResolverSource());
                index.Load();
                using UtilityBookTabViewModel vm = new(index, isBrowser: false, clipDirectory: clips);
                vm.SelectedMap = "de_mirage";

                // The busiest position with a pair on disk under the lineup's old name.
                (LandingGroup Group, GrenadeLineup Lineup, string Old)? pick = vm.Groups
                    .SelectMany(g => g.Lineups.Select(l => (Group: g, Lineup: l)))
                    .Where(x => x.Lineup.Techniques.Count > 0)
                    .OrderByDescending(x => x.Lineup.Throws.Count)
                    .Select(x => (x.Group, x.Lineup, Old: x.Lineup.AliasIds.Append(x.Lineup.Id)
                        .Select(id => Path.Combine(source, LineupClipPlanner.FileStem("de_mirage", x.Group.Kind, id) + LineupClipPlanner.GifExtension))
                        .FirstOrDefault(File.Exists) ?? ""))
                    .FirstOrDefault(x => x.Old.Length > 0);
                if (pick is not { } found)
                {
                    throw new SkipTestException("no mirage lineup has a clip in GV2_CLIPS");
                }

                LineupTechnique first = found.Lineup.Techniques[0];
                string stem = LineupClipPlanner.FileStem("de_mirage", found.Group.Kind, found.Lineup.Id, first.Key);
                File.Copy(found.Old, Path.Combine(clips, stem + LineupClipPlanner.GifExtension));
                File.Copy(LineupClipPlanner.SetposPathFor(found.Old), Path.Combine(clips, stem + LineupClipPlanner.SetposExtension));

                UtilityBookTabView view = new() { DataContext = vm };
                Window window = new() { Width = 1400, Height = 1000, Content = view };
                window.Show();
                vm.FocusLanding(found.Group.Id);
                vm.ClickThrow(UtilityBookTabViewModel.PositionKey(found.Lineup, first));
                Settle();

                GifView gif = view.GetVisualDescendants().OfType<GifView>().Single();
                await gif.Loading;
                for (int i = 0; i < 8; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(60);
                }

                Settle();
                window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "utility-card-clip.png"),
                    new PngBitmapEncoderOptions());
                Console.WriteLine($"[card-clip] {vm.Detail?.Title} {first.Label}: frames {gif.FrameCount}, on frame {gif.CurrentFrame}");
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Detail!.ClipPath).IsNotNull();
                    await Assert.That(gif.FrameCount).IsGreaterThan(1);
                    await Assert.That(gif.CurrentFrame).IsGreaterThan(0).Because("the clip plays");
                }

                vm.Back();
                Settle();
                await Assert.That(gif.FrameCount).IsEqualTo(0).Because("a closed card holds no clip");
                File.Delete(Path.Combine(clips, stem + LineupClipPlanner.GifExtension));
                await Assert.That(File.Exists(Path.Combine(clips, stem + LineupClipPlanner.GifExtension))).IsFalse();
                window.Close();
            });
        }
        finally
        {
            Directory.Delete(clips, true);
        }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
