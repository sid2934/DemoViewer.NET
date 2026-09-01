#region

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.ViewModels.MatchOverview;

/// <summary>
///     One roster entry on the Match Overview landing page: a player's name + bot flag, grouped by side.
/// </summary>
public sealed record OverviewPlayer(string Name, bool IsBot);

/// <summary>
///     One scoreboard line on the Match Overview landing page. Deliberately a handful of pre-formatted
///     strings rather than typed numbers: this is a read-only glance surface (the Stats tab owns sorting,
///     categories and export), and pre-formatting keeps the invariant "—" placeholder rule in ONE place.
/// </summary>
public sealed record OverviewStatRow(
    string Name,
    int Team,
    string Kills,
    string Deaths,
    string Assists,
    string Adr,
    string Rating)
{
    /// <summary>CT rows carry the blue side accent, T rows amber (the app-wide scoreboard convention).</summary>
    public bool IsCt => Team == 3;
}

/// <summary>
///     One step of the open pipeline as shown in the Match Overview progress strip. The three instances are
///     created ONCE in the tab view-model's constructor and only ever have their flags flipped. The strip
///     must never add or remove rows, because that is exactly the layout jump this page exists to avoid.
/// </summary>
public sealed partial class LoadStageViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isDone;

    public LoadStageViewModel(string label) => Label = label;

    /// <summary>Display label ("Parsing", "Enriching", "Analysing").</summary>
    public string Label { get; }
}

/// <summary>
///     The "Match Overview" landing tab. It is the FIRST thing a user sees when they open a demo, shown
///     immediately (before the heavy parse even starts) so a double-click has an instant, visible effect
///     instead of a silent multi-second wait.
///     <para>
///         <b>Skeleton-first contract.</b> Every section (identity, progress strip, quick facts, final score,
///         rosters, scoreboard, CTAs) exists in the visual tree from the FIRST rendered frame and only ever has
///         its <i>values</i> swapped placeholder → real. Nothing appears or disappears as the load progresses,
///         because a page that grows new cards mid-load reads as the UI misbehaving even when it is fast. That
///         is why the placeholders are <see cref="Placeholder" /> strings rather than <c>IsVisible</c> toggles,
///         and why the CTAs gate on <c>IsEnabled</c> rather than visibility.
///     </para>
///     <para>
///         <b>Fill order</b>: driven entirely by the shell (MainViewModel owns the load pipeline; this VM holds
///         no load logic): <see cref="BeginOpening" /> (file name + cheap header) → <see cref="SetSummary" />
///         (parsed: duration / tick rate / rosters) → <see cref="BeginAnalysis" /> → <see cref="SetAnalysis" />
///         (final score + scoreboard). <see cref="Fail" /> can interrupt at any point and deliberately KEEPS
///         whatever has already filled in: a demo that parsed but failed to analyse is still worth showing.
///     </para>
///     Consumer-facing, so every audience sees it.
/// </summary>
public sealed partial class MatchOverviewTabViewModel : ViewModelBase, IWorkspaceTabViewModel
{
    /// <summary>
    ///     Shown wherever a real value is not known YET (or turned out to be unavailable). A single shared
    ///     glyph is what makes the skeleton read as "this will fill in" rather than as blank/broken.
    /// </summary>
    public const string Placeholder = "—";

    // Stage ceilings for the creep below. Each stage eases TOWARD its ceiling and never reaches it, so
    // finishing the stage always produces visible forward motion rather than an anticlimax.
    private const double ParseCeiling = 0.45;
    private const double EnrichCeiling = 0.70;
    private const double AnalyseCeiling = 0.97;

    // ── Responsive body (two columns at >= 1000px) ────────────────────────────
    //
    // One Grid, never two stacked layouts (the Highlights master-detail precedent): the two column groups
    // bind Grid.Column / Grid.Row / Grid.ColumnSpan to view-model ints, so the wide layout puts them side by
    // side and the narrow one stacks them, with NO duplicated subtree to drift.

    /// <summary>Below this width the body stacks into one column.</summary>
    private const double TwoColumnBreakpoint = 1000;

    private readonly Action<string>? _computeFullStats;
    private readonly Func<string, string, string, int, int, bool>? _isClipStaged;

    private readonly Func<bool>? _isVerifyPresent;
    private readonly Action<string>? _openDemo;
    private readonly Action? _returnToLive;
    private readonly Func<string, string, string, int, int, bool>? _stageClip;
    private readonly Action<string, string, string, int, int>? _unstageClip;
    private readonly Func<int, string?, CancellationToken, Task<bool>>? _verifyMoment;
    private readonly Action? _viewPlayback;
    private readonly Action? _viewStats;
    private int _analysisRounds;
    private int? _analysisSideCt;
    private int? _analysisSideT;

    // Raw inputs to the reconciliation below. The authoritative score and the analysis run land in either
    // order (the score can come from an already-indexed library entry, well before analysis finishes), so
    // both are stored and the derived state is recomputed whenever either arrives.
    private int? _authCt;
    private int? _authT;

    // ── Completeness ──────────────────────────────────────────────────────────

    /// <summary>
    ///     How complete the data behind the page is. THE answer to "why is this section empty?": the page
    ///     names the tier it has instead of leaving the user to infer it from blank cards, and every state
    ///     carries the single action that advances it.
    /// </summary>
    [ObservableProperty]
    private OverviewCompleteness _completeness = OverviewCompleteness.None;

    [ObservableProperty]
    private ObservableCollection<OverviewPlayer> _counterTerrorists = [];

    // Drives the within-stage creep. Lazily created (always on the UI thread, from BeginOpening) and only
    // ever running while a stage is in flight.
    private DispatcherTimer? _creepTimer;

    /// <summary>Rounds won by the CT SIDE across the whole match (both teams' CT halves summed).</summary>
    [ObservableProperty]
    private string _ctSideWinsDisplay = Placeholder;

    /// <summary>Label for the left number: the clan name on a pro demo, else "ENDED CT".</summary>
    [ObservableProperty]
    private string _ctTeamLabel = "ENDED CT";

    // ── Final score ───────────────────────────────────────────────────────────
    // SOURCE: the analysis engine's per-team round wins (CTW + TW summed per team), which counts the rounds
    // each team actually won. NOT CCSTeam.m_iScore: that entity snapshot loses the winning team's final
    // round on a demo cut at the buzzer, so both pro demos here report a 12 that cannot be a completed
    // premier result. Counting round-win events survives that truncation.
    //
    // SEMANTICS: each number is a TEAM's total for the whole match, and a team is identified by its clan
    // name when the demo carries one (pro demos) or by the side it FINISHED on otherwise. They are NOT
    // per-side totals: sides swap at the half, so on the reference demo the team ending CT totalled 3
    // while the CT side won 15 of 16 rounds. The per-side split is separate, and suppressed unless it
    // reconciles against this score.
    [ObservableProperty]
    private string _ctTeamScoreDisplay = Placeholder;

    /// <summary>One-line damage summary for the banner ("3 string tables rejected — …").</summary>
    [ObservableProperty]
    private string _damageSummary = string.Empty;

    // ── Quick facts (placeholders until parsed) ──
    [ObservableProperty]
    private string _durationDisplay = Placeholder;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _failed;

    // ── Identity (shown ASAP) ──
    [ObservableProperty]
    private string _fileName = string.Empty;

    /// <summary>
    ///     True once the analysis stage has filled the rounds / score / scoreboard. Drives the view's
    ///     <c>.pending</c> styling: a still-unknown value is DIMMED rather than hidden, so the layout never
    ///     changes but a placeholder never masquerades as a real number either.
    /// </summary>
    [ObservableProperty]
    private bool _hasAnalysis;

    // ── Empty state ──
    [ObservableProperty]
    private bool _hasContent; // false → "open a demo" empty state

    /// <summary>
    ///     True once the roster carries a real team split. Distinct from <see cref="HasSummary" />: a MIGRATED
    ///     cache row has player NAMES from the old names-only list and no teams, so the facts are real while
    ///     the rosters are not. Without the split, the header counts must stay at the placeholder rather than
    ///     assert a confident zero.
    /// </summary>
    [ObservableProperty]
    private bool _hasRoster;

    /// <summary>True once the authoritative score has landed: the score plate's <c>.pending</c> gate.</summary>
    [ObservableProperty]
    private bool _hasScore;

    /// <summary>
    ///     True only when the per-side split RECONCILES with the authoritative score (they must cover the
    ///     same set of rounds). It is derived from the analysis CTW/TW columns, which are unreliable on
    ///     HLTV demos, so it hides itself rather than print a split that cannot be true.
    /// </summary>
    [ObservableProperty]
    private bool _hasSideSplit;

    /// <summary>True when the demo carries any spectator; gates the tile so it never shows a bare 0.</summary>
    [ObservableProperty]
    private bool _hasSpectators;

    // ── Rosters (filled once parsed) ──
    [ObservableProperty]
    private bool _hasSummary;

    /// <summary>Why the highlight section is empty, or blank when it is not.</summary>
    [ObservableProperty]
    private string _highlightsMessage = string.Empty;

