#region

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>How a team's match ended, for the pill next to its name.</summary>
public enum TeamOutcome
{
    /// <summary>Unknown or not applicable: no pill.</summary>
    None,

    /// <summary>Team won.</summary>
    Win,

    /// <summary>Team lost.</summary>
    Loss,

    /// <summary>Match tied.</summary>
    Draw
}

/// <summary>
///     A team's identity strip: a side-coloured edge, the team name, and an outcome pill. The section
///     header above each half of the scoreboard, and the same control shrunk down for the per-row side
///     marker the board currently draws as an inline bullet.
///     <para>
///         <see cref="Team" /> takes the CS2 wire values, matching <c>StatsRow.Team</c>: 2 is T, 3 is CT,
///         anything else is a spectator or unknown and gets no side colour. Taking the raw value keeps
///         every call site free of a conversion the rest of the stats code does not do.
///     </para>
///     <para>
///         Side and outcome are exposed as pseudo-classes (<c>:ct</c>, <c>:t</c>, <c>:win</c>,
///         <c>:loss</c>, <c>:draw</c>) so <c>Styles/Stats.axaml</c> owns the colours and the pill's
///         visibility, and no brush is held in code.
///     </para>
/// </summary>
public class TeamBadge : TemplatedControl
{
    /// <summary>CS2 wire value for the Terrorist side.</summary>
    public const int TeamT = 2;

    /// <summary>CS2 wire value for the Counter-Terrorist side.</summary>
    public const int TeamCt = 3;

    /// <summary>CS2 wire team value: 2 = T, 3 = CT. Anything else draws no side colour.</summary>
    public static readonly StyledProperty<int> TeamProperty =
        AvaloniaProperty.Register<TeamBadge, int>(nameof(Team));

    /// <summary>The team's display name.</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<TeamBadge, string?>(nameof(Label));

    /// <summary>Win, loss, draw or nothing.</summary>
    public static readonly StyledProperty<TeamOutcome> OutcomeProperty =
        AvaloniaProperty.Register<TeamBadge, TeamOutcome>(nameof(Outcome));

    /// <summary>Optional trailing detail, such as a round score.</summary>
    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<TeamBadge, string?>(nameof(Detail));

    /// <summary>
    ///     Read-only surface for the pill's text, so a template can bind it without a converter. Direct
    ///     rather than styled because it is derived from <see cref="Outcome" /> and must never be set from
    ///     outside.
    /// </summary>
    public static readonly DirectProperty<TeamBadge, string> OutcomeTextProperty =
        AvaloniaProperty.RegisterDirect<TeamBadge, string>(nameof(OutcomeText), o => o.OutcomeText);

    /// <inheritdoc cref="TeamProperty" />
    public int Team
    {
        get => GetValue(TeamProperty);
        set => SetValue(TeamProperty, value);
    }

    /// <inheritdoc cref="LabelProperty" />
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <inheritdoc cref="OutcomeProperty" />
    public TeamOutcome Outcome
    {
        get => GetValue(OutcomeProperty);
        set => SetValue(OutcomeProperty, value);
    }

    /// <inheritdoc cref="DetailProperty" />
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>The outcome pill's text. Empty when there is no outcome to show.</summary>
    public string OutcomeText => Outcome switch
    {
        TeamOutcome.Win => "WIN",
        TeamOutcome.Loss => "LOSS",
        TeamOutcome.Draw => "DRAW",
        _ => string.Empty
    };

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TeamProperty)
        {
            PseudoClasses.Set(":ct", Team == TeamCt);
            PseudoClasses.Set(":t", Team == TeamT);
        }

        if (change.Property == OutcomeProperty)
        {
            TeamOutcome old = change.GetOldValue<TeamOutcome>();
            PseudoClasses.Set(":win", Outcome == TeamOutcome.Win);
            PseudoClasses.Set(":loss", Outcome == TeamOutcome.Loss);
            PseudoClasses.Set(":draw", Outcome == TeamOutcome.Draw);
            RaisePropertyChanged(OutcomeTextProperty, TextFor(old), OutcomeText);
        }

        if (change.Property == LabelProperty || change.Property == OutcomeProperty)
        {
            AutomationProperties.SetName(this,
                OutcomeText.Length > 0 ? $"{Label} {OutcomeText}" : Label ?? string.Empty);
        }
    }

    private static string TextFor(TeamOutcome outcome) => outcome switch
    {
        TeamOutcome.Win => "WIN",
        TeamOutcome.Loss => "LOSS",
        TeamOutcome.Draw => "DRAW",
        _ => string.Empty
    };
}
