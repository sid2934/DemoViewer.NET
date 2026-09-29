#region

using Avalonia.Controls;
using Avalonia.Threading;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.ViewModels.UtilityBook;
using DemoViewer.NET.Views.StratBook;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The Strat Book's lineup picker open over the Strats section, on an in-memory store and an in-memory
///     Grenade Index of three de_mirage smoke lineups: a landing group focused, its second position selected.
/// </summary>
public static partial class Variants
{
    private const string PickerMap = "de_mirage";

    private static StratBookTabView StratLineupPicker()
    {
        GrenadeIndex index = PickerIndex();
        StratBookTabViewModel vm = new(new StratStore(null), null, a => Dispatcher.UIThread.Post(a), false, grenades: index,
            lineupMap: map => new UtilityBookTabViewModel(index, lockedMap: map));
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = PickerMap;
        vm.NewStratCommand.Execute(null);
        vm.Editor.Name = "A split through palace";
        vm.Editor.AddStepCommand.Execute(null);
        vm.Editor.Steps[^1].Actor = "B";
        vm.Editor.Steps[^1].Verb = "throw";
        vm.Editor.EndEditBurst();

        vm.OpenLineupPickerCommand.Execute(vm.Editor.Steps[^1]);
        LineupPickerViewModel picker = vm.LineupPicker!;
        LandingGroup window = picker.Map.Groups.OrderByDescending(g => g.ThrowCount).First();
        picker.Map.ClickLanding(window.Id);
        picker.SelectedPosition = picker.Positions[^1];
        return new StratBookTabView { DataContext = vm };
    }

    // Three smoke lineups over five demos: a window smoke thrown standing and jumping from T spawn, a jungle
    // smoke and a CT smoke, each landing well apart.
    private static GrenadeIndex PickerIndex()
    {
        (float X, float Y, float Z, bool Jump, int Demos, float LandX, float LandY, float LandZ)[] positions =
        [
            (1136, -254, -100, false, 3, -1150, -650, -160),
            (1141, -249, -100, true, 2, -1150, -650, -160),
            (380, -1450, -100, false, 3, -1350, -1150, -100),
            (-60, -2150, -100, true, 2, -2050, -1350, -160)
        ];
        DemoCacheStore cache = new(null);
        for (int n = 1; n <= 5; n++)
        {
            string path = $"/capture/picker-{n}.dem";
            List<GrenadeRow> rows = [];
            for (int p = 0; p < positions.Length; p++)
            {
                var at = positions[p];
                if (n > at.Demos)
                {
                    continue;
                }

                rows.Add(new GrenadeRow
                {
                    Id = $"s{p}",
                    Kind = GrenadeKind.Smoke,
                    ThrowerTeam = 2,
                    ReleaseTick = 1000 + p * 64,
                    ReleasePosition = new WorldPoint(at.X + n, at.Y, at.Z),
                    ReleaseEyeYaw = 180,
                    JumpThrow = at.Jump,
                    DetonationPosition = new WorldPoint(at.LandX + n * 8, at.LandY, at.LandZ),
                    EndKind = GrenadeEndKind.Detonated
                });
            }

            DemoCacheRecord record = new()
            {
                Path = path,
                Size = 10,
                ModifiedTicks = 20,
                Sha256 = $"picker{n}",
                Map = PickerMap,
                Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 }
            };
            GrenadeDocument document = new()
            {
                Demo = new GrenadeDemoHeader { Sha256 = $"picker{n}", StableKey = DemoCacheStore.StableKey(path) },
                Grenades = rows
            };
            cache.WriteSibling(path, GrenadeSidecar.Suffix, GrenadeSidecar.Serialize(document));
            DemoCacheStore.StampGrenades(record);
            record.GrenadeState = DemoAnalysisState.Indexed;
            record.GrenadeCount = rows.Count;
            record.GrenadeWalker = GrenadeWalker.Version;
            cache.Upsert(record);
        }

        GrenadeIndex index = new(cache);
        index.Load();
        return index;
    }
}
