#region

using System.ComponentModel;
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Output;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>The player-details sub-sections.</summary>
public enum DetailSection
{
    Overview,
    Rounds,
    Vision
}

/// <summary>
///     The player-details dashboard VM: every panel is a pure
///     projection of the tables the parent <see cref="StatsTabViewModel" /> already holds, filtered
///     to one <c>player_slot</c>. All visual geometry (sparkline points, bar heights/widths) is
///     computed HERE and bound as data: the view draws, it never measures. Per-player
///     data is tiny (≤~30 rounds, ≤~10 weapons), so a full rebuild on slot switch is free.
/// </summary>
public sealed partial class PlayerDetailsViewModel : ObservableObject
{
    private const double KvTrackWidth = 150;

    /// <summary>Core-strip tile keys in display order; HLTV is the hero tile.</summary>
    private static readonly (string Key, bool Hero)[] _coreTileKeys =
    [
        ("HLTV", true), ("TotalK", false), ("TotalD", false), ("TotalA", false), ("ADR", false),
        ("KAST%", false), ("KD", false), ("HS%", false), ("Surv%", false)
    ];

    /// <summary>
    ///     Rounds-table column order (design P-10). <c>__opn__</c> is the synthetic ▲/▼ opening-duel
    ///     glyph column derived from FK/FD.
    /// </summary>
    private static readonly string[] _roundDetailKeys =
    [
        "Kills", "Deaths", "Assists", "Damage", "HasKAST", "HSKills", "__opn__",
        "Flashed", "EKills", "Traded", "UtilDmg", "DeagleHS"
    ];

    // ── Section sub-rail ──────────────────────────────────────────────────────

    /// <summary>The active sub-section; persists across player switches.</summary>
    [ObservableProperty]
    private DetailSection _section = DetailSection.Overview;

    /// <summary>The highlighted round (form-card deep-link target); 0 = none.</summary>
    [ObservableProperty]
    private int _selectedDetailRound;

    /// <summary>The dropdown selection; changing it re-targets the dashboard.</summary>
    [ObservableProperty]
    private PlayerRef? _selectedPlayer;

    private bool _syncingPlayer;

    /// <summary>Builds the dashboard for <paramref name="slot" /> over the parent's tables.</summary>
    public PlayerDetailsViewModel(StatsTabViewModel parent, int slot)
    {
        ArgumentNullException.ThrowIfNull(parent);
        Parent = parent;
        Weapons = new WeaponBreakdownViewModel(parent);
        Vision = new VisionViewModel(parent);
        Parent.PropertyChanged += OnParentPropertyChanged;
        SetSlot(slot);
    }

    /// <summary>The owning Stats tab: close command, visibility compute/busy, table access.</summary>
    public StatsTabViewModel Parent { get; }

    /// <summary>The open player's slot (the join key into every table).</summary>
    public int PlayerSlot { get; private set; } = -1;

    /// <summary>True when the Overview sub-section is active.</summary>
    public bool IsOverview => Section == DetailSection.Overview;

    /// <summary>True when the Rounds sub-section is active.</summary>
    public bool IsRounds => Section == DetailSection.Rounds;

    /// <summary>True when the Vision sub-section is active.</summary>
    public bool IsVision => Section == DetailSection.Vision;

    // ── Player switching (header ◄ dropdown ►) ────────────────────────────────

    /// <summary>All players in the match, CT then T (from the parent's game table).</summary>
    public IReadOnlyList<PlayerRef> Players => Parent.DetailPlayers;

    // ── P-1/P-2 identity header + core strip ──────────────────────────────────

    /// <summary>The player's display name.</summary>
    public string PlayerName { get; private set; } = "";

    /// <summary>Side tag ("CT" / "T").</summary>
    public string TeamLabel { get; private set; } = "";

    /// <summary>Side-color hook (CT blue / T amber).</summary>
    public bool IsCt { get; private set; }

    /// <summary>Map name (quiet subtitle).</summary>
    public string MapName { get; private set; } = "";

    /// <summary>"CT · de_dust2" subtitle.</summary>
    public string SubtitleText => TeamLabel.Length > 0 ? $"{TeamLabel} · {MapName}" : MapName;

    /// <summary>False when the slot has no <c>player_game_stats</c> row (empty-state header).</summary>
    public bool HasGameRow { get; private set; }

    /// <summary>Core stat tiles (Rating hero + K/D/A/ADR/KAST%/K/D ratio/HS%/Surv%).</summary>
    public IReadOnlyList<StatTileItem> CoreTiles { get; private set; } = [];

    // ── P-3 form timeline ─────────────────────────────────────────────────────

    /// <summary>Per-round form geometry (kills sparkline, damage bars, KAST dots, duel ticks).</summary>
    public FormTimelineViewModel Form { get; private set; } = FormTimelineViewModel.Empty;

    // ── P-4 weapon breakdown ──────────────────────────────────────────────────

    /// <summary>Per-weapon share bars with the kills/damage metric toggle.</summary>
    public WeaponBreakdownViewModel Weapons { get; }

    // ── P-5 achievements ──────────────────────────────────────────────────────