    /// <summary>
    ///     True when the parse reported structured warnings (the S11 diagnostics channel, v0.6.0):
    ///     rejected string tables, dropped player blobs. Drives the "THIS DEMO MAY BE DAMAGED"
    ///     banner, additive like the sample-clip banner: the partial parse still renders, but a
    ///     placeholder-riddled page now explains itself. Set by the shell alongside
    ///     <c>SetSummary</c>; cleared by <c>ResetValues</c>.
    /// </summary>
    [ObservableProperty]
    private bool _isDamaged;

    // ── Load state ──
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isNarrow;

    /// <summary>
    ///     True when the open demo is the BUNDLED TOUR SAMPLE (a deliberate few-round trim of a real
    ///     match). Drives the identity hero's "sample clip" banner so its partial score reads as
    ///     by-design rather than as a scoring bug, the exact false alarm partial demos have caused
    ///     before. Set by the shell right after <see cref="BeginOpening" /> (path-compared against the
    ///     resolved sample), so it is stable for the whole load and never pops in mid-parse.
    /// </summary>
    [ObservableProperty]
    private bool _isSampleClip;

    /// <summary>
    ///     File name of the demo that is actually OPEN, when the page is previewing a different one. Set by
    ///     the shell; drives the hero band's "◀ Back to &lt;demo&gt;" affordance so a preview is never a
    ///     one-way trip away from the loaded demo.
    /// </summary>
    [ObservableProperty]
    private string? _liveDemoName;

    [ObservableProperty]
    private string _mapDisplay = string.Empty;

    // ── Mode ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Which job the page is doing. Set by <see cref="BeginOpening" /> (Live),
    ///     <see cref="SetCachedRecord" /> (Cached) and <see cref="Clear" /> (Empty).
    /// </summary>
    [ObservableProperty]
    private OverviewMode _mode = OverviewMode.Empty;

    [ObservableProperty]
    private string _playerCountDisplay = Placeholder;

    // ── Scoreboard (filled once analysed) ──
    [ObservableProperty]
    private ObservableCollection<OverviewStatRow> _playerStats = [];

    /// <summary>
    ///     The scoreboard section's third state. The section is always present, so it needs a real
    ///     "ran, produced nothing" message: a skeleton that never resolves reads worse than the layout jump
    ///     this page removes.
    /// </summary>
    [ObservableProperty]
    private string _playerStatsMessage = string.Empty;

    /// <summary>
    ///     0..1 coarse progress across the three open stages. Each stage is a black box, so this steps at
    ///     stage boundaries rather than sweeping smoothly. The stage strip carries the detail.
    /// </summary>
    [ObservableProperty]
    private double _progress;

    // ── Empty-slot messages (the partial-fill rule) ───────────────────────────
    //
    // THE RULE: a slot whose tier is missing shows ONE short sentence naming the tier, beside the one action
    // that fills it: never a grid of placeholders. A wall of "—" in a 268px scoreboard is indistinguishable
    // from "analysis is still running", which in a cached render is a lie: nothing is running and nothing
    // will arrive unless the user asks. The placeholder stays correct for a SINGLE value inside an otherwise
    // populated card, where the surrounding context already says the tier is there.

    /// <summary>
    ///     Why the roster cards are empty, or blank when they are not. Fills the reserved roster slot the way
    ///     <see cref="PlayerStatsMessage" /> fills the scoreboard's.
    /// </summary>
    [ObservableProperty]
    private string _rosterMessage = string.Empty;

    [ObservableProperty]
    private string _roundCountDisplay = Placeholder;

    [ObservableProperty]
    private string _serverName = string.Empty;

    /// <summary>
    ///     Named, non-proxy entries with no team assignment: observers, coaches, admins. Kept out
    ///     of <see cref="PlayerCountDisplay" /> so that number always equals the two rosters; shown
    ///     separately so the information is not simply lost.
    /// </summary>
    [ObservableProperty]
    private string _spectatorCountDisplay = "0";

