#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     The subject's best marks over the filter's window: most kills, best HLTV and best ADR in one match, and
///     the longest run of won matches. Each links to its demo, which opens with the subject focused in Stats; a
///     streak links to the match that ended it at its longest. Demos without stats do not count, and a tie ends a
///     streak. The marks come from the stats the tab already read, so the section never loads anything itself.
/// </summary>
public sealed partial class PersonalBestsSectionViewModel : PlayerOverviewSection
{
    private readonly PlayerOverviewServices _services;
    private ulong _subject;

    [ObservableProperty]
    private IReadOnlyList<PersonalBestItem> _rows = [];

    /// <param name="services">The directory that opens demos and the shell's focus action.</param>
    public PersonalBestsSectionViewModel(PlayerOverviewServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public override string Id => "personal-bests";

    /// <inheritdoc />
    public override PlayerOverviewPageKind Page => PlayerOverviewPageKind.Achievements;

    /// <inheritdoc />
    public override SectionSlot Slot => SectionSlot.Top;

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _subject = context.Player?.SteamId64 ?? 0;
        Rows = context.Player is null ? [] : Build(context.Window);
        IsVisible = Rows.Count > 0;
    }

    /// <summary>The bests of <paramref name="demos" />; a tie goes to the newest match.</summary>
    /// <param name="demos">The demos in the window, newest match first.</param>
    internal static IReadOnlyList<PersonalBestItem> Build(IReadOnlyList<PlayerDemo> demos)
    {
        List<PersonalBestItem> rows = [];
        AddBest(rows, demos, "Most kills", "kills in one match", d => d.Stats?.GetValueOrDefault("TotalK"), "0");
        AddBest(rows, demos, "Best HLTV", "HLTV in one match", d => Single(d, "HLTV"), "0.00");
        AddBest(rows, demos, "Best ADR", "ADR in one match", d => Single(d, "ADR"), "0.0");

        // Oldest first; a demo with no outcome is skipped rather than breaking the run.
        int run = 0, best = 0;
        PlayerDemo? ender = null;
        for (int i = demos.Count - 1; i >= 0; i--)
        {
            switch (demos[i].Outcome)
            {
                case MatchOutcome.Win:
                    run++;
                    if (run >= best)
                    {
                        best = run;
                        ender = demos[i];
                    }

                    break;
                case not null:
                    run = 0;
                    break;
            }
        }

        if (ender is not null)
        {
            rows.Add(new PersonalBestItem("Longest win streak", best.ToString(CultureInfo.InvariantCulture),
                best == 1 ? "win" : "wins in a row", ender.Match));
        }

        return rows;
    }

    private static double? Single(PlayerDemo demo, string key) =>
        demo.Stats is { } stats ? ColumnRules.For(key)?.Pool([stats]) : null;

    private static void AddBest(List<PersonalBestItem> rows, IReadOnlyList<PlayerDemo> demos, string label, string unit,
        Func<PlayerDemo, double?> value, string format)
    {
        PlayerDemo? at = null;
        double top = 0;
        foreach (PlayerDemo demo in demos)
        {
            if (value(demo) is { } v && (at is null || v > top))
            {
                at = demo;
                top = v;
            }
        }

        if (at is not null)
        {
            rows.Add(new PersonalBestItem(label, top.ToString(format, CultureInfo.InvariantCulture), unit, at.Match));
        }
    }

    /// <summary>Opens the row's demo and shows the subject in Stats, or marks the file missing.</summary>
    [RelayCommand]
    private void Open(PersonalBestItem? row)
    {
        if (row is null)
        {
            return;
        }

        string? path = _services.Directory.OpenPath(row.Match);
        row.IsFileMissing = path is null;
        if (path is not null)
        {
            _services.FocusPlayerInStats(path, _subject);
        }
    }
}

/// <summary>One personal best, every cell formatted.</summary>
public sealed partial class PersonalBestItem : ObservableObject
{
    /// <summary>True when <see cref="PersonalBestsSectionViewModel.OpenCommand" /> found no path on disk.</summary>
    [ObservableProperty]
    private bool _isFileMissing;

    /// <param name="label">What was measured.</param>
    /// <param name="valueText">The mark.</param>
    /// <param name="unitText">What the mark counts.</param>
    /// <param name="match">The demo it was set in.</param>
    public PersonalBestItem(string label, string valueText, string unitText, PlayerMatch match)
    {
        Label = label;
        ValueText = valueText;
        UnitText = unitText;
        Match = match ?? throw new ArgumentNullException(nameof(match));
        WhereText = $"{DemoEntry.PrettifyMap(match.Row.Map)} · {HistoryRowItem.DateOf(match)}";
    }

    /// <summary>"Most kills".</summary>
    public string Label { get; }

    /// <summary>The mark, "34" or "1.62".</summary>
    public string ValueText { get; }

    /// <summary>"kills in one match".</summary>
    public string UnitText { get; }

    /// <summary>The demo's map and time.</summary>
    public string WhereText { get; }

    /// <summary>The demo.</summary>
    public PlayerMatch Match { get; }
}