    /// <summary>This player's chain satisfactions, in tick order.</summary>
    public IReadOnlyList<AchievementItem> Achievements { get; private set; } = [];

    /// <summary>True when the player has at least one recorded achievement.</summary>
    public bool HasAchievements => Achievements.Count > 0;

    /// <summary>Card header with the count.</summary>
    public string AchievementsHeader => $"Achievements ({Achievements.Count})";

    // ── P-6 opening duels ─────────────────────────────────────────────────────

    /// <summary>Duel-win gauge fill width in px (Duel% of the track).</summary>
    public double DuelGaugeWidth { get; private set; }

    /// <summary>Duel-win percentage text.</summary>
    public string DuelPercentText { get; private set; } = "";

    /// <summary>Opening K / Opening D / +/- and the CT/T split rows.</summary>
    public IReadOnlyList<KeyValueItem> DuelItems { get; private set; } = [];

    // ── P-7 clutch & multi-kills ──────────────────────────────────────────────

    /// <summary>Clutches / rapid kills / revenge rows.</summary>
    public IReadOnlyList<KeyValueItem> ClutchItems { get; private set; } = [];

    /// <summary>2K/3K/4K/Ace histogram bars (height ∝ count).</summary>
    public IReadOnlyList<HistBarItem> MultiKillBars { get; private set; } = [];

    // ── P-8 utility ───────────────────────────────────────────────────────────

    /// <summary>Utility usage rows (flashes, HE, smokes, molotovs, flash assists, avg blind).</summary>
    public IReadOnlyList<KeyValueItem> UtilityItems { get; private set; } = [];

    // ── P-9 damage & accuracy ─────────────────────────────────────────────────

    /// <summary>Damage/accuracy rows; Accuracy is VM-derived (HitFoe/Shots), plainly a display value.</summary>
    public IReadOnlyList<KeyValueItem> DamageItems { get; private set; } = [];

    // ── P-10 round-by-round table ─────────────────────────────────────────────

    /// <summary>Rounds-table column headers (catalogue display names + the synthetic Opn column).</summary>
    public IReadOnlyList<DetailRoundColumn> RoundTableColumns { get; private set; } = [];

    /// <summary>One row per live round for this player.</summary>
    public IReadOnlyList<DetailRoundRow> RoundTableRows { get; private set; } = [];

    /// <summary>True when the player has any live-round rows.</summary>
    public bool HasRoundRows => RoundTableRows.Count > 0;

    // ── P-11/P-12 vision ──────────────────────────────────────────────────────

    /// <summary>Visibility summary + per-opponent matrix (with the two empty-state gates).</summary>
    public VisionViewModel Vision { get; }

    /// <summary>Unhooks the parent-change subscription (called when the overlay closes).</summary>
    public void Detach() => Parent.PropertyChanged -= OnParentPropertyChanged;