    private double _stageCeiling;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Rounds won by the T SIDE across the whole match (both teams' T halves summed).</summary>
    [ObservableProperty]
    private string _tSideWinsDisplay = Placeholder;

    /// <summary>Label for the right number: the clan name on a pro demo, else "ENDED T".</summary>
    [ObservableProperty]
    private string _tTeamLabel = "ENDED T";

    [ObservableProperty]
    private string _tTeamScoreDisplay = Placeholder;

    [ObservableProperty]
    private ObservableCollection<OverviewPlayer> _terrorists = [];

    [ObservableProperty]
    private string _tickRateDisplay = Placeholder;

    /// <param name="viewStats">Switches to the Stats tab (the full scoreboard), the overview's CTA. May be null.</param>
    /// <param name="viewPlayback">Switches to the 2D Playback tab, the overview's CTA. May be null.</param>
    /// <param name="computeFullStats">
    ///     Enqueues the demo (by path) for a full analysis pass, the completeness chip's action. The shell
    ///     wires this to the global processing queue, which already serialises through <c>HeavyJobGate</c>,
    ///     already surfaces in the processing-queue status chip, and already fans one parse out to every
    ///     evaluator, so a single click fills the parse gaps, the scoreboard and the highlights together.
    ///     Null leaves the action absent rather than inert.
    /// </param>
    /// <param name="openDemo">Opens the demo (by path) for real, the cached render's primary CTA. May be null.</param>
    /// <param name="returnToLive">
    ///     Re-renders the demo that is actually open, after a preview. Owned by the SHELL, not by this
    ///     view-model: the live page is a projection of pipeline state this view-model cannot re-derive, so
    ///     restoring it means asking the shell to re-push (or to render its cache record). May be null.
    /// </param>
    /// <param name="verifyMoment">
    ///     Seeks the live CS2 to a highlight's moment. Same call the Highlights tab makes; null (or a browser
    ///     host, or Live Sync disabled) leaves the affordance absent.
    /// </param>
    /// <param name="isVerifyPresent">The <c>chrome.livesync</c> gate snapshot: whether Verify exists at all.</param>
    /// <param name="stageClip">
    ///     Adds a highlight to the Reels tray, returning true when it landed. Takes the clip's full
    ///     identity (demo path, ruleset, highlight id, tick, player slot) because a reel is CROSS-DEMO
    ///     and the tray keys on all five. Null leaves the <c>[ + ]</c> buttons inert.
    /// </param>
    /// <param name="unstageClip">Removes a staged clip by the same identity.</param>
    /// <param name="isClipStaged">
    ///     Whether a clip is already in the tray. Called once per row build, so it must be cheap: the tray
    ///     backs it with a dictionary lookup.
    /// </param>
    public MatchOverviewTabViewModel(
        Action? viewStats = null,
        Action? viewPlayback = null,
        Action<string>? computeFullStats = null,
        Action<string>? openDemo = null,
        Action? returnToLive = null,
        Func<int, string?, CancellationToken, Task<bool>>? verifyMoment = null,
        Func<bool>? isVerifyPresent = null,
        Func<string, string, string, int, int, bool>? stageClip = null,
        Action<string, string, string, int, int>? unstageClip = null,
        Func<string, string, string, int, int, bool>? isClipStaged = null)
    {
        _viewStats = viewStats;
        _viewPlayback = viewPlayback;
        _computeFullStats = computeFullStats;
        _openDemo = openDemo;
        _returnToLive = returnToLive;
        _verifyMoment = verifyMoment;
        _isVerifyPresent = isVerifyPresent;
        _stageClip = stageClip;
        _unstageClip = unstageClip;
        _isClipStaged = isClipStaged;
        Stages = [ParseStage, EnrichStage, AnalyseStage];
    }

    // ── Subject identity ──────────────────────────────────────────────────────
    //
    // THE INVARIANT THIS PAGE IS BUILT ON: it never paints data belonging to a demo other than its current
    // subject. Every fill below is keyed, and a push whose key does not match the subject is DROPPED.
    //
    // Why this is not paranoia: the fill setters are called from async continuations that outlive the open
    // they belong to (ResolveTeamNamesAsync, the analysis run), while this VM is a SINGLETON owned by the
    // shell. Close demo A mid-analysis and open demo B and A's late continuation lands on B's page. The
    // no-key form of this bug is already documented in SetAnalysis's own comment, which defends against one
    // instance of it by cross-checking arguments; a key defends against all of them by construction, and is
    // the precondition for the cached render, where previewing B while A is still loading is ROUTINE rather
    // than exceptional.
    //
    // The key is opaque to this VM: the shell passes the demo's local path where it has one and the file
    // name where it does not (browser host). It only ever has to be stable and comparable.

    /// <summary>
    ///     Identity of the demo this page is currently painting, or null in the empty state. Set by
    ///     <see cref="BeginOpening" /> (live) and compared by every other fill entry point.
    /// </summary>
    public string? SubjectKey { get; private set; }

    /// <summary>True in a cached render: drives the hero band's cached affordances.</summary>
    public bool IsCached => Mode == OverviewMode.Cached;

    /// <summary>True when a demo is genuinely open (or opening) rather than previewed.</summary>
    public bool IsLive => Mode == OverviewMode.Live;

    /// <summary>
    ///     Roster-header count for the CT card. A DISPLAY STRING, not the collection's <c>Count</c>: an empty
    ///     roster renders "0", and "0" is an assertion: it says this match had no Counter-Terrorists, which is
    ///     never true and is exactly the kind of confident-wrong number the rest of this page goes out of its
    ///     way to avoid. Before the roster lands (and in a cached render whose tier does not carry the team
    ///     split) the placeholder is the value that avoids asserting a false zero.
    /// </summary>
    public string CtRosterCountDisplay => HasRoster
        ? CounterTerrorists.Count.ToString(CultureInfo.InvariantCulture)
        : Placeholder;

    /// <summary>Roster-header count for the T card. See <see cref="CtRosterCountDisplay" />.</summary>
    public string TRosterCountDisplay => HasRoster
        ? Terrorists.Count.ToString(CultureInfo.InvariantCulture)
        : Placeholder;

    /// <summary>The per-player highlight groups for this demo. Empty when tier 3 is absent.</summary>
    public ObservableCollection<OverviewHighlightGroup> HighlightGroups { get; } = [];

    /// <summary>Section header count, e.g. "7 highlights".</summary>
    public string HighlightCountDisplay
    {
        get
        {
            int n = HighlightGroups.Sum(g => g.Highlights.Count);
            return n == 1 ? "1 highlight" : n.ToString(CultureInfo.InvariantCulture) + " highlights";
        }
    }

    /// <summary>Gates the section-header count. An explicit bool, never an int bound to IsVisible.</summary>
    public bool HasHighlights => HighlightGroups.Count > 0;

    /// <summary>
    ///     The highlight card's OWN action, separate from the completeness chip's.
    ///     <para>
    ///         They diverge in exactly one state and it is a common one: a finished live open has a real
    ///         scoreboard (so the chip has nothing to advance) while highlights are still un-indexed, because
    ///         they come from a different pass. Without a separate action the user would be told highlights
    ///         need indexing and given no way to do it.
    ///     </para>
    /// </summary>
    public string? HighlightsActionLabel => HasHighlights ? null : "Compute full stats";

    /// <summary>
    ///     Gates the highlight card's action button.
    ///     <para>
    ///         Never offered on a LIVE page: the open is already harvesting this demo's highlights, so the
    ///         button would sit under "Harvesting highlights…" contradicting it, and pressing it would queue a
    ///         second full pass: a whole redundant parse plus snapshot analysis, through a gate that allows
    ///         one heavy job machine-wide. Once the harvest lands the section speaks for itself; if the demo
    ///         genuinely fired nothing, re-running would produce the same nothing.
    ///     </para>
    /// </summary>
    public bool HasHighlightsAction =>
        !HasHighlights
        && HighlightsMessage.Length > 0
        && Mode != OverviewMode.Live
        && _computeFullStats is not null
        && SubjectKey is not null;

    /// <summary>Chip text: the state, in words. Colour is the redundant cue; this is the primary carrier.</summary>
    public string CompletenessLabel => Completeness switch
    {
        OverviewCompleteness.Live => IsLoading ? StatusText : "Ready",
        OverviewCompleteness.Full => "FULL",
        OverviewCompleteness.Indexed => "INDEXED · stats not computed",
        OverviewCompleteness.NotIndexed => "NOT INDEXED",
        OverviewCompleteness.Failed => Mode == OverviewMode.Live ? "FAILED" : "INDEX FAILED",
        _ => string.Empty
    };

    /// <summary>Label of the one action that advances the current state, or null when there is nothing to do.</summary>
    public string? CompletenessActionLabel => Completeness switch
    {
        OverviewCompleteness.Indexed => "Compute full stats",
        OverviewCompleteness.NotIndexed => "Index this demo",
        OverviewCompleteness.Failed when Mode == OverviewMode.Cached => "Retry",
        _ => null
    };

    /// <summary>Drives the action button; false in every state that has nothing to advance.</summary>
    public bool HasCompletenessAction =>
        CompletenessActionLabel is not null && _computeFullStats is not null && SubjectKey is not null;

    // Class-driving flags for the shared Ellipse.dot state classes. A bound class → token selector, never a
    // brush on the view-model: that is the theme mandate, and it is why this chip re-themes for free.
    public bool IsCompletenessWorking => Completeness == OverviewCompleteness.Live && IsLoading;

    /// <summary>
    ///     True for Full, Indexed AND a finished live open. INDEXED is deliberately included: it resolves the
    ///     same good token, and <see cref="IsCompletenessPartial" /> puts <c>.hollow</c> on the SAME element,
    ///     which turns the fill into a ring. One element, two classes: <c>Ellipse.dot.stateGood</c> is
    ///     declared <c>:not(.hollow)</c> exactly so the pair cannot collide.
    /// </summary>
    public bool IsCompletenessGood =>
        Completeness is OverviewCompleteness.Full or OverviewCompleteness.Indexed
        || Completeness == OverviewCompleteness.Live && !IsLoading;

    /// <summary>Indexed = a HOLLOW good ring: the established "partial / not the whole story" treatment.</summary>
    public bool IsCompletenessPartial => Completeness == OverviewCompleteness.Indexed;

    public bool IsCompletenessOff => Completeness == OverviewCompleteness.NotIndexed;
    public bool IsCompletenessError => Completeness == OverviewCompleteness.Failed;

    /// <summary>Left ("the match") column group: spans all three columns when narrow.</summary>
    public int MatchColumnSpan => IsNarrow ? 3 : 1;

    /// <summary>Right ("the moments") column group: column 2 when wide, column 0 row 1 when narrow.</summary>
    public int MomentsColumn => IsNarrow ? 0 : 2;

    public int MomentsRow => IsNarrow ? 1 : 0;
    public int MomentsColumnSpan => IsNarrow ? 3 : 1;

    /// <summary>The "◀ Back to …" affordance shows only while previewing with a different demo open.</summary>
    public bool CanReturnToLive =>
        Mode == OverviewMode.Cached
        && _returnToLive is not null
        && !string.IsNullOrEmpty(LiveDemoName);

    /// <summary>Stage 1: the heavy demo parse.</summary>
    public LoadStageViewModel ParseStage { get; } = new("Parsing");

    /// <summary>Stage 2: post-parse enrichment (roster, navigation index, game clock, module fan-out).</summary>
    public LoadStageViewModel EnrichStage { get; } = new("Enriching");

    /// <summary>Stage 3: the analysis engine run that produces the score and per-player stats.</summary>
    public LoadStageViewModel AnalyseStage { get; } = new("Analysing");

    /// <summary>The three open stages, in order. Fixed-length: never added to or removed from.</summary>
    public IReadOnlyList<LoadStageViewModel> Stages { get; }

    /// <summary>
    ///     Gates (enables, never hides) the full-stats CTA. Waits for ANALYSIS, not merely the parse: the
    ///     Stats tab is fed by the same evaluation run, so offering the jump earlier would land the user on an
    ///     empty scoreboard.
    /// </summary>
    /// <remarks>
    ///     Mode-gated as well: in a cached render the Stats tab holds a DIFFERENT demo (or none), so jumping
    ///     there from a preview would land the user on another match's scoreboard. The cached page offers
    ///     "Open this demo" instead: the precondition both explore CTAs actually depend on.
    /// </remarks>
    public bool CanExploreStats => Mode == OverviewMode.Live && HasAnalysis && _viewStats is not null;

    /// <summary>
    ///     Gates (enables, never hides) the 2D-playback CTA. Only needs the parse: playback runs off the
    ///     frame stream and does not wait for analysis.
    /// </summary>
    public bool CanExplorePlayback => Mode == OverviewMode.Live && HasSummary && _viewPlayback is not null;

    /// <summary>Gates the cached render's primary CTA: the one that turns a preview into a real open.</summary>
    public bool CanOpenDemo =>
        Mode == OverviewMode.Cached && _openDemo is not null && SubjectKey is not null;

    /// <summary>
    ///     A completed premier match ends with the winner on 13 (regulation), 15 (drawn OT) or 16 (OT win).
    ///     Anything else means either the demo stops early (legitimate, and common on buzzer-cut recordings)
    ///     or the derivation failed. True when the score satisfies a completed match, so callers/tests can
    ///     tell "this demo was cut short" from "our counting is broken".
    /// </summary>
    public bool ScoreLooksComplete =>
        _authCt is { } a && _authT is { } b && Math.Max(a, b) is 13 or 15 or 16;

    public void OnActivated(IModuleContext context)
    {
    }

    public void OnDeactivated()
    {
    }

    /// <summary>
    ///     True when <paramref name="subjectKey" /> names the demo this page is currently painting. A null
    ///     key never matches: an unkeyed caller is a caller that cannot prove which demo it is talking about,
    ///     and this page's whole contract is that it does not guess.
    /// </summary>
    public bool IsSubject(string? subjectKey) =>
        subjectKey is not null
        && SubjectKey is not null
        && string.Equals(subjectKey, SubjectKey, StringComparison.OrdinalIgnoreCase);

    // Every keyed fill runs through this. HasContent alone was the old guard; it cannot distinguish "a demo
    // is open" from "THIS demo is open".
    //
    // Mode matters as much as the key: a cached render is a page with NOTHING running behind it, so a live
    // pipeline push must not land on one even when the keys agree (the user previewed the very demo that is
    // also open). Accepting it would restart the stage strip under a page that says "cached".
    private bool Accepts(string? subjectKey) =>
        HasContent && Mode == OverviewMode.Live && IsSubject(subjectKey);

    partial void OnModeChanged(OverviewMode value)
    {
        OnPropertyChanged(nameof(IsCached));
        OnPropertyChanged(nameof(IsLive));
        RaiseCompletenessChanged();
        RaiseCtaChanged();
    }

    /// <summary>
    ///     Pushes the parse's health verdict onto the banner. Kept subject-keyed like the other
    ///     post-parse pushes so a stale load cannot stamp a newer page.
    ///     <para>
    ///         The banner gates on <see cref="ParseHealth.Damaged" />, NOT on having warnings at all.
    ///         Those are different questions: a demo recorded by a game build newer than the parser
    ///         drops net messages it has no case for and grades <see cref="ParseHealth.Degraded" />
    ///         while being a perfectly good recording. Gating on <c>Warnings.Count > 0</c> would fire
    ///         this banner on every such demo, which is to say, on every demo, every time CS2 ships
    ///         a build ahead of our parser. Only <c>Damaged</c> means the demo's OWN data failed to
    ///         decode, which is the case worth interrupting someone over.
    ///     </para>
    /// </summary>
    public void SetParseHealth(string? subjectKey, ParseHealth health, IReadOnlyList<ParseWarning> warnings)
    {
        if (!Accepts(subjectKey))
        {
            return;
        }

        if (health != ParseHealth.Damaged)
        {
            IsDamaged = false;
            DamageSummary = string.Empty;
            return;
        }

        int tables = warnings.Count(w => w.Code.StartsWith("string-table-", StringComparison.Ordinal));
        int players = warnings.Count(w => w.Code == ParseWarningCodes.PlayerInfoUnreadable);
        List<string> parts = [];
        if (tables > 0)
        {
            parts.Add($"{tables} string-table message(s) were rejected");
        }

        if (players > 0)
        {
            parts.Add($"{players} player record(s) were unreadable");
        }

        if (parts.Count == 0)
        {
            parts.Add($"{warnings.Count} parse warning(s)");
        }

        DamageSummary = string.Join(" and ", parts)
                        + " — player names, rosters, or events may be missing. The demo file may be damaged.";
        IsDamaged = true;
    }

    private void RaiseHighlightActionChanged()
    {
        OnPropertyChanged(nameof(HighlightsActionLabel));
        OnPropertyChanged(nameof(HasHighlightsAction));
    }

    partial void OnIsNarrowChanged(bool value)
    {
        OnPropertyChanged(nameof(MatchColumnSpan));
        OnPropertyChanged(nameof(MomentsColumn));
        OnPropertyChanged(nameof(MomentsRow));
        OnPropertyChanged(nameof(MomentsColumnSpan));
    }

    /// <summary>Sets the one/two-column layout from the tab's measured width (called by the view).</summary>
    public void SetViewportWidth(double width) => IsNarrow = width > 0 && width < TwoColumnBreakpoint;

    partial void OnLiveDemoNameChanged(string? value) => OnPropertyChanged(nameof(CanReturnToLive));

    /// <summary>
    ///     Step 1: called the instant an open begins, BEFORE the parse: shows the file name and whatever cheap
    ///     header info is already known (map / server may be empty and fill in later), and puts the page into
    ///     its loading state with every section present and placeholder-filled. This is what makes a
    ///     double-click feel responsive.
    /// </summary>
    /// <param name="fileName">The demo's file name, shown immediately, before anything is parsed.</param>
    /// <param name="mapDisplay">Prettified map name if the cheap header read produced one, else null.</param>
    /// <param name="serverName">Server name if the cheap header read produced one, else null.</param>
    /// <param name="subjectKey">
    ///     Identity of the demo being opened: the local path where there is one, else the file name. Becomes
    ///     this page's <see cref="SubjectKey" />; every subsequent fill must present it or be dropped.
    /// </param>
    public void BeginOpening(string fileName, string? mapDisplay, string? serverName, string? subjectKey)
    {
        ResetValues();
        Mode = OverviewMode.Live;
        SubjectKey = subjectKey ?? fileName;

        // The demo being opened BECOMES the live one, so any previous live name is stale from here. Cleared
        // by construction rather than left to every shell call site to remember: ResetValues deliberately
        // does not touch it (it is shell-owned), and a missed clear would have a later preview offering
        // "◀ Back to <the demo before last>". Only reachable via a mode check today, which is exactly the
        // kind of accidental safety that stops being true after one refactor.
        LiveDemoName = null;

        HasContent = true;
        FileName = fileName;
        MapDisplay = mapDisplay ?? string.Empty;
        ServerName = serverName ?? string.Empty;
        IsLoading = true;
        StatusText = "Opening demo…";
        Progress = 0.02;
        ParseStage.IsActive = true;
        Completeness = OverviewCompleteness.Live;
        StartCreep(ParseCeiling);
        RaiseCompletenessChanged();
        RaiseCtaChanged();
    }

    /// <summary>
    ///     Step 1b: paints everything this demo's CACHE RECORD already knows, into the page a live open has
    ///     just begun on. Called immediately after <see cref="BeginOpening" />; the pipeline's own fills then
    ///     land on top as they arrive.
    ///     <para>
    ///         <b>This is what makes the mode model pay for itself.</b> Both modes paint the same sections from
    ///         the same slots, so a demo the user was previewing a second ago does not blank itself the instant
    ///         they open it: the rosters, score and highlights simply stay on screen while the parse runs
    ///         behind them. Without this the page threw away everything it already knew and made the user
    ///         watch a multi-second skeleton rebuild facts that were sitting in a file on disk.
    ///     </para>
    ///     <para>
    ///         <b>Stays in <see cref="OverviewMode.Live" />.</b> It seeds VALUES, not mode: the stage strip
    ///         keeps running, <see cref="IsLoading" /> stays true, and completeness stays
    ///         <see cref="OverviewCompleteness.Live" /> because a pipeline genuinely is running. Routing this
    ///         through <see cref="SetCachedRecord" /> instead would flip the page to Cached and every
    ///         subsequent live fill (which all go through <c>Accepts</c>, and <c>Accepts</c> requires Live)
    ///         would be silently dropped for the rest of the load.
    ///     </para>
    /// </summary>
    /// <param name="subjectKey">The open's identity; a mismatch means this seed is for a previous open.</param>
    /// <param name="record">The demo's cache record, at whatever tier it has reached.</param>
    public void SeedFromCache(string? subjectKey, DemoCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!Accepts(subjectKey))
        {
            return;
        }

        // Identity, map and server are already set by BeginOpening from the cheap header read. The record
        // cannot improve on them and a stale record must not overwrite them.
        if (record.Parse.IsPresent)
        {
            if (record.DurationSeconds > 0)
            {
                DurationDisplay = FormatDuration(TimeSpan.FromSeconds(record.DurationSeconds));
            }

            if (record.TickRate > 0)
            {
                TickRateDisplay = record.TickRate.ToString(CultureInfo.InvariantCulture);
            }

            if (record.HasTeamSplit)
            {
                Terrorists.Clear();
                CounterTerrorists.Clear();
                foreach (CachedPlayerInfo p in record.Roster)
                {
                    OverviewPlayer entry = new(DisplayText.Sanitize(p.Name), p.IsBot);
                    if (p.Team == 3)
                    {
                        CounterTerrorists.Add(entry);
                    }
                    else
                    {
                        Terrorists.Add(entry);
                    }
                }

                int players = CounterTerrorists.Count + Terrorists.Count;
                if (players > 0)
                {
                    PlayerCountDisplay = players.ToString(CultureInfo.InvariantCulture);
                }

                int spectators = record.Spectators.Count(s => s.Name.Length > 0);
                SpectatorCountDisplay = spectators.ToString(CultureInfo.InvariantCulture);
                HasSpectators = spectators > 0;
                HasSummary = true;
                HasRoster = true;
                RosterMessage = string.Empty;
            }

            int rounds = record.Rounds.Count > 0 ? record.Rounds.Count : record.RoundCount;
            if (rounds > 0)
            {
                RoundCountDisplay = rounds.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (record.CtScore is not null && record.TScore is not null)
        {
            _authCt = record.CtScore;
            _authT = record.TScore;
            CtTeamScoreDisplay = FormatScore(record.CtScore);
            TTeamScoreDisplay = FormatScore(record.TScore);
            HasScore = true;
        }

        if (!string.IsNullOrWhiteSpace(record.CtClan))
        {
            CtTeamLabel = record.CtClan.ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(record.TClan))
        {
            TTeamLabel = record.TClan.ToUpperInvariant();
        }

        bool scannerRan = record.Analysis.IsPresent && record.AnalysisState == DemoAnalysisState.Indexed;
        if (record.Scoreboard.Count > 0)
        {
            _analysisSideCt = record.CtSideWins;
            _analysisSideT = record.TSideWins;
            _analysisRounds = record.AnalysisRoundCount;
            PlayerStats.Clear();
            FillCachedScoreboard(record);
            PlayerStatsMessage = string.Empty;
            HasAnalysis = PlayerStats.Count > 0;
        }

        ReconcileAnalysisAgainstScore();

        // Highlights are the one section a finished open CANNOT fill by itself: the interactive pipeline
        // stores them but never projects them back into the page, so the cached copy is the only source
        // this page will ever have for them.
        // Verify seeks the OPEN demo, and in this mode that is exactly what this demo is: the one case where
        // the affordance is genuinely live, as opposed to a cached render of some other match.
        FillCachedHighlights(record, scannerRan, true);

        RaiseCompletenessChanged();
        RaiseCtaChanged();
    }

    /// <summary>
    ///     Re-fills ONLY the highlight section from a record, leaving every other slot alone.
    ///     <para>
    ///         This is what completes the story for an OPEN demo. The open harvests highlights for free
    ///         (<c>HighlightScanService.OnOpenDemoEvaluated</c>) but does so off-thread, finishing after
    ///         <see cref="SetAnalysis" /> has already run, so the page had passed its last fill point before
    ///         the highlights existed, and the section the user opened the demo to look at stayed empty until
    ///         they navigated away and came back. Seeding at <see cref="SeedFromCache" /> cannot cover it
    ///         either: at that moment the harvest has not started.
    ///     </para>
    ///     <para>
    ///         Deliberately narrower than <see cref="SetCachedRecord" />, which resets every value and flips
    ///         the page to <see cref="OverviewMode.Cached" />; doing that to a live page would wipe the
    ///         pipeline's own fills and then silently drop everything it pushed afterwards.
    ///     </para>
    /// </summary>
    /// <param name="subjectKey">The demo this record belongs to; a mismatch is dropped.</param>
    /// <param name="record">The demo's cache record, freshly read.</param>
    public void RefreshHighlightsFromCache(string? subjectKey, DemoCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!HasContent || !IsSubject(subjectKey))
        {
            return;
        }

        bool scannerRan = record.Analysis.IsPresent && record.AnalysisState == DemoAnalysisState.Indexed;

        // A live page whose scan has not landed yet keeps whatever it is already saying ("Harvesting
        // highlights…"). The alternative is asserting "No highlights fired for this demo" about a harvest
        // that is still running: a statement that is both premature and frequently wrong, since this
        // fires on the SCOREBOARD write, which on an open always precedes the harvest completing.
        if (!scannerRan && Mode == OverviewMode.Live)
        {
            return;
        }

        // Verify is offered only while this demo is the OPEN one, which is exactly the live case.
        FillCachedHighlights(record, scannerRan, Mode == OverviewMode.Live);
    }

    /// <summary>
    ///     Free-form status nudge within the current stage. <paramref name="progress" /> only ever moves the
    ///     bar FORWARD: the creep may already have carried it past the caller's coarse figure, and yanking it
    ///     backwards is the one thing a progress bar must never do.
    /// </summary>
    public void SetStage(string? subjectKey, string status, double progress)
    {
        if (!Accepts(subjectKey))
        {
            return;
        }

        StatusText = status;
        Progress = Math.Max(Progress, Math.Clamp(progress, 0, 1));
    }

    /// <summary>
    ///     Step 2, the demo is parsed: fill the quick facts and rosters IN PLACE and move to the enrichment
    ///     stage. Note this does NOT leave the loading state: enrichment and analysis still follow, and the
    ///     score / scoreboard sections are still placeholders.
    /// </summary>
    public void SetSummary(string? subjectKey, ParsedDemo parsed)
    {
        if (!Accepts(subjectKey))
        {
            return;
        }

        MapDisplay = DemoEntry.PrettifyMap(parsed.MapName);
        ServerName = parsed.ServerName;
        DurationDisplay = FormatDuration(parsed.Duration);
        TickRateDisplay = parsed.TickRate > 0
            ? parsed.TickRate.ToString(CultureInfo.InvariantCulture)
            : Placeholder;

        Terrorists.Clear();
        CounterTerrorists.Clear();
        int players = 0;
        int spectators = 0;
        foreach (PlayerInfo p in parsed.Players.Values)
        {
            // The GOTV proxy / demo recorder occupies a userinfo slot with a name, so counting
            // every named entry reported 11 players for a 10-player match on every demo. It is
            // infrastructure, not a participant, and it never had a roster row either (team 0),
            // so excluding it is also what makes the count agree with the two rosters below it.
            if (p.Name.Length == 0 || p.IsHltv)
            {
                continue;
            }

            OverviewPlayer entry = new(p.Name, p.IsBot);

            // Team 2 = T, 3 = CT (parser convention). ONLY these count as players.
            //
            // Counting every named non-proxy entry was the older behaviour, and it broke the
            // invariant this card is built on: that the headline number equals the two rosters
            // printed beneath it. Matchmaking demos hid the bug because they carry no observers;
            // tournament GOTV routinely does. Four of the seven pro demos in this repo carry three
            // extra accounts each (an observer, a coach, an admin), so they would read "13" above
            // rosters of ten. Spectators are now counted separately instead of being silently
            // folded into a number that claims to describe the match.
            if (p.Team == 2)
            {
                Terrorists.Add(entry);
                players++;
            }
            else if (p.Team == 3)
            {
                CounterTerrorists.Add(entry);
                players++;
            }
            else
            {
                spectators++;
            }
        }

        PlayerCountDisplay = players > 0 ? players.ToString(CultureInfo.InvariantCulture) : Placeholder;
        SpectatorCountDisplay = spectators.ToString(CultureInfo.InvariantCulture);

        // Drives the tile's visibility: most demos have none, and a permanently-zero cell is noise.
        HasSpectators = spectators > 0;
        HasSummary = true;
        // A live parse always yields the team split, so the roster gate lands with the summary. (A cached
        // render is the case where the two can diverge; see SetCachedRecord.)
        HasRoster = true;
        RosterMessage = string.Empty;

        ParseStage.IsActive = false;
        ParseStage.IsDone = true;
        EnrichStage.IsActive = true;
        StatusText = "Preparing playback and navigation…";
        Progress = Math.Max(Progress, ParseCeiling);
        StartCreep(EnrichCeiling);
        RaiseCtaChanged();
    }

    /// <summary>Step 3: enrichment finished; the analysis engine is starting.</summary>
    public void BeginAnalysis(string? subjectKey)
    {
        if (!Accepts(subjectKey))
        {
            return;
        }

        EnrichStage.IsActive = false;
        EnrichStage.IsDone = true;
        AnalyseStage.IsActive = true;
        StatusText = "Analysing match…";
        Progress = Math.Max(Progress, EnrichCeiling);
        StartCreep(AnalyseCeiling);
    }

    /// <summary>
    ///     Step 4, analysis finished: fill the final score and the basic scoreboard from the analysis engine's
    ///     own per-player game table, so the numbers are the SAME ones the Stats tab shows (no second
    ///     projection to drift out of sync). A null table, or one with no rows, is the legitimate
    ///     "ran but produced nothing" outcome and surfaces as a message in place of the rows.
    /// </summary>
    /// <param name="subjectKey">Identity of the demo this run belongs to; a mismatch is dropped.</param>
    /// <param name="gameTable">The analysis per-player match table, or null when the run produced none.</param>
    /// <param name="teamScoresBySort">
    ///     Derived MATCH TOTALS per team, keyed by team-sort (0 = the team that finished CT, 1 = the team
    ///     that finished T). Not per-side totals; see the SideWins fields.
    /// </param>
    /// <param name="roundCount">Rounds the analysis resolved, or 0 when unknown.</param>
    public void SetAnalysis(
        string? subjectKey,
        MetricTable? gameTable,
        IReadOnlyDictionary<int, int?>? teamScoresBySort,
        int roundCount)
    {
        if (!Accepts(subjectKey))
        {
            return;
        }

        // EVERY analysis-stage value is keyed off the one fact "did this run produce a table", not off the
        // caller's other two arguments. A caller holding per-demo state that outlives an unload could
        // otherwise hand over the PREVIOUS demo's score dict alongside a null table, and the page would paint
        // the last match's 13–9 next to an empty scoreboard: the exact "two different scores for one match"
        // this page must never show. Cheap to enforce here; impossible to notice if it ever happens.
        //
        // The subject key above now rules out the cross-demo case this defends against, but the check stays:
        // it also covers a SAME-demo re-run that produced no table, which no key can distinguish.
        if (gameTable is not null)
        {
            _analysisRounds = roundCount;
            (_analysisSideCt, _analysisSideT) = ComputeSideWins(gameTable);
            ReconcileAnalysisAgainstScore();
        }

        PlayerStats.Clear();
        if (gameTable is not null)
        {
            // CT block then T block (the scoreboard convention), each strongest-first by rating so the glance
            // surface leads with who carried. Rating may be absent for a demo whose rules didn't produce it;
            // ordering then falls back to kills, which every ruleset has.
            IEnumerable<OverviewStatRow> rows = gameTable.Rows
                .Select(BuildStatRow)
                .Where(r => r.Name.Length > 0)
                .OrderBy(r => r.IsCt ? 0 : 1)
                .ThenByDescending(r => ParseSortable(r.Rating))
                .ThenByDescending(r => ParseSortable(r.Kills))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

            foreach (OverviewStatRow row in rows)
            {
                PlayerStats.Add(row);
            }
        }

        PlayerStatsMessage = PlayerStats.Count > 0
            ? string.Empty
            : "Analysis produced no per-player stats for this demo.";

        AnalyseStage.IsActive = false;
        AnalyseStage.IsDone = true;
        HasAnalysis = true;
        IsLoading = false;
        StopCreep();
        Progress = 1.0;
        StatusText = "Ready";

        // COMPLETENESS STAYS `Live`, NOT `Full`. That distinction is load-bearing.
        //
        // `Full` means "this demo's CACHE RECORD carries a current tier 3". A finished open is a different
        // fact: the interactive pipeline ran (StatsTab.GameTable), which fills the scoreboard but does NOT
        // harvest highlights: those come from the separate rules pass the cache stores
        // (RulesHighlightHarvester.RunBareAnalysis). Claiming `Full` here put a chip reading FULL above a
        // highlight card reading "needs a full analysis pass", on the same screen, about the same demo. A
        // page whose whole job is to state which tier it actually has must not contradict itself; `Live`
        // resolves to the "Ready" label, which is exactly what finished.
        // "Highlights are built by the analysis index, not by opening a demo" USED to be true here and is not
        // any more: the open harvests them for free (OnOpenDemoEvaluated), just off-thread and finishing
        // after this point. So an empty section at this instant means "not yet", and
        // RefreshHighlightsFromCache replaces this the moment the harvest lands: with the rows, with "no
        // highlights fired", or with the failure copy, depending on how it went.
        HighlightsMessage = HighlightGroups.Count > 0
            ? string.Empty
            : "Harvesting highlights…";
        OnPropertyChanged(nameof(HighlightCountDisplay));
        OnPropertyChanged(nameof(HasHighlights));
        RaiseHighlightActionChanged();
        RaiseCompletenessChanged();
        RaiseCtaChanged();
    }

    /// <summary>
    ///     Team identity for the score plate. The clans come from the demo (pro demos carry them); on
    ///     matchmaking demos they are blank and the labels fall back to the side each team finished on.
    ///     Separate from the score itself, which the analysis run supplies.
    /// </summary>
    public void SetTeamNames(string? subjectKey, string? ctClan, string? tClan)
    {
        // This one is reached from an async continuation (ResolveTeamNamesAsync) that routinely outlives the
        // open that started it, and it previously had NO guard at all, so a slow clan lookup for demo A
        // could relabel demo B's score plate with A's team names. The key closes that.
        if (!Accepts(subjectKey))
        {
            return;
        }

        CtTeamLabel = string.IsNullOrWhiteSpace(ctClan) ? "ENDED CT" : ctClan.ToUpperInvariant();
        TTeamLabel = string.IsNullOrWhiteSpace(tClan) ? "ENDED T" : tClan.ToUpperInvariant();
    }

    /// <summary>
    ///     Per-team round wins from the analysis engine, keyed by team-sort (0 = the team that finished CT,
    ///     1 = the team that finished T). Each value is that TEAM's total across both halves: rounds it won
    ///     regardless of which side it was on at the time, because the engine attributes each round win to
    ///     whoever was on the winning side at that moment and the halves accumulate into the same player rows.
    /// </summary>
    public void SetTeamScores(string? subjectKey, int? ctTeam, int? tTeam)
    {
        if (!Accepts(subjectKey))
        {
            return;
        }

        _authCt = ctTeam;
        _authT = tTeam;
        CtTeamScoreDisplay = FormatScore(ctTeam);
        TTeamScoreDisplay = FormatScore(tTeam);
        HasScore = ctTeam is not null && tTeam is not null;
        ReconcileAnalysisAgainstScore();
    }

    // The analysis-derived per-side split and round count are shown ONLY when they account for the same
    // rounds as the authoritative score. On HLTV demos the CTW/TW columns collapse (furia: a 0+1 "split"
    // against a real 2+12=14 match) and the round count drifts (vitality-vs-fut: 19 against an 18-round
    // score), so an unguarded display prints numbers that cannot both be true. Suppressing beats guessing.
    private void ReconcileAnalysisAgainstScore()
    {
        int? total = _authCt + _authT;

        bool splitOk = total is { } rounds
                       && _analysisSideCt is { } ct
                       && _analysisSideT is { } t
                       && ct + t == rounds;
        HasSideSplit = splitOk;
        CtSideWinsDisplay = splitOk ? FormatScore(_analysisSideCt) : Placeholder;
        TSideWinsDisplay = splitOk ? FormatScore(_analysisSideT) : Placeholder;

        RoundCountDisplay = total is { } r && _analysisRounds == r
            ? _analysisRounds.ToString(CultureInfo.InvariantCulture)
            : total is { } fallback && fallback > 0
                ? fallback.ToString(CultureInfo.InvariantCulture) // the score itself accounts for every round
                : Placeholder;
    }

    /// <summary>
    ///     The open failed. Deliberately KEEPS everything already filled in (a demo that parsed but failed to
    ///     analyse is still worth showing) and stops the stage strip where it stood.
    /// </summary>
    public void Fail(string? subjectKey, string error)
    {
        if (!Accepts(subjectKey))
        {
            return;
        }

        IsLoading = false;
        StopCreep();
        Failed = true;
        ErrorText = error;
        StatusText = HasSummary ? "Loaded with errors" : "Couldn't open this demo";

        foreach (LoadStageViewModel stage in Stages)
        {
            stage.IsActive = false;
        }

        // A stage that never ran must not claim a result: the scoreboard's message must say only what happened.
        if (!AnalyseStage.IsDone && PlayerStats.Count == 0)
        {
            PlayerStatsMessage = "Analysis did not complete for this demo.";
        }

        Completeness = OverviewCompleteness.Failed;
        RaiseCompletenessChanged();
        RaiseCtaChanged();
    }

    /// <summary>
    ///     Renders a demo entirely from its cache record: the page's SECOND job, and the one that makes it
    ///     "the primary per-demo page" rather than a landing page.
    ///     <para>
    ///         <b>Starts nothing.</b> No parse, no header read, no queue: the record is a small file the store
    ///         already read. The cached render's credibility rests on this page starting no work the user did
    ///         not ask for, which is also why the only thing that can advance a tier here is an explicit press
    ///         of the completeness chip's action.
    ///     </para>
    ///     <para>
    ///         <b>Deliberately not routed through <see cref="BeginOpening" />.</b> That entry point means "a
    ///         load is starting": it flips <see cref="IsLoading" />, activates the parse stage and starts the
    ///         creep timer, all of which would be a lie here. A cached page shows every stage PENDING with
    ///         <see cref="Progress" /> at zero, because nothing ran; the completeness chip carries the real
    ///         state. Marking the stages done instead would claim a pipeline that never executed.
    ///     </para>
    /// </summary>
    /// <param name="record">The demo's cache record, at whatever tier it has reached.</param>
    public void SetCachedRecord(DemoCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        ResetValues();
        Mode = OverviewMode.Cached;
        SubjectKey = record.Path;
        HasContent = true;
        FileName = Path.GetFileName(record.Path);
        MapDisplay = string.IsNullOrEmpty(record.Map) ? string.Empty : DemoEntry.PrettifyMap(record.Map);
        ServerName = record.Server ?? string.Empty;
        StatusText = string.Empty;

        bool hasParse = record.Parse.IsPresent;
        HasSummary = hasParse;
        HasRoster = hasParse && record.HasTeamSplit;

        if (hasParse)
        {
            DurationDisplay = record.DurationSeconds > 0
                ? FormatDuration(TimeSpan.FromSeconds(record.DurationSeconds))
                : Placeholder;
            TickRateDisplay = record.TickRate > 0
                ? record.TickRate.ToString(CultureInfo.InvariantCulture)
                : Placeholder;
        }

        // Rosters. A migrated legacy row has names with no team, so the split is absent while the names are
        // real. The message says exactly that instead of drawing two empty teams.
        if (HasRoster)
        {
            foreach (CachedPlayerInfo p in record.Roster)
            {
                OverviewPlayer entry = new(DisplayText.Sanitize(p.Name), p.IsBot);
                if (p.Team == 3)
                {
                    CounterTerrorists.Add(entry);
                }
                else
                {
                    Terrorists.Add(entry);
                }
            }

            int players = CounterTerrorists.Count + Terrorists.Count;
            PlayerCountDisplay = players > 0
                ? players.ToString(CultureInfo.InvariantCulture)
                : Placeholder;

            int spectators = record.Spectators.Count(s => s.Name.Length > 0);
            SpectatorCountDisplay = spectators.ToString(CultureInfo.InvariantCulture);
            HasSpectators = spectators > 0;
            RosterMessage = string.Empty;
        }
        else
        {
            RosterMessage = hasParse
                ? "Team split needs a re-index."
                : "Roster needs an index pass.";
            PlayerCountDisplay = record.Players.Count > 0
                ? record.Players.Count.ToString(CultureInfo.InvariantCulture)
                : Placeholder;
        }

        int rounds = record.Rounds.Count > 0 ? record.Rounds.Count : record.RoundCount;
        RoundCountDisplay = rounds > 0 ? rounds.ToString(CultureInfo.InvariantCulture) : Placeholder;

        // Score + clans are tier 2: an indexed demo shows a real final score with no analysis run at all.
        _authCt = record.CtScore;
        _authT = record.TScore;
        CtTeamScoreDisplay = FormatScore(record.CtScore);
        TTeamScoreDisplay = FormatScore(record.TScore);
        HasScore = record.CtScore is not null && record.TScore is not null;
        CtTeamLabel = string.IsNullOrWhiteSpace(record.CtClan) ? "ENDED CT" : record.CtClan.ToUpperInvariant();
        TTeamLabel = string.IsNullOrWhiteSpace(record.TClan) ? "ENDED T" : record.TClan.ToUpperInvariant();

        // TIER 3 IS TWO FACTS, NOT ONE. Conflating them made the page contradict itself.
        //
        // Highlights and the scoreboard have different producers. The highlights scan runs the rules engine in
        // BARE mode (AnalysisOptions.CaptureSnapshots = false) because that is what makes a library-wide sweep
        // affordable; the scoreboard is projected from the final snapshot vector, which bare mode does not
        // produce at all. So "this demo has been scanned" and "this demo has stats" are genuinely independent,
        // and a demo can legitimately sit with real highlights and no scoreboard for as long as the user never
        // asks for stats.
        //
        // Reading both off one flag meant a scanned demo rendered a FULL chip directly above "Analysis produced
        // no per-player stats for this demo." That's on the same screen, about the same demo. A page whose whole job
        // is to report which tier it actually holds must not do that.
        // Each half is read off ITS OWN evidence, not off a shared flag. AnalysisState belongs to the
        // highlights scan's lifecycle (the scanner owns it); the scoreboard's evidence is simply that rows
        // exist, because its producer is a different pass that never touches that field. Deriving the
        // scoreboard from AnalysisState would hide a real scoreboard on a demo that has only ever been
        // opened, and deriving highlights from row count would call a scan "done" before it had run.
        bool scannerRan = record.Analysis.IsPresent && record.AnalysisState == DemoAnalysisState.Indexed;
        bool hasScoreboard = record.Scoreboard.Count > 0;
        HasAnalysis = hasScoreboard;

        if (hasScoreboard)
        {
            _analysisSideCt = record.CtSideWins;
            _analysisSideT = record.TSideWins;
            _analysisRounds = record.AnalysisRoundCount;
            FillCachedScoreboard(record);
        }

        // Runs in BOTH branches: it is what derives RoundCountDisplay from the score when the analysis round
        // count is absent or disagrees, and it must not leave a stale side split behind either.
        ReconcileAnalysisAgainstScore();
        if (!hasScoreboard && rounds > 0)
        {
            // The reconcile prefers analysis-derived figures; with no analysis the parse's own round count is
            // the better answer than the score-sum fallback (a demo cut mid-match has more rounds than the
            // score accounts for).
            RoundCountDisplay = rounds.ToString(CultureInfo.InvariantCulture);
        }

        FillCachedHighlights(record, scannerRan);

        PlayerStatsMessage = hasScoreboard
            ? PlayerStats.Count > 0 ? string.Empty : "Analysis produced no per-player stats for this demo."
            : record.AnalysisState == DemoAnalysisState.Failed
                ? "The last analysis pass failed for this demo."
                : "Player stats need a full analysis pass.";

        Completeness = ClassifyCached(record);
        RaiseCompletenessChanged();
        RaiseCtaChanged();
    }

    // The scoreboard, joined against the roster by SLOT: the unified record stores stat rows by slot rather
    // than repeating a name on every row, so the name comes from the one place that owns it.
    private void FillCachedScoreboard(DemoCacheRecord record)
    {
        Dictionary<int, CachedPlayerInfo> bySlot = [];
        foreach (CachedPlayerInfo p in record.Players)
        {
            bySlot.TryAdd(p.Slot, p);
        }

        IEnumerable<OverviewStatRow> rows = record.Scoreboard
            .Select(r => new OverviewStatRow(
                bySlot.TryGetValue(r.Slot, out CachedPlayerInfo? p) ? DisplayText.Sanitize(p.Name) : string.Empty,
                r.Team,
                r.Kills.ToString(CultureInfo.InvariantCulture),
                r.Deaths.ToString(CultureInfo.InvariantCulture),
                r.Assists.ToString(CultureInfo.InvariantCulture),
                r.Adr.ToString("0.##", CultureInfo.InvariantCulture),
                r.Rating.ToString("0.##", CultureInfo.InvariantCulture)))
            .Where(r => r.Name.Length > 0)
            .OrderBy(r => r.IsCt ? 0 : 1)
            .ThenByDescending(r => ParseSortable(r.Rating))
            .ThenByDescending(r => ParseSortable(r.Kills))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

        foreach (OverviewStatRow row in rows)
        {
            PlayerStats.Add(row);
        }
    }

    private void FillCachedHighlights(DemoCacheRecord record, bool hasAnalysis, bool canVerify = false)
    {
        HighlightGroups.Clear();

        if (!hasAnalysis)
        {
            HighlightsMessage = record.AnalysisState == DemoAnalysisState.Failed
                ? "The last analysis pass failed for this demo."
                : "Highlights need a full analysis pass.";
        }
        else
        {
            foreach (OverviewHighlightGroup group in OverviewHighlightProjector.Project(
                         record,
                         _isVerifyPresent?.Invoke() ?? false,
                         // Verify seeks the OPEN demo; a cached page is by definition not it. The live seed
                         // (SeedFromCache) is the one caller that passes true.
                         canVerify,
                         _stageClip is null ? null : ToggleStage,
                         RunVerifyAsync))
            {
                // Seed each row from the tray. Without this, staging a clip, navigating away and coming back
                // shows a [ + ] on a clip already in the tray, and pressing it would toggle it OUT, which
                // reads as the button doing the opposite of what it says. Cheap by contract: the tray backs
                // this with a dictionary lookup.
                if (_isClipStaged is not null)
                {
                    foreach (OverviewHighlightRow row in group.Highlights)
                    {
                        (string rulesetId, string highlightId) = SplitTypeKey(row.TypeKey);
                        row.IsStaged = _isClipStaged(
                            row.DemoPath, rulesetId, highlightId, row.Tick, row.PlayerSlot);
                    }
                }

                HighlightGroups.Add(group);
            }

            HighlightsMessage = HighlightGroups.Count > 0
                ? string.Empty
                : "No highlights fired for this demo.";
        }

        OnPropertyChanged(nameof(HighlightCountDisplay));
        OnPropertyChanged(nameof(HasHighlights));
        RaiseHighlightActionChanged();
    }

    // FULL requires the SCOREBOARD, not merely a stamped analysis tier: the chip sits above a body whose
    // stats half would still be reading "needs a full analysis pass" on a highlights-only record, and the
    // chip's own action IS "Compute full stats". Claiming Full there would both contradict the section
    // beneath it and retire the one button that fixes it. See the two-facts note in SetCachedRecord.
    private static OverviewCompleteness ClassifyCached(DemoCacheRecord record) =>
        record.AnalysisState == DemoAnalysisState.Failed
            ? OverviewCompleteness.Failed
            : record.Analysis.IsPresent
              && record.AnalysisState == DemoAnalysisState.Indexed
              && record.Scoreboard.Count > 0
                ? OverviewCompleteness.Full
                : record.Parse.IsPresent
                    ? OverviewCompleteness.Indexed
                    : OverviewCompleteness.NotIndexed;

    // [ + ] / [ ✓ ]: the Match Overview end of the cross-demo clip tray. A single toggle rather than two
    // buttons: the row already renders its staged state, so the second press is unmistakably "undo that".
    //
    // IsStaged is set from what the tray REPORTS, not optimistically: StageFromCache resolves the cache row
    // itself and returns false when the demo or the highlight is no longer cached (a rescan can drop a
    // highlight the page is still showing). Assuming success would leave a ✓ on a clip that is not in the
    // tray, and the tray is the thing that actually renders.
    private void ToggleStage(OverviewHighlightRow row)
    {
        (string rulesetId, string highlightId) = SplitTypeKey(row.TypeKey);

        if (row.IsStaged)
        {
            _unstageClip?.Invoke(row.DemoPath, rulesetId, highlightId, row.Tick, row.PlayerSlot);
            row.IsStaged = false;
            return;
        }

        row.IsStaged =
            _stageClip?.Invoke(row.DemoPath, rulesetId, highlightId, row.Tick, row.PlayerSlot) ?? false;
    }

    // TypeKey is "{rulesetId}.{highlightId}". Split on the FIRST dot: a ruleset id is a single segment while
    // a highlight id may itself contain dots, so LastIndexOf would silently mis-attribute those.
    private static (string RulesetId, string HighlightId) SplitTypeKey(string typeKey)
    {
        int dot = typeKey.IndexOf('.', StringComparison.Ordinal);
        return dot < 0
            ? (typeKey, string.Empty)
            : (typeKey[..dot], typeKey[(dot + 1)..]);
    }

    private async Task RunVerifyAsync(OverviewHighlightRow row)
    {
        if (_verifyMoment is null)
        {
            return;
        }

        // Tick is frame clock and passed AS-IS; spectate by the RAW in-demo name (CSVG's currency).
        await _verifyMoment(row.Tick, row.RawPlayerName, CancellationToken.None);
    }

    /// <summary>Resets to the empty "open a demo" state (a demo was closed / none is open).</summary>
    public void Clear()
    {
        ResetValues();
        Mode = OverviewMode.Empty;
        // Dropping the subject is what makes every in-flight continuation for the closed demo a no-op:
        // Accepts() fails on a null SubjectKey, so nothing can repaint a page that no longer has a demo.
        SubjectKey = null;
        HasContent = false;
        FileName = string.Empty;
        MapDisplay = string.Empty;
        ServerName = string.Empty;
        StatusText = string.Empty;
        RaiseCtaChanged();
    }

    // The roster header counts are derived from HasRoster (see CtRosterCountDisplay), so they have to be
    // re-raised whenever it flips, in both directions: the roster fill sets it, ResetValues takes it back
    // to the placeholder.
    partial void OnHasRosterChanged(bool value)
    {
        OnPropertyChanged(nameof(CtRosterCountDisplay));
        OnPropertyChanged(nameof(TRosterCountDisplay));
    }

    // The chip's label follows the live status line while a load is running, and its dot follows IsLoading.
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(CompletenessLabel));

