#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>One spray a player produced, as a pickable row.</summary>
/// <param name="Run">The run itself.</param>
/// <param name="Index">1-based position in the player's list, for the label.</param>
public sealed record SprayRunItem(SprayRun Run, int Index)
{
    /// <summary>Label for the picker: which spray, and how many bullets landed in it.</summary>
    public string Label => $"#{Index}  ·  {Run.Shots.Count} hits";
}

/// <summary>
///     Backs the per-weapon spray plot on the player drilldown: which weapon, which spray, and what
///     the offsets are measured from.
///     <para>
///         Sampling is lazy and cached per origin on the parent, because the two modes cost very
///         different amounts. <see cref="SprayOrigin.FirstBullet" /> is an event walk;
///         <see cref="SprayOrigin.TargetCentre" /> additionally replays entity state to find where
///         each victim actually was, so it runs off the UI thread and reports busy.
///     </para>
///     <para>
///         <b>Empty is a real answer here.</b> A player with no qualifying spray gets a message rather
///         than a blank plot: <c>bullet_damage</c> only fires for bullets that HIT, so a run is the
///         landed subsequence of a trigger pull, and a player who sprayed all match but connected in
///         ones and twos genuinely has nothing to draw.
///     </para>
/// </summary>
public sealed partial class SprayViewModel : ObservableObject
{
    private readonly StatsTabViewModel _parent;
    private IReadOnlyList<PlayerSprays> _forPlayer = [];
    private int _slot = -1;
    private int _reload;

    internal SprayViewModel(StatsTabViewModel parent) => _parent = parent;

    /// <summary>Weapons this player has at least one qualifying spray with.</summary>
    public ObservableCollection<string> Weapons { get; } = [];

    /// <summary>Sprays available for <see cref="SelectedWeapon" />.</summary>
    public ObservableCollection<SprayRunItem> Runs { get; } = [];

    /// <summary>True when there is a plot to draw.</summary>
    [ObservableProperty]
    private bool _hasData;

    /// <summary>Shown instead of the plot when there is nothing to draw.</summary>
    [ObservableProperty]
    private string? _unavailableMessage;

    /// <summary>True while the target-centre sampler is replaying entity state.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Selected weapon; changing it reloads the run list.</summary>
    [ObservableProperty]
    private string? _selectedWeapon;

    /// <summary>Selected spray; changing it redraws the plot.</summary>
    [ObservableProperty]
    private SprayRunItem? _selectedRun;

    /// <summary>
    ///     False measures every bullet from the spray's first one (compensation alone); true measures
    ///     each from the victim's centre at that instant, so the origin moves and the plot shows
    ///     compensation and tracking together.
    /// </summary>
    [ObservableProperty]
    private bool _trackTarget;

    /// <summary>The weapon's recoil pattern, as the plot's reference trace.</summary>
    public IReadOnlyList<SprayPatternPoint> Pattern { get; private set; } = [];

    /// <summary>The selected spray's bullets.</summary>
    public IReadOnlyList<SpraySample> Shots => SelectedRun?.Run.Shots ?? [];

    /// <summary>Re-reads everything for a player.</summary>
    /// <param name="slot">Player slot.</param>
    internal void SetSlot(int slot)
    {
        _slot = slot;
        _ = ReloadAsync();
    }

    partial void OnTrackTargetChanged(bool value) => _ = ReloadAsync();

    partial void OnSelectedWeaponChanged(string? value) => RebuildRuns();

    partial void OnSelectedRunChanged(SprayRunItem? value)
    {
        OnPropertyChanged(nameof(Shots));
    }

    private async Task ReloadAsync()
    {
        if (_parent.LoadedDemo is not { } demo || _slot < 0)
        {
            Clear("Load a demo to see spray shapes.");
            return;
        }

        // The newest reload owns IsBusy and the lists. An older one that lands after it must touch
        // neither, or a slow TargetCentre sample overwrites the FirstBullet one already toggled to.
        int reload = ++_reload;
        SprayOrigin origin = TrackTarget ? SprayOrigin.TargetCentre : SprayOrigin.FirstBullet;
        IsBusy = true;
        try
        {
            SprayModel model = await _parent.SpraysAsync(demo, origin);
            if (reload != _reload)
            {
                return;
            }

            _forPlayer = [.. model.Players.Where(p => p.Slot == _slot)];
            _patternsByWeapon = model.IdealByWeapon;
            Populate();
        }
#pragma warning disable CA1031 // A sampling failure degrades the plot; it must not take the drilldown down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (reload == _reload)
            {
                // Surfaced to the reader rather than logged: the drilldown is where they are looking,
                // and an empty plot with no explanation is the failure this whole family keeps inviting.
                Clear($"Spray data could not be read from this demo ({ex.GetType().Name}).");
            }
        }
        finally
        {
            // Busy clears last. Whoever observes not-busy and a weapon list must also observe the
            // selection and shots that go with it.
            if (reload == _reload)
            {
                IsBusy = false;
            }
        }
    }

    private void Populate()
    {
        Weapons.Clear();
        foreach (string weapon in _forPlayer.Select(p => p.Weapon).OrderBy(w => w, StringComparer.Ordinal))
        {
            Weapons.Add(weapon);
        }

        if (Weapons.Count == 0)
        {
            Clear("No spray of three or more landed bullets this match.");
            return;
        }

        UnavailableMessage = null;
        HasData = true;

        // Assigning the SAME weapon back is not a property change, so the generated callback does not
        // fire and RebuildRuns never runs. That is the ordinary case after an origin toggle, because
        // the weapon list is rebuilt from the same match and comes back identical: without this the
        // picker and the plot would keep the previous origin's runs under the new label, which looks
        // entirely plausible and is the wrong geometry.
        if (string.Equals(SelectedWeapon, Weapons[0], StringComparison.Ordinal))
        {
            RebuildRuns();
            return;
        }

        SelectedWeapon = Weapons[0];
    }

    private IReadOnlyDictionary<string, IReadOnlyList<SprayPatternPoint>> _patternsByWeapon =
        new Dictionary<string, IReadOnlyList<SprayPatternPoint>>();

    private void RebuildRuns()
    {
        Runs.Clear();
        Pattern = SelectedWeapon is { } weapon
                  && _patternsByWeapon.TryGetValue(weapon, out IReadOnlyList<SprayPatternPoint>? pts)
            ? pts
            : [];
        OnPropertyChanged(nameof(Pattern));

        PlayerSprays? group = _forPlayer.FirstOrDefault(p => p.Weapon == SelectedWeapon);
        if (group is null)
        {
            SelectedRun = null;
            return;
        }

        int i = 1;
        foreach (SprayRun run in group.Runs.OrderByDescending(r => r.Shots.Count))
        {
            Runs.Add(new SprayRunItem(run, i++));
        }

        SelectedRun = Runs.Count > 0 ? Runs[0] : null;
    }

    private void Clear(string message)
    {
        Weapons.Clear();
        Runs.Clear();
        Pattern = [];
        SelectedRun = null;
        HasData = false;
        UnavailableMessage = message;
        OnPropertyChanged(nameof(Pattern));
        OnPropertyChanged(nameof(Shots));
    }
}