    private void OnParentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Visibility computed (or reset) while the overlay is open → the Vision panel re-reads the
        // now-(un)populated tables (state retention).
        if (e.PropertyName is nameof(StatsTabViewModel.HasVisibilityStats))
        {
            Vision.Refresh(PlayerSlot, MapName);
        }
    }

    partial void OnSectionChanged(DetailSection value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsRounds));
        OnPropertyChanged(nameof(IsVision));
    }

    /// <summary>Activates the Overview sub-section.</summary>
    [RelayCommand]
    private void ShowOverview() => Section = DetailSection.Overview;

    /// <summary>Activates the Rounds sub-section.</summary>
    [RelayCommand]
    private void ShowRounds() => Section = DetailSection.Rounds;

    /// <summary>Activates the Vision sub-section.</summary>
    [RelayCommand]
    private void ShowVision() => Section = DetailSection.Vision;

    partial void OnSelectedPlayerChanged(PlayerRef? value)
    {
        if (!_syncingPlayer && value is not null && value.Slot != PlayerSlot)
        {
            SetSlot(value.Slot);
        }
    }

    /// <summary>Steps to the previous player (wraps).</summary>
    [RelayCommand]
    private void PrevPlayer() => StepPlayer(-1);

    /// <summary>Steps to the next player (wraps).</summary>
    [RelayCommand]
    private void NextPlayer() => StepPlayer(+1);

    private void StepPlayer(int delta)
    {
        IReadOnlyList<PlayerRef> players = Players;
        if (players.Count == 0)
        {
            return;
        }

        int idx = 0;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Slot == PlayerSlot)
            {
                idx = i;
                break;
            }
        }

        SetSlot(players[(idx + delta + players.Count) % players.Count].Slot);
    }

    /// <summary>
    ///     Re-targets every panel to <paramref name="slot" />, keeping section / selected round /
    ///     weapon-metric toggle (state retention).
    /// </summary>
    public void SetSlot(int slot)
    {
        PlayerSlot = slot;
        _syncingPlayer = true;
        SelectedPlayer = Players.FirstOrDefault(p => p.Slot == slot);
        _syncingPlayer = false;
        RebuildAll();
    }

    partial void OnSelectedDetailRoundChanged(int value) => RebuildRoundsTable();

    /// <summary>Form-card deep-link: jump to the Rounds sub-section with a round highlighted.</summary>
    public void SelectRoundFromForm(int round)
    {
        SelectedDetailRound = round;
        Section = DetailSection.Rounds;
    }

    // ── Rebuild ───────────────────────────────────────────────────────────────

    private void RebuildAll()
    {
        MetricTable? gameTable = Parent.GameTable;
        MetricRow? gameRow = gameTable?.Rows.FirstOrDefault(r => StatsTabViewModel.RowSlot(r) == PlayerSlot);
        IReadOnlyList<string> gameColumns = gameTable?.ValueColumns ?? [];

        RebuildIdentity(gameRow);
        RebuildForm();
        Weapons.SetSlot(PlayerSlot);
        RebuildAchievements();
        RebuildDuels(gameRow, gameColumns);
        RebuildClutch(gameRow, gameColumns);
        UtilityItems = BuildKvItems(gameRow, gameColumns,
            ["Flash", "EFlash", "HE", "Smokes", "Molly", "FlashAst", "AvgBlind"]);
        OnPropertyChanged(nameof(UtilityItems));
        RebuildDamage(gameRow, gameColumns);
        RebuildRoundsTable();
        Vision.Refresh(PlayerSlot, MapName);
    }

    private void RebuildIdentity(MetricRow? gameRow)
    {
        HasGameRow = gameRow is not null;
        PlayerName = gameRow?.Dimensions.GetValueOrDefault("player_name")?.ToString()
                     ?? SelectedPlayer?.Name ?? $"slot {PlayerSlot}";
        int team = gameRow?.Dimensions.GetValueOrDefault("team") is { } t
            ? Convert.ToInt32(t, CultureInfo.InvariantCulture)
            : SelectedPlayer?.Team ?? 0;
        TeamLabel = team switch { 2 => "T", 3 => "CT", _ => "" };
        IsCt = team == 3;
        MapName = gameRow?.Dimensions.GetValueOrDefault("map")?.ToString() ?? "";

        List<StatTileItem> tiles = [];
        if (gameRow is not null && Parent.GameTable is { } table)
        {
            foreach ((string key, bool hero) in _coreTileKeys)
            {
                if (!table.ValueColumns.Contains(key))
                {
                    continue;
                }

                ColumnMeta meta = ColumnCatalogue.Resolve(key);
                string display = new StatCell(gameRow.Values.GetValueOrDefault(key)).Display;
                tiles.Add(new StatTileItem(meta.Display, display.Length == 0 ? "–" : display, hero, meta.Tooltip));
            }
        }

        CoreTiles = tiles;
        OnPropertyChanged(nameof(HasGameRow));
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(TeamLabel));
        OnPropertyChanged(nameof(IsCt));
        OnPropertyChanged(nameof(MapName));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(CoreTiles));
    }

    private void RebuildForm()
    {
        MetricTable? roundTable = Parent.RoundTable;
        Form = roundTable is null
            ? FormTimelineViewModel.Empty
            : new FormTimelineViewModel(RoundRowsForSlot(roundTable), roundTable.ValueColumns);
        OnPropertyChanged(nameof(Form));
    }

    private List<MetricRow> RoundRowsForSlot(MetricTable roundTable) =>
        roundTable.Rows
            .Where(r => StatsTabViewModel.RowSlot(r) == PlayerSlot)
            .OrderBy(RoundOf)
            .ToList();

    private void RebuildAchievements()
    {
        List<AchievementItem> items = [];
        if (Parent.EventsTable is { } events)
        {
            foreach (MetricRow row in events.Rows
                         .Where(r => StatsTabViewModel.RowSlot(r) == PlayerSlot)
                         .OrderBy(r => Convert.ToInt32(
                             r.Dimensions.GetValueOrDefault("tick") ?? 0, CultureInfo.InvariantCulture)))
            {
                string chain = row.Dimensions.GetValueOrDefault("chain")?.ToString() ?? "?";
                int round = Convert.ToInt32(
                    row.Dimensions.GetValueOrDefault("round_number") ?? 0, CultureInfo.InvariantCulture);
                int tick = Convert.ToInt32(
                    row.Dimensions.GetValueOrDefault("tick") ?? 0, CultureInfo.InvariantCulture);
                items.Add(AchievementItem.From(chain, round, tick));
            }
        }

        Achievements = items;
        OnPropertyChanged(nameof(Achievements));
        OnPropertyChanged(nameof(HasAchievements));
        OnPropertyChanged(nameof(AchievementsHeader));
    }

    private void RebuildDuels(MetricRow? gameRow, IReadOnlyList<string> columns)
    {
        double duelPct = Math.Clamp(AsDouble(gameRow?.Values.GetValueOrDefault("Duel%")), 0, 100);
        DuelGaugeWidth = duelPct / 100 * KvTrackWidth;
        DuelPercentText = columns.Contains("Duel%")
            ? duelPct.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "";

        List<KeyValueItem> items = BuildKvItems(gameRow, columns, ["TotalFK", "TotalFD", "FK±"]);
        string ctSplit = SplitText(gameRow, columns, "CTFK", "CTFD");
        string tSplit = SplitText(gameRow, columns, "TFK", "TFD");
        if (ctSplit.Length > 0)
        {
            items.Add(new KeyValueItem("CT side", ctSplit, false, false,
                "Opening kills / deaths on the CT side"));
        }

        if (tSplit.Length > 0)
        {
            items.Add(new KeyValueItem("T side", tSplit, false, false,
                "Opening kills / deaths on the T side"));
        }

        DuelItems = items;
        OnPropertyChanged(nameof(DuelGaugeWidth));
        OnPropertyChanged(nameof(DuelPercentText));
        OnPropertyChanged(nameof(DuelItems));
    }

    private static string SplitText(MetricRow? row, IReadOnlyList<string> columns, string kKey, string dKey)
    {
        if (!columns.Contains(kKey) && !columns.Contains(dKey))
        {
            return "";
        }

        double k = AsDouble(row?.Values.GetValueOrDefault(kKey));
        double d = AsDouble(row?.Values.GetValueOrDefault(dKey));
        return string.Create(CultureInfo.InvariantCulture, $"{k:0.##} K / {d:0.##} D");
    }

    private void RebuildClutch(MetricRow? gameRow, IReadOnlyList<string> columns)
    {
        ClutchItems = BuildKvItems(gameRow, columns, ["Clutch", "RapidKills", "Revenge"]);

        const double MaxBarHeight = 28;
        string[] buckets = ["2K", "3K", "4K", "5K"];
        List<(string Label, int Count, bool Positive)> present = [];
        foreach (string key in buckets)
        {
            if (!columns.Contains(key))
            {
                continue;
            }

            ColumnMeta meta = ColumnCatalogue.Resolve(key);
            present.Add((meta.Display, (int)AsDouble(gameRow?.Values.GetValueOrDefault(key)),
                meta.Emphasis == Emphasis.Positive));
        }

        int max = Math.Max(1, present.Count == 0 ? 0 : present.Max(b => b.Count));
        MultiKillBars = present
            .Select(b => new HistBarItem(b.Label, b.Count,
                Math.Max(1, (double)b.Count / max * MaxBarHeight), b.Positive))
            .ToList();
        OnPropertyChanged(nameof(ClutchItems));
        OnPropertyChanged(nameof(MultiKillBars));
    }

    private void RebuildDamage(MetricRow? gameRow, IReadOnlyList<string> columns)
    {
        List<KeyValueItem> items = BuildKvItems(gameRow, columns, ["EnemyDmg"]);

        // Display-only derived accuracy (design P-9): HitFoe / Shots · 100, never a golden column.
        if (columns.Contains("Shots") && columns.Contains("HitFoe"))
        {
            double shots = AsDouble(gameRow?.Values.GetValueOrDefault("Shots"));
            double hits = AsDouble(gameRow?.Values.GetValueOrDefault("HitFoe"));
            string acc = shots > 0
                ? (hits / shots * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%"
                : "–";
            items.Add(new KeyValueItem("Accuracy", acc, false, false,
                "Shots that hit an enemy ÷ shots fired (derived for display)"));
        }

        items.AddRange(BuildKvItems(gameRow, columns,
            ["Shots", "HitFoe", "TotalHS", "HS%", "TeamDmg", "SelfDmg", "AvgHP→Dmg"]));
        DamageItems = items;
        OnPropertyChanged(nameof(DamageItems));
    }

    private void RebuildRoundsTable()
    {
        MetricTable? roundTable = Parent.RoundTable;
        if (roundTable is null)
        {
            RoundTableColumns = [];
            RoundTableRows = [];
            OnPropertyChanged(nameof(RoundTableColumns));
            OnPropertyChanged(nameof(RoundTableRows));
            OnPropertyChanged(nameof(HasRoundRows));
            return;
        }

        bool hasOpn = roundTable.ValueColumns.Contains("FK") || roundTable.ValueColumns.Contains("FD");
        List<string> keys = _roundDetailKeys
            .Where(k => k == "__opn__" ? hasOpn : roundTable.ValueColumns.Contains(k))
            .ToList();

        List<DetailRoundColumn> columns = new(keys.Count);
        foreach (string key in keys)
        {
            if (key == "__opn__")
            {
                columns.Add(new DetailRoundColumn(key, "Opn", 44,
                    "Opening duel this round (▲ opening kill, ▼ opening death)"));
                continue;
            }

            ColumnMeta meta = ColumnCatalogue.Resolve(key);
            columns.Add(new DetailRoundColumn(key, meta.Display, meta.Width, meta.Tooltip));
        }

        List<DetailRoundRow> rows = [];
        foreach (MetricRow row in RoundRowsForSlot(roundTable))
        {
            int round = RoundOf(row);
            List<StatCell> cells = new(keys.Count);
            foreach (string key in keys)
            {
                cells.Add(key == "__opn__"
                    ? OpnCell(row)
                    : new StatCell(
                        row.Values.GetValueOrDefault(key), ColumnCatalogue.Resolve(key)));
            }

            rows.Add(new DetailRoundRow(round, cells, round == SelectedDetailRound));
        }

        RoundTableColumns = columns;
        RoundTableRows = rows;
        OnPropertyChanged(nameof(RoundTableColumns));
        OnPropertyChanged(nameof(RoundTableRows));
        OnPropertyChanged(nameof(HasRoundRows));
    }

    /// <summary>Synthetic Opn cell: ▲ opening kill (positive), ▼ opening death (negative).</summary>
    private static StatCell OpnCell(MetricRow row)
    {
        // FK/FD are per-round BOOL columns (bool true when active): numeric coercion reads 0.
        bool fk = IsTruthy(row.Values.GetValueOrDefault("FK"));
        bool fd = IsTruthy(row.Values.GetValueOrDefault("FD"));
        string glyph = (fk, fd) switch
        {
            (true, true) => "▲▼",
            (true, false) => "▲",
            (false, true) => "▼",
            _ => ""
        };
        Emphasis emphasis = fk ? Emphasis.Positive : fd ? Emphasis.Negative : Emphasis.None;
        return new StatCell(glyph.Length == 0 ? null : glyph,
            new ColumnMeta("__opn__", "Opn", StatGroup.OpeningDuels, 0, false, emphasis,
                ColumnAggregate.None, "Opening duel this round", 44));
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static int RoundOf(MetricRow row) =>
        Convert.ToInt32(row.Dimensions.GetValueOrDefault("round_number") ?? 0, CultureInfo.InvariantCulture);

    internal static double AsDouble(object? value) =>
        value is int or long or double or float
            ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
            : 0;

    internal static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        float f => f != 0,
        string s => s is not ("" or "0"),
        _ => true
    };

    /// <summary>
    ///     Builds label/value rows for the given engine keys: display name + tooltip from
    ///     <see cref="ColumnCatalogue" />, value via <see cref="StatCell" /> formatting, emphasis
    ///     accents only when the value is non-zero (zeros are meaningful but not remarkable).
    /// </summary>
    private static List<KeyValueItem> BuildKvItems(
        MetricRow? row, IReadOnlyList<string> columns, string[] keys)
    {
        List<KeyValueItem> items = [];
        foreach (string key in keys)
        {
            if (!columns.Contains(key))
            {
                continue;
            }

            ColumnMeta meta = ColumnCatalogue.Resolve(key);
            object? raw = row?.Values.GetValueOrDefault(key);
            string display = new StatCell(raw).Display;
            bool nonZero = AsDouble(raw) != 0;
            items.Add(new KeyValueItem(
                meta.Display,
                display.Length == 0 ? "–" : display,
                meta.Emphasis == Emphasis.Positive && nonZero,
                meta.Emphasis == Emphasis.Negative && nonZero,
                meta.Tooltip));
        }

        return items;
    }
}