    partial void OnIsLoadingChanged(bool value) => RaiseCompletenessChanged();

    partial void OnCompletenessChanged(OverviewCompleteness value) => RaiseCompletenessChanged();

    private void RaiseCompletenessChanged()
    {
        OnPropertyChanged(nameof(CompletenessLabel));
        OnPropertyChanged(nameof(CompletenessActionLabel));
        OnPropertyChanged(nameof(HasCompletenessAction));
        OnPropertyChanged(nameof(IsCompletenessWorking));
        OnPropertyChanged(nameof(IsCompletenessGood));
        OnPropertyChanged(nameof(IsCompletenessPartial));
        OnPropertyChanged(nameof(IsCompletenessOff));
        OnPropertyChanged(nameof(IsCompletenessError));
        OnPropertyChanged(nameof(CanReturnToLive));
    }

    [RelayCommand]
    private void ViewStats() => _viewStats?.Invoke();

    [RelayCommand]
    private void ViewPlayback() => _viewPlayback?.Invoke();

    /// <summary>
    ///     The completeness chip's action: enqueue this demo for the pass that fills the tier it is missing.
    ///     Deliberately does NOT open the demo: computing and opening are different intents, and conflating
    ///     them would make a glance at the cache cost a full load.
    /// </summary>
    [RelayCommand]
    private void ComputeFullStats()
    {
        if (SubjectKey is { } key)
        {
            _computeFullStats?.Invoke(key);
        }
    }

