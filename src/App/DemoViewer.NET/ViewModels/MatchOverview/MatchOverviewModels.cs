#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.ViewModels.MatchOverview;

/// <summary>
///     Which of its two jobs the Match Overview page is doing right now.
///     <para>
///         The page is a CACHE RENDER with a live mode, not a landing page with a cache bolt-on: both modes
///         paint the same sections from the same slots, and the only difference is where the values came from
///         and whether a pipeline is running behind them. That is what makes opening a demo you were
///         previewing produce no visual discontinuity — the cached render IS the skeleton the live fill lands
///         into.
///     </para>
/// </summary>
public enum OverviewMode
{
    /// <summary>No demo is being shown at all.</summary>
    Empty,

    /// <summary>A demo is open (or opening) and the shell is pushing pipeline state into the page.</summary>
    Live,

    /// <summary>A demo is being rendered from its cache record. Nothing is running; nothing will arrive.</summary>
    Cached
}

/// <summary>
///     How complete the data behind the page is — the single honest answer to "why is this section empty?".
///     Every state maps to one action that advances it (see
///     <see cref="MatchOverviewTabViewModel.CompletenessActionLabel" />), so the user is never left inferring.
/// </summary>
public enum OverviewCompleteness
{
    /// <summary>No demo — the empty state owns the page.</summary>
    None,

    /// <summary>A pipeline is running right now.</summary>
    Live,

    /// <summary>Tier 3 present and current: scoreboard and highlights are real.</summary>
    Full,

    /// <summary>Tier 2 present, tier 3 absent or stale: identity, rosters and score are real; stats are not.</summary>
    Indexed,

    /// <summary>Header only (or less) — nothing has parsed this demo yet.</summary>
    NotIndexed,

    /// <summary>The last pass threw. Retryable.</summary>
    Failed
}

/// <summary>
///     One highlight group on the Match Overview highlight section — a player and the moments they produced.
///     <para>
///         Grouped by <c>PlayerSlot</c> and resolved against the record's roster, because the unified cache
///         stores highlights by SLOT rather than re-storing a name on every event row. Team identity rides the
///         coloured dot beside a neutral name, never the text colour — the page-wide rule.
///     </para>
/// </summary>
public sealed partial class OverviewHighlightGroup(string playerName, int team, bool isExpanded = false)
    : ObservableObject
{
    private bool _isExpanded = isExpanded;

    /// <summary>Sanitized display name — hostile bidi/combining-mark names crash Avalonia's wrap splitter.</summary>
    public string PlayerName { get; } = playerName;

    /// <summary>2 = T, 3 = CT (parser convention).</summary>
    public int Team { get; } = team;

    /// <summary>Drives the CT variant of the shared team dot.</summary>
    public bool IsCt => Team == 3;

    public ObservableCollection<OverviewHighlightRow> Highlights { get; } = [];

    /// <summary>
    ///     Expander state. Lives here (not in the view) so it survives the tab's view teardown. Defaults to
    ///     COLLAPSED: a demo can produce a dozen per-player sections, and a wall of pre-expanded lists buries
    ///     the "which players had moments" overview the section leads with — the user opens the one they want.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Header count, e.g. "4".</summary>
    public string CountDisplay => Highlights.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>Raised for the view once the rows are in, so the header count is not read before it is right.</summary>
    public void NotifyRowsChanged() => OnPropertyChanged(nameof(CountDisplay));

    /// <summary>
    ///     Header "Select all" — stages every not-yet-staged highlight in this player's section into the reel
    ///     tray. Deliberately ADD-ONLY: the per-row Stage command TOGGLES, so invoking it on an already-staged
    ///     row would remove it; pressing "Select all" only ever adds, so it is safe to press repeatedly and
    ///     never silently un-stages a clip the user already took.
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        foreach (OverviewHighlightRow row in Highlights)
        {
            if (!row.IsStaged)
            {
                row.StageCommand.Execute(null);
            }
        }
    }
}

/// <summary>
///     One fired highlight on the Match Overview highlight section. <see cref="StageCommand" />
///     flips <see cref="IsStaged" /> and forwards to the injected callback, whose other end is the
///     Reels dashboard's clip tray (wired by the composition root via <c>ReelTrayLocator</c>).
/// </summary>
public sealed partial class OverviewHighlightRow : ViewModelBase
{
    private readonly Action<OverviewHighlightRow>? _onStage;
    private readonly Func<OverviewHighlightRow, Task>? _onVerify;

    [ObservableProperty]
    private bool _isStaged;

    [ObservableProperty]
    private bool _isVerifying;

    /// <param name="title">The rendered title, captured at emission and sanitized for display.</param>
    /// <param name="rawPlayerName">The RAW in-demo name — CSVG's spectate currency, never sanitized.</param>
    /// <param name="tick">Frame-clock tick. Passed AS-IS; never converted to server-tick space.</param>
    /// <param name="roundNumber">Round the highlight fired in, or 0 when unknown.</param>
    /// <param name="typeKey">Qualified <c>{rulesetId}.{highlightId}</c> — the filter identity.</param>
    /// <param name="verifyPresent">Whether the Verify affordance exists at all (the chrome.livesync gate).</param>
    /// <param name="onStage">Staging callback; null leaves the button inert.</param>
    /// <param name="onVerify">Verify callback; null leaves the button inert.</param>
    public OverviewHighlightRow(
        string title,
        string rawPlayerName,
        int tick,
        int roundNumber,
        string typeKey,
        bool verifyPresent,
        Action<OverviewHighlightRow>? onStage,
        Func<OverviewHighlightRow, Task>? onVerify)
    {
        Title = title;
        RawPlayerName = rawPlayerName;
        Tick = tick;
        RoundNumber = roundNumber;
        TypeKey = typeKey;
        VerifyPresent = verifyPresent;
        _onStage = onStage;
        _onVerify = onVerify;
    }