/// <summary>
///     Form-timeline geometry: measured per-round Kills sparkline, Damage bar
///     strip, KAST dot-strip, and opening-duel ticks. All values pre-scaled to fixed pixel boxes so
///     the view only draws. Strips whose source column is absent are hidden, never zero-faked.
/// </summary>
public sealed class FormTimelineViewModel
{
    /// <summary>Horizontal pixels per round: shared by all four strips so columns align.</summary>
    public const double SlotWidth = 14;

    private const double SparkHeight = 36;
    private const double BarBoxHeight = 36;

    /// <summary>Builds geometry from this player's round rows (already round-ordered).</summary>
    public FormTimelineViewModel(IReadOnlyList<MetricRow> roundRows, IReadOnlyList<string> valueColumns)
    {
        ArgumentNullException.ThrowIfNull(roundRows);
        ArgumentNullException.ThrowIfNull(valueColumns);

        HasRounds = roundRows.Count > 0;
        HasKast = valueColumns.Contains("HasKAST");
        HasDuels = valueColumns.Contains("FK") || valueColumns.Contains("FD");
        PixelWidth = Math.Max(SlotWidth, roundRows.Count * SlotWidth);

        if (!HasRounds)
        {
            RangeLabel = "";
            return;
        }

        List<(int Round, double Kills, double Deaths, double Assists, double Damage, bool Kast,
            bool Fk, bool Fd)> rounds = [];
        foreach (MetricRow row in roundRows)
        {
            int round = Convert.ToInt32(
                row.Dimensions.GetValueOrDefault("round_number") ?? 0, CultureInfo.InvariantCulture);
            rounds.Add((round,
                PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("Kills")),
                PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("Deaths")),
                PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("Assists")),
                PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("Damage")),
                PlayerDetailsViewModel.IsTruthy(row.Values.GetValueOrDefault("HasKAST")),
                PlayerDetailsViewModel.IsTruthy(row.Values.GetValueOrDefault("FK")),
                PlayerDetailsViewModel.IsTruthy(row.Values.GetValueOrDefault("FD"))));
        }

        RangeLabel = string.Create(CultureInfo.InvariantCulture,
            $"rounds {rounds[0].Round}–{rounds[^1].Round}");

        double killMax = Math.Max(1, rounds.Max(r => r.Kills));
        double dmgMax = Math.Max(1, rounds.Max(r => r.Damage));

        List<Point> points = new(rounds.Count);
        List<FormBar> bars = new(rounds.Count);
        List<FormDot> dots = new(rounds.Count);
        List<FormTick> ticks = new(rounds.Count);
        for (int i = 0; i < rounds.Count; i++)
        {
            (int round, double kills, double deaths, double assists, double damage, bool kast,
                bool fk, bool fd) = rounds[i];
            string tooltip = string.Create(CultureInfo.InvariantCulture,
                                 $"r{round}: {kills:0.##}K {assists:0.##}A {deaths:0.##}D · {damage:0.##} dmg")
                             + (HasKast ? $" · KAST {(kast ? "✓" : "✗")}" : "");

            points.Add(new Point(i * SlotWidth + SlotWidth / 2,
                2 + (1 - kills / killMax) * (SparkHeight - 4)));
            bars.Add(new FormBar(round, Math.Max(1, damage / dmgMax * (BarBoxHeight - 2)), tooltip));
            dots.Add(new FormDot(round, kast, tooltip));
            ticks.Add(new FormTick(round, fk ? "▲" : fd ? "▼" : "·", fk, !fk && fd, tooltip));
        }

        KillPoints = points;
        DamageBars = bars;
        KastDots = dots;
        DuelTicks = ticks;
    }

    /// <summary>An empty timeline (no live rounds).</summary>
    public static FormTimelineViewModel Empty { get; } = new([], []);

    /// <summary>False when the player has no live-round rows (empty state).</summary>
    public bool HasRounds { get; }

    /// <summary>True when the round table carries a KAST column (else the strip hides).</summary>
    public bool HasKast { get; }

    /// <summary>True when the round table carries FK/FD columns (else the tick row hides).</summary>
    public bool HasDuels { get; }

    /// <summary>"rounds 1–30" range caption.</summary>
    public string RangeLabel { get; }

    /// <summary>Width of every strip in px (rounds × slot width).</summary>
    public double PixelWidth { get; }

    /// <summary>Kills sparkline points, pre-scaled to the spark box.</summary>
    public IList<Point> KillPoints { get; } = [];

    /// <summary>Damage bar per round (height pre-scaled to the bar box).</summary>
    public IReadOnlyList<FormBar> DamageBars { get; } = [];

    /// <summary>KAST dot per round (filled = KAST round).</summary>
    public IReadOnlyList<FormDot> KastDots { get; } = [];

    /// <summary>Opening-duel tick per round (▲ opening kill / ▼ opening death / · neither).</summary>
    public IReadOnlyList<FormTick> DuelTicks { get; } = [];
}