    /// <summary>Turns the cached preview into a real open (the shell's normal load funnel).</summary>
    [RelayCommand]
    private void OpenDemo()
    {
        if (SubjectKey is { } key)
        {
            _openDemo?.Invoke(key);
        }
    }

    /// <summary>Returns to the demo that is actually open, after a preview.</summary>
    [RelayCommand]
    private void ReturnToLive() => _returnToLive?.Invoke();

    // Every per-demo value back to its placeholder, and every stage back to pending. Shared by the two
    // entry points into a clean state (a new open, and closing the demo) so neither can drift.
    private void ResetValues()
    {
        StopCreep();
        Failed = false;
        ErrorText = string.Empty;
        IsSampleClip = false;
        IsDamaged = false;
        DamageSummary = string.Empty;
        HasSummary = false;
        HasRoster = false;
        HasAnalysis = false;
        IsLoading = false;
        Progress = 0;
        Completeness = OverviewCompleteness.None;
        RosterMessage = string.Empty;
        HighlightsMessage = string.Empty;
        HighlightGroups.Clear();
        OnPropertyChanged(nameof(HighlightCountDisplay));
        OnPropertyChanged(nameof(HasHighlights));

        DurationDisplay = Placeholder;
        TickRateDisplay = Placeholder;
        PlayerCountDisplay = Placeholder;
        SpectatorCountDisplay = "0";
        HasSpectators = false;
        RoundCountDisplay = Placeholder;
        CtTeamScoreDisplay = Placeholder;
        TTeamScoreDisplay = Placeholder;
        CtTeamLabel = "ENDED CT";
        TTeamLabel = "ENDED T";
        HasScore = false;
        CtSideWinsDisplay = Placeholder;
        TSideWinsDisplay = Placeholder;
        HasSideSplit = false;
        _authCt = null;
        _authT = null;
        _analysisSideCt = null;
        _analysisSideT = null;
        _analysisRounds = 0;

        Terrorists.Clear();
        CounterTerrorists.Clear();
        PlayerStats.Clear();
        PlayerStatsMessage = string.Empty;

        foreach (LoadStageViewModel stage in Stages)
        {
            stage.IsActive = false;
            stage.IsDone = false;
        }
    }

