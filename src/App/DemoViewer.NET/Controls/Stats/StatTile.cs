#region

using Avalonia;
using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>
///     A headline stat as a labelled tile: big number over a small caption, coloured by sentiment. The
///     component behind the player view's core stat strip.
///     <para>
///         Templated rather than drawn, because it is layout and text with no custom geometry. The number
///         itself is a nested <see cref="StatValue" /> in <see cref="StatValueMode.Plain" />, so the
///         sentiment rule is not reimplemented here: it arrives with the control.
///     </para>
/// </summary>
public class StatTile : StatPresenter
{
    /// <summary>The metric name under the value.</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<StatTile, string?>(nameof(Label));

    /// <summary>An optional third line: a delta, a rank, a comparison. Pre-formatted by the caller.</summary>
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<StatTile, string?>(nameof(Caption));

    /// <summary>Promotes the tile to the strip's lead metric: larger number, stronger surface.</summary>
    public static readonly StyledProperty<bool> IsHeroProperty =
        AvaloniaProperty.Register<StatTile, bool>(nameof(IsHero));

    /// <inheritdoc cref="LabelProperty" />
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <inheritdoc cref="CaptionProperty" />
    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <inheritdoc cref="IsHeroProperty" />
    public bool IsHero
    {
        get => GetValue(IsHeroProperty);
        set => SetValue(IsHeroProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsHeroProperty)
        {
            PseudoClasses.Set(":hero", IsHero);
        }
    }
}