/// <summary>
///     P-4 weapon breakdown: horizontal share bars over the keyed per-weapon tables, with a
///     kills/damage metric toggle that persists across player switches. Tables are located by
///     <see cref="MetricTable.Name" /> and the value column is read from <c>ValueColumns[0]</c>:
///     never a hardcoded column name.
/// </summary>
public sealed partial class WeaponBreakdownViewModel : ObservableObject
{
    private const double TrackWidth = 150;
    private const string KillsTableName = "player_kills_by_weapon";
    private const string DamageTableName = "player_damage_by_weapon";

    private readonly StatsTabViewModel _parent;

    /// <summary>True = the damage metric is active; false = kills.</summary>
    [ObservableProperty]
    private bool _showDamage;

    private int _slot = -1;

    internal WeaponBreakdownViewModel(StatsTabViewModel parent) => _parent = parent;

    /// <summary>Toggle-pill hook (inverse of <see cref="ShowDamage" />).</summary>
    public bool ShowKills => !ShowDamage;

    /// <summary>Share bars for the active metric, sorted descending.</summary>
    public IReadOnlyList<BarRowItem> Bars { get; private set; } = [];

    /// <summary>True when there is nothing to chart (message in <see cref="EmptyMessage" />).</summary>
    public bool ShowEmpty => Bars.Count == 0;