    // ── Within-stage creep ────────────────────────────────────────────────────
    // Each of the three stages is a black box: the parser reports no progress, and the parse alone is
    // several seconds. Without this the bar sits perfectly still for most of the load and then lurches,
    // which reads as a hang. So while a stage is in flight the bar eases ASYMPTOTICALLY toward that stage's
    // ceiling and never arrives: motion means "still working", and the real stage completion is what
    // actually advances it past the ceiling.
    //
    // Be clear-eyed about what this is: the motion is TIME-BASED, not a measurement. The stage boundaries
    // and the three-step strip beside the bar are what reflect real progress. The
    // creep never overtakes its ceiling, so it cannot claim a stage is nearly done when it has just begun.
    private void StartCreep(double ceiling)
    {
        _stageCeiling = ceiling;

        // Created lazily rather than in the ctor: BeginOpening always runs on the UI thread, whereas the VM
        // itself is constructed during shell composition.
        _creepTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background,
            (_, _) => Creep());
        _creepTimer.Start();
    }

    private void StopCreep() => _creepTimer?.Stop();

    private void Creep()
    {
        if (!IsLoading || Progress >= _stageCeiling)
        {
            return;
        }

        // Exponential ease: fast at first, slowing as it nears the ceiling. Motion stays perceptible for a
        // long time without the bar ever looking like it is about to finish.
        Progress = Math.Min(_stageCeiling, Progress + (_stageCeiling - Progress) * 0.055);
    }

    private void RaiseCtaChanged()
    {
        OnPropertyChanged(nameof(CanExploreStats));
        OnPropertyChanged(nameof(CanExplorePlayback));
    }

    private static OverviewStatRow BuildStatRow(MetricRow row) =>
        new(
            row.Dimensions.GetValueOrDefault("player_name")?.ToString() ?? string.Empty,
            row.Dimensions.GetValueOrDefault("team") is { } t
                ? Convert.ToInt32(t, CultureInfo.InvariantCulture)
                : 0,
            Format(row.Values.GetValueOrDefault("TotalK")),
            Format(row.Values.GetValueOrDefault("TotalD")),
            Format(row.Values.GetValueOrDefault("TotalA")),
            Format(row.Values.GetValueOrDefault("ADR")),
            Format(row.Values.GetValueOrDefault("HLTV")));

    // Same rendering contract as the Stats tab's StatCell.Display (invariant, doubles to 2 decimals), with
    // the page-wide placeholder standing in for a missing value instead of a blank cell.
    private static string Format(object? raw) => raw switch
    {
        null => Placeholder,
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        float f => f.ToString("0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) is { Length: > 0 } s ? s : Placeholder
    };

    /// <summary>
    ///     Rounds won by each SIDE across the whole match: genuinely per-side, unlike the per-team totals.
    ///     Every row of a team carries that team's own <c>CTW</c> / <c>TW</c> (rounds it won while CT / while
    ///     T), so summing ONE row per team gives the match-wide side split. Returns nulls when the columns
    ///     are absent (a ruleset that doesn't produce them) or a team's rows disagree; a missing number
    ///     beats a wrong one.
    /// </summary>
    private static (int? Ct, int? T) ComputeSideWins(MetricTable gameTable)
    {
        Dictionary<int, (int Ct, int T)> perTeam = new();
        foreach (MetricRow row in gameTable.Rows)
        {
            if (row.Values.GetValueOrDefault("CTW") is not { } ctw
                || row.Values.GetValueOrDefault("TW") is not { } tw
                || row.Dimensions.GetValueOrDefault("team") is not { } teamValue)
            {
                continue;
            }

            int team = Convert.ToInt32(teamValue, CultureInfo.InvariantCulture);
            (int Ct, int T) wins = (
                Convert.ToInt32(ctw, CultureInfo.InvariantCulture),
                Convert.ToInt32(tw, CultureInfo.InvariantCulture));

            if (perTeam.TryGetValue(team, out (int Ct, int T) seen))
            {
                if (seen != wins)
                {
                    return (null, null); // teammates disagree, so the whole split is untrustworthy
                }
            }
            else
            {
                perTeam[team] = wins;
            }
        }

        return perTeam.Count == 0
            ? (null, null)
            : (perTeam.Values.Sum(w => w.Ct), perTeam.Values.Sum(w => w.T));
    }

    private static string FormatScore(int? score) =>
        score is { } s ? s.ToString(CultureInfo.InvariantCulture) : Placeholder;

    // Ordering key for an already-formatted cell: placeholders sort last.
    private static double ParseSortable(string display) =>
        double.TryParse(display, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : double.MinValue;

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes}:{d.Seconds:D2}";
}
