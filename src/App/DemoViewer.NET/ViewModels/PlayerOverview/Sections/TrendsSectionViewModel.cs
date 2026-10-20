#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     HLTV, ADR and K/D match by match over the filter's window, oldest on the left, and the subject's form: the
///     last <see cref="FormSize" /> matches against the <see cref="FormSize" /> before them, each pooled by its
///     <see cref="ColumnRules" /> rule. Only matches with stats are drawn; a match still pending is not a zero.
///     The section needs two matches with stats for a line and <see cref="MinFormSize" /> either side for a form.
/// </summary>
public sealed partial class TrendsSectionViewModel : PlayerOverviewSection
{
    /// <summary>The most matches either side of the form comparison holds.</summary>
    public const int FormSize = 10;

    /// <summary>The fewest matches either side of the form comparison holds, so the shortest history it covers is twice this.</summary>
    public const int MinFormSize = 3;

    /// <summary>The change that fills one arm of a form bar, in percent. Shared, so the three bars compare.</summary>
    public const double FormExtent = 25;

    private static readonly (string Key, string Label, string Format)[] Metrics =
    [
        ("HLTV", "Rating", "0.00"),
        ("ADR", "ADR", "0.0"),
        ("KD", "K/D", "0.00")
    ];

    [ObservableProperty]
    private string _formText = "";

    [ObservableProperty]
    private IReadOnlyList<TrendItem> _trends = [];

    /// <inheritdoc />
    public override string Id => "trends";

    /// <summary>True when the window holds enough matches to compare the last against the previous.</summary>
    public bool HasForm => FormText.Length > 0;

    partial void OnFormTextChanged(string value) => OnPropertyChanged(nameof(HasForm));

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<PlayerDemo> played = context.Player is null ? [] : [.. context.Window.Where(d => d.Stats is not null)];
        if (played.Count < 2)
        {
            Trends = [];
            FormText = "";
            IsVisible = false;
            return;
        }

        int size = Math.Min(FormSize, played.Count / 2);
        bool form = size >= MinFormSize;
        PlayerAggregate? recent = form ? PlayerAggregate.Of(played.GetRange(0, size)) : null;
        PlayerAggregate? previous = form ? PlayerAggregate.Of(played.GetRange(size, size)) : null;

        Trends = [.. Metrics.Select(m => new TrendItem(m.Key, m.Label, m.Format, played, recent, previous)).Where(t => t.HasLine)];
        FormText = form ? string.Create(CultureInfo.InvariantCulture, $"Last {size} against the {size} before") : "";
        IsVisible = Trends.Count > 0;
    }
}

/// <summary>One metric over the subject's matches: the line, its average and the form bar.</summary>
public sealed class TrendItem
{
    /// <param name="key">The catalogue key.</param>
    /// <param name="label">The caption.</param>
    /// <param name="format">The number format of the key.</param>
    /// <param name="played">The window's demos with stats, newest first.</param>
    /// <param name="recent">The pool of the last matches; null when the history is too short for a form.</param>
    /// <param name="previous">The pool of the matches before them.</param>
    internal TrendItem(string key, string label, string format, IReadOnlyList<PlayerDemo> played, PlayerAggregate? recent, PlayerAggregate? previous)
    {
        Key = key;
        Label = label;

        List<double> values = [];
        List<string> tips = [];
        for (int i = played.Count - 1; i >= 0; i--)
        {
            if (PlayerAggregate.Of([played[i]])[key] is not { } value || !double.IsFinite(value))
            {
                continue;
            }

            values.Add(value);
            tips.Add($"{RankDisplay.DateOf(played[i].Match)} · {DemoEntry.PrettifyMap(played[i].Match.Row.Map)} · {value.ToString(format, CultureInfo.InvariantCulture)}");
        }

        Values = values;
        PointTips = tips;
        if (values.Count == 0)
        {
            LatestText = AverageText = "";
            return;
        }

        LatestText = values[^1].ToString(format, CultureInfo.InvariantCulture);
        AverageText = "avg " + values.Average().ToString(format, CultureInfo.InvariantCulture);

        if (recent?[key] is { } now && previous?[key] is { } before)
        {
            RecentText = now.ToString(format, CultureInfo.InvariantCulture);
            PreviousText = before.ToString(format, CultureInfo.InvariantCulture);
            if (before > 0)
            {
                ChangePercent = (now - before) / before * 100;
                ChangeText = string.Create(CultureInfo.InvariantCulture, $"{(ChangePercent > 0 ? "+" : "")}{ChangePercent:0.0}%");
            }
        }
    }

    /// <summary>The catalogue key: HLTV, ADR or KD.</summary>
    public string Key { get; }

    /// <summary>The caption.</summary>
    public string Label { get; }

    /// <summary>The metric after each match with stats, oldest first.</summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>One line per <see cref="Values" /> entry: when, where and how much.</summary>
    public IReadOnlyList<string> PointTips { get; }

    /// <summary>True when there are two points to draw a line through.</summary>
    public bool HasLine => Values.Count > 1;

    /// <summary>The newest match's value.</summary>
    public string LatestText { get; }

    /// <summary>"avg 1.12" over the drawn matches.</summary>
    public string AverageText { get; }

    /// <summary>The last matches' pooled value; empty without a form.</summary>
    public string RecentText { get; } = "";

    /// <summary>The previous matches' pooled value; empty without a form.</summary>
    public string PreviousText { get; } = "";

    /// <summary>"+6.2%" or "-3.0%"; empty when the form is unknown or the previous value is zero.</summary>
    public string ChangeText { get; } = "";

    /// <summary>The change from the previous matches to the last, in percent; zero when unknown.</summary>
    public double ChangePercent { get; }

    /// <summary>True when the form bar has something to draw.</summary>
    public bool HasForm => ChangeText.Length > 0;

    /// <summary>The form bar's losing arm: how far the last matches fell.</summary>
    public double FallPercent => Math.Max(-ChangePercent, 0);

    /// <summary>The form bar's winning arm: how far the last matches rose.</summary>
    public double RisePercent => Math.Max(ChangePercent, 0);

    /// <summary>"1.21 against 1.10".</summary>
    public string FormDetail => HasForm ? $"{RecentText} against {PreviousText}" : "";

    /// <summary>The shared half-scale of every form bar.</summary>
    public double Extent { get; } = TrendsSectionViewModel.FormExtent;
}