    /// <summary>Distinguishes rules-not-loaded from no-kills (design's two empty states).</summary>
    public string EmptyMessage { get; private set; } = "";

    partial void OnShowDamageChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowKills));
        Rebuild();
    }

    /// <summary>Selects the kills metric.</summary>
    [RelayCommand]
    private void SelectKills() => ShowDamage = false;

    /// <summary>Selects the damage metric.</summary>
    [RelayCommand]
    private void SelectDamage() => ShowDamage = true;

    internal void SetSlot(int slot)
    {
        _slot = slot;
        Rebuild();
    }

    private void Rebuild()
    {
        MetricTable? active = FindTable(ShowDamage ? DamageTableName : KillsTableName);
        MetricTable? other = FindTable(ShowDamage ? KillsTableName : DamageTableName);

        if (active is null)
        {
            Bars = [];
            EmptyMessage = other is null
                ? "Load the weapon-stats rules to see per-weapon breakdowns."
                : $"No weapon {(ShowDamage ? "damage" : "kills")} recorded.";
            Notify();
            return;
        }

        string valueColumn = active.ValueColumns[0];
        List<(string Weapon, double Value)> rows = active.Rows
            .Where(r => StatsTabViewModel.RowSlot(r) == _slot)
            .Select(r => (
                Weapon: r.Dimensions.GetValueOrDefault("key")?.ToString() ?? "?",
                Value: PlayerDetailsViewModel.AsDouble(r.Values.GetValueOrDefault(valueColumn))))
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Weapon, StringComparer.OrdinalIgnoreCase)
            .ToList();

        double max = Math.Max(1, rows.Count == 0 ? 0 : rows.Max(x => x.Value));
        Bars = rows
            .Select(x => new BarRowItem(x.Weapon, x.Value / max * TrackWidth,
                x.Value.ToString("0.##", CultureInfo.InvariantCulture),
                $"{x.Weapon}: {x.Value.ToString("0.##", CultureInfo.InvariantCulture)} {(ShowDamage ? "damage" : "kills")}"))
            .ToList();
        EmptyMessage = Bars.Count == 0
            ? $"No weapon {(ShowDamage ? "damage" : "kills")} recorded."
            : "";
        Notify();
    }

    private MetricTable? FindTable(string name) =>
        _parent.ExtraTables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    private void Notify()
    {
        OnPropertyChanged(nameof(Bars));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
    }
}