    public string Title { get; }

    /// <summary>RAW name for the CS2 spectate call. Display goes through the sanitized <see cref="Title" />.</summary>
    public string RawPlayerName { get; }

    public int Tick { get; }
    public int RoundNumber { get; }
    public string TypeKey { get; }

    /// <summary>
    ///     The demo this highlight belongs to, and the player slot it fired for — together with
    ///     <see cref="TypeKey" /> and <see cref="Tick" /> they are the tray's clip identity.
    ///     <para>
    ///         Carried on the ROW rather than closed over by the staging callback because a reel is
    ///         cross-demo: the tray keys clips by <c>(demoPath, rulesetId, highlightId, tick, playerSlot)</c>
    ///         precisely so a page showing demo A cannot stage over a clip already taken from demo B.
    ///     </para>
    /// </summary>
    public string DemoPath { get; init; } = string.Empty;

    /// <inheritdoc cref="DemoPath" />
    public int PlayerSlot { get; init; }

    /// <summary>Verify exists at all — governed by <c>chrome.livesync</c>, not by session state.</summary>
    public bool VerifyPresent { get; }

    /// <summary>
    ///     Verify is offered only in LIVE mode. In a cached render the demo on this page is not the demo CS2
    ///     has loaded, and seeking the open demo to another demo's tick would play the wrong moment — the same
    ///     demo-identity rule the Highlights tab's per-row gate enforces, arriving here for free.
    /// </summary>
    public bool CanVerify { get; init; }

    public string RoundDisplay => RoundNumber > 0
        ? "r" + RoundNumber.ToString(CultureInfo.InvariantCulture)
        : MatchOverviewTabViewModel.Placeholder;

    public string TickDisplay => Tick.ToString("N0", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void Stage()
    {
        // Does NOT flip IsStaged — the handler does, from what the TRAY reports.
        //
        // While this was a step-5 stub it flipped optimistically, which is wrong twice over now that a real
        // tray is on the other end. It made the flag a lie whenever staging failed (the tray refuses a clip
        // whose cache row has gone, e.g. after a rescan), and worse, the handler reads IsStaged to decide
        // stage-vs-unstage — so a pre-flip turned every press into the opposite action and nothing could
        // ever be staged at all. With no handler wired the button is simply inert, which is the honest
        // rendering of "there is no tray to put this in".
        _onStage?.Invoke(this);
    }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (_onVerify is null || IsVerifying)
        {
            return;
        }

        IsVerifying = true;
        try
        {
            await _onVerify(this);
        }
        finally
        {
            IsVerifying = false;
        }
    }
}

/// <summary>
///     Projects a <see cref="DemoCacheRecord" />'s highlights into the page's per-player groups.
///     Kept out of the tab view-model so the join (highlight → roster slot → name/team) has one home and can
///     be tested without a view.
/// </summary>
public static class OverviewHighlightProjector
{
    /// <summary>
    ///     Groups <paramref name="record" />'s highlights by player, MOST MOMENTS FIRST and, within a player,
    ///     by tick.
    ///     <para>
    ///         Deliberately not the scoreboard's CT-block-then-T ordering: that one mirrors a scoreboard,
    ///         where side is the organising idea. This is a "what happened in this match" list, so it leads
    ///         with whoever produced the most — the same reason the scoreboard itself sorts strongest-first
    ///         within a side.
    ///     </para>
    ///     <para>
    ///         Players are resolved by SLOT against the record's roster; a highlight whose slot is not in the
    ///         roster still renders under a placeholder name rather than being dropped — losing a moment
    ///         because a roster row is missing would be worse than showing it unattributed.
    ///     </para>
    /// </summary>
    public static List<OverviewHighlightGroup> Project(
        DemoCacheRecord record,
        bool verifyPresent,
        bool canVerify,
        Action<OverviewHighlightRow>? onStage,
        Func<OverviewHighlightRow, Task>? onVerify)
    {
        ArgumentNullException.ThrowIfNull(record);

        Dictionary<int, CachedPlayerInfo> bySlot = [];
        foreach (CachedPlayerInfo p in record.Players)
        {
            bySlot.TryAdd(p.Slot, p);
        }

        List<OverviewHighlightGroup> groups = [];
        foreach (IGrouping<int, CachedHighlightEvent> group in record.Highlights
                     .GroupBy(h => h.PlayerSlot)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => bySlot.TryGetValue(g.Key, out CachedPlayerInfo? p)
                         ? DisplayText.Sanitize(p.Name)
                         : string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            bySlot.TryGetValue(group.Key, out CachedPlayerInfo? player);
            OverviewHighlightGroup vm = new(
                player is not null && player.Name.Length > 0
                    ? DisplayText.Sanitize(player.Name)
                    : MatchOverviewTabViewModel.Placeholder,
                player?.Team ?? 0);

            foreach (CachedHighlightEvent h in group.OrderBy(h => h.Tick))
            {
                vm.Highlights.Add(new OverviewHighlightRow(
                    DisplayText.Sanitize(h.RenderedTitle),
                    player?.Name ?? string.Empty,
                    h.Tick,
                    h.RoundNumber,
                    h.TypeKey,
                    verifyPresent,
                    onStage,
                    onVerify)
                {
                    CanVerify = canVerify,
                    DemoPath = record.Path,
                    PlayerSlot = h.PlayerSlot
                });
            }

            vm.NotifyRowsChanged();
            groups.Add(vm);
        }

        return groups;
    }
}
