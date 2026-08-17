#region

using System.Collections.ObjectModel;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Playback;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     VM-level coverage for the navigation-review Phase B filter convergence: the strip-ready
///     <see cref="EventFilterFlyoutViewModel" /> wraps the demo-derived <see cref="GameEventFilterItem" />
///     collection (the shell's <c>GameEventFilters</c>) without owning it, so toggling here is exactly
///     what the special-seek reads. No demo / dispatcher needed — pure observable VM behavior.
/// </summary>
public class EventFilterFlyoutTests
{
    private static readonly string[] _expectedRoundSet = ["round_freeze_end", "round_officially_ended"];

    private static ObservableCollection<GameEventFilterItem> SampleFilters() =>
    [
        new("player_death"),
        new("smokegrenade_detonate"), // a demo-derived event the legacy hardcoded list never had
        new("round_start")
    ];

    [Test]
    public async Task Flyout_WrapsTheSameCollection_NoCopy()
    {
        ObservableCollection<GameEventFilterItem> filters = SampleFilters();
        EventFilterFlyoutViewModel flyout = new(filters);

        // The flyout exposes the SAME instance (single source of truth), not a copy.
        await Assert.That(ReferenceEquals(flyout.Filters, filters)).IsTrue();
    }

    [Test]
    public async Task DeselectAll_ThenSelectAll_TogglesEveryItem()
    {
        ObservableCollection<GameEventFilterItem> filters = SampleFilters();
        EventFilterFlyoutViewModel flyout = new(filters);

        // All default to enabled.
        await Assert.That(filters.All(f => f.IsEnabled)).IsTrue();

        flyout.DeselectAllCommand.Execute(null);
        await Assert.That(filters.All(f => !f.IsEnabled)).IsTrue();
        // "Deselect-all = match any" is the navigator's job; here we just confirm the flyout cleared them.

        flyout.SelectAllCommand.Execute(null);
        await Assert.That(filters.All(f => f.IsEnabled)).IsTrue();
    }

    [Test]
    public async Task Tooltip_TracksEnabledCount_Live()
    {
        ObservableCollection<GameEventFilterItem> filters = SampleFilters();
        EventFilterFlyoutViewModel flyout = new(filters);

        bool tooltipChanged = false;
        flyout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EventFilterFlyoutViewModel.FilterTooltip))
            {
                tooltipChanged = true;
            }
        };

        // All enabled → "all event types".
        await Assert.That(flyout.FilterTooltip).Contains("all event types");

        // Toggling one item raises FilterTooltip and changes the text.
        filters[0].IsEnabled = false;
        await Assert.That(tooltipChanged).IsTrue();

        flyout.DeselectAllCommand.Execute(null);
        await Assert.That(flyout.FilterTooltip).Contains("match any");

        // Exactly one enabled → names that one event.
        filters[1].IsEnabled = true;
        await Assert.That(flyout.FilterTooltip).Contains(filters[1].EventName);
    }

    [Test]
    public async Task PresetRound_SelectsExactlyRoundStar_AndSummarizesAsRound()
    {
        // Mirrors a GOTV set: round lifecycle is round_freeze_end / round_officially_ended, not round_start.
        ObservableCollection<GameEventFilterItem> filters =
        [
            new("player_death"),
            new("round_freeze_end"),
            new("round_officially_ended"),
            new("bomb_planted")
        ];
        EventFilterFlyoutViewModel flyout = new(filters);

        flyout.PresetRoundCommand.Execute(null);

        // Exactly the round_* union is enabled — reproduces the removed NavPrev/NextRound target set.
        await Assert.That(filters.Where(f => f.IsEnabled).Select(f => f.EventName).OrderBy(n => n))
            .IsEquivalentTo(_expectedRoundSet);
        // And the chip reads the preset name, not "2 events".
        await Assert.That(flyout.TargetSummary).IsEqualTo("Round");
    }

    [Test]
    public async Task Presets_AnyEvent_And_Kills_DriveSummary()
    {
        ObservableCollection<GameEventFilterItem> filters = SampleFilters();
        EventFilterFlyoutViewModel flyout = new(filters);

        // All enabled by default → match-any.
        await Assert.That(flyout.TargetSummary).IsEqualTo("Any event");

        flyout.PresetKillsCommand.Execute(null);
        await Assert.That(filters.Single(f => f.IsEnabled).EventName).IsEqualTo("player_death");
        await Assert.That(flyout.TargetSummary).IsEqualTo("player_death");

        flyout.PresetAnyEventCommand.Execute(null);
        await Assert.That(filters.All(f => !f.IsEnabled)).IsTrue(); // none selected = match any
        await Assert.That(flyout.TargetSummary).IsEqualTo("Any event");
    }

    [Test]
    public async Task TargetSummary_RaisesPropertyChanged_Live()
    {
        ObservableCollection<GameEventFilterItem> filters = SampleFilters();
        EventFilterFlyoutViewModel flyout = new(filters);

        bool summaryChanged = false;
        flyout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EventFilterFlyoutViewModel.TargetSummary))
            {
                summaryChanged = true;
            }
        };

        filters[0].IsEnabled = false;
        await Assert.That(summaryChanged).IsTrue();
    }

    [Test]
    public async Task AppendedDemoEvent_IsSubscribed_TooltipUpdates()
    {
        ObservableCollection<GameEventFilterItem> filters = SampleFilters();
        EventFilterFlyoutViewModel flyout = new(filters);

        // Simulate the load-time append of a newly-seen demo event.
        GameEventFilterItem appended = new("weapon_reload");
        filters.Add(appended);

        bool tooltipChanged = false;
        flyout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EventFilterFlyoutViewModel.FilterTooltip))
            {
                tooltipChanged = true;
            }
        };

        // Toggling the appended item must raise the tooltip (proves it was subscribed on Add).
        appended.IsEnabled = false;
        await Assert.That(tooltipChanged).IsTrue();
    }
}