/// <summary>
///     P-11/P-12 vision panel: per-player summary bars plus the directed per-opponent matrix
///     ("I saw them" = rows where this player is the viewer; "exposed to them" = rows where this
///     player is the target, both directions). Mirrors the parent's compute gate for
///     its two empty states.
/// </summary>
public sealed class VisionViewModel : ObservableObject
{
    private const double TrackWidth = 150;

    private readonly StatsTabViewModel _parent;

    internal VisionViewModel(StatsTabViewModel parent) => _parent = parent;

    /// <summary>True when computed visibility rows exist for this player.</summary>
    public bool HasData { get; private set; }

    /// <summary>True → show the "Compute 3-D line-of-sight" CTA (map has a bake, not computed).</summary>
    public bool ShowCta { get; private set; }

    /// <summary>True → show <see cref="UnavailableMessage" /> (no bake, or no samples).</summary>
    public bool ShowUnavailable { get; private set; }

    /// <summary>Why visibility is unavailable.</summary>
    public string UnavailableMessage { get; private set; } = "";

    /// <summary>Exposed / vision share bars (fractions of sampled time).</summary>
    public IReadOnlyList<BarRowItem> SummaryBars { get; private set; } = [];

    /// <summary>"312.4 s exposed · 414.9 s vision" caption.</summary>
    public string SecondsSummary { get; private set; } = "";

    /// <summary>Per-opponent directed rows, sorted by "I saw them" descending.</summary>
    public IReadOnlyList<OpponentVisionRow> Opponents { get; private set; } = [];

    /// <summary>True when the matrix has rows.</summary>
    public bool HasOpponents => Opponents.Count > 0;

    internal void Refresh(int slot, string mapName)
    {
        MetricRow? row = _parent.VisibilityPlayersTable?.Rows
            .FirstOrDefault(r => StatsTabViewModel.RowSlot(r) == slot);
        HasData = _parent.HasVisibilityStats && row is not null;
        ShowCta = !_parent.HasVisibilityStats && _parent.CanComputeVisibility;
        ShowUnavailable = !HasData && !ShowCta;
        UnavailableMessage = _parent.HasVisibilityStats
            ? "Visibility replay sampled no data for this player."
            : $"Visibility unavailable — no collision bake for {(mapName.Length > 0 ? mapName : "this map")}.";

        if (row is not null)
        {
            double exposedSec = PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("ExposedToEnemiesSec"));
            double visionSec = PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("CouldSeeEnemySec"));
            double exposedShare = Math.Clamp(
                PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("ExposedShare")), 0, 1);
            double visionShare = Math.Clamp(
                PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("VisionShare")), 0, 1);
            SummaryBars =
            [
                new BarRowItem("Exposed", exposedShare * TrackWidth, FormatShare(exposedShare),
                    "Time at least one enemy had a clear 3D line of sight to this player, as a share of sampled time"),
                new BarRowItem("Vision", visionShare * TrackWidth, FormatShare(visionShare),
                    "Time this player had at least one enemy on screen, as a share of sampled time")
            ];
            SecondsSummary = string.Create(CultureInfo.InvariantCulture,
                $"{exposedSec:0.#} s exposed · {visionSec:0.#} s vision");
            Opponents = BuildOpponents(slot);
        }
        else
        {
            SummaryBars = [];
            SecondsSummary = "";
            Opponents = [];
        }

        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(ShowCta));
        OnPropertyChanged(nameof(ShowUnavailable));
        OnPropertyChanged(nameof(UnavailableMessage));
        OnPropertyChanged(nameof(SummaryBars));
        OnPropertyChanged(nameof(SecondsSummary));
        OnPropertyChanged(nameof(Opponents));
        OnPropertyChanged(nameof(HasOpponents));
    }

    private List<OpponentVisionRow> BuildOpponents(int slot)
    {
        MetricTable? pairs = _parent.VisibilityPairsTable;
        if (pairs is null)
        {
            return [];
        }

        Dictionary<int, (string Name, double Saw, double Exposed)> byOpponent = [];
        foreach (MetricRow row in pairs.Rows)
        {
            int viewer = DimInt(row, "viewer_slot");
            int target = DimInt(row, "target_slot");
            if (viewer == slot)
            {
                string name = row.Dimensions.GetValueOrDefault("target_name")?.ToString() ?? $"slot {target}";
                (_, double saw, double exposed) = byOpponent.GetValueOrDefault(target, (name, 0, 0));
                byOpponent[target] = (name,
                    saw + PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("could_see_sec")), exposed);
            }
            else if (target == slot)
            {
                string name = row.Dimensions.GetValueOrDefault("viewer_name")?.ToString() ?? $"slot {viewer}";
                (_, double saw, double exposed) = byOpponent.GetValueOrDefault(viewer, (name, 0, 0));
                byOpponent[viewer] = (name, saw,
                    exposed + PlayerDetailsViewModel.AsDouble(row.Values.GetValueOrDefault("exposed_sec")));
            }
        }

        return byOpponent.Values
            .OrderByDescending(o => o.Saw)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => new OpponentVisionRow(o.Name, FormatSeconds(o.Saw), FormatSeconds(o.Exposed)))
            .ToList();
    }

    private static int DimInt(MetricRow row, string key) =>
        row.Dimensions.GetValueOrDefault(key) is { } v
            ? Convert.ToInt32(v, CultureInfo.InvariantCulture)
            : -1;

    private static string FormatSeconds(double s) => s.ToString("0.#", CultureInfo.InvariantCulture) + " s";

    private static string FormatShare(double share) =>
        (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";
}

/// <summary>One core-strip tile: big value over a small label.</summary>
public sealed record StatTileItem(string Label, string Value, bool IsHero, string Tooltip);

/// <summary>One labelled share bar: label, pre-scaled fill width (px), value text.</summary>
public sealed record BarRowItem(string Label, double BarWidth, string ValueText, string Tooltip = "");

/// <summary>One label/value row with optional good/bad accent.</summary>
public sealed record KeyValueItem(string Label, string Value, bool IsPositive, bool IsNegative, string Tooltip);

/// <summary>One multi-kill histogram column: label, count, pre-scaled height.</summary>
public sealed record HistBarItem(string Label, int Count, double Height, bool Positive)
{
    /// <summary>Count text above the bar (empty when zero, so zero buckets read as quiet).</summary>
    public string CountText => Count > 0 ? Count.ToString(CultureInfo.InvariantCulture) : "";
}

/// <summary>One damage bar in the form strip (height pre-scaled; Round is the deep-link key).</summary>
public sealed record FormBar(int Round, double Height, string Tooltip);

/// <summary>One KAST dot in the form strip.</summary>
public sealed record FormDot(int Round, bool Filled, string Tooltip);

/// <summary>One opening-duel tick in the form strip (▲ up / ▼ down / · neither).</summary>
public sealed record FormTick(int Round, string Glyph, bool Up, bool Down, string Tooltip);

/// <summary>One rounds-table column header (engine key + display metadata).</summary>
public sealed record DetailRoundColumn(string Key, string Display, double Width, string Tooltip);

/// <summary>One rounds-table row: round number + cells aligned with the column list.</summary>
public sealed record DetailRoundRow(int Round, IReadOnlyList<StatCell> Cells, bool IsSelected)
{
    /// <summary>Round-number cell text.</summary>
    public string RoundLabel => Round.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One per-opponent directed vision row ("I saw them" / "exposed to them").</summary>
public sealed record OpponentVisionRow(string Name, string SawText, string ExposedText);

/// <summary>One achievement chip: chain id + locators, with a family accent.</summary>
public sealed record AchievementItem(
    string Chain,
    string RoundLabel,
    int Tick,
    bool IsMultiKill,
    bool IsClutch,
    bool IsOpening)
{
    /// <summary>Classifies the chain id into a chip-accent family by name convention.</summary>
    public static AchievementItem From(string chain, int round, int tick)
    {
        string label = round > 0
            ? $"round {round}"
            : "warmup";
        string c = chain.ToUpperInvariant();
        bool multi = c.Contains("ACE", StringComparison.Ordinal)
                     || c.Contains("MULTI", StringComparison.Ordinal)
                     || c.Contains("RAPID", StringComparison.Ordinal)
                     || c.Contains("2K", StringComparison.Ordinal)
                     || c.Contains("3K", StringComparison.Ordinal)
                     || c.Contains("4K", StringComparison.Ordinal)
                     || c.Contains("5K", StringComparison.Ordinal);
        bool clutch = c.Contains("CLUTCH", StringComparison.Ordinal);
        bool opening = !multi && !clutch
                              && (c.Contains("OPENING", StringComparison.Ordinal)
                                  || c.Contains("FIRST", StringComparison.Ordinal)
                                  || c.Contains("ENTRY", StringComparison.Ordinal));
        return new AchievementItem(chain, label, tick, multi && !clutch, clutch, opening);
    }
}
