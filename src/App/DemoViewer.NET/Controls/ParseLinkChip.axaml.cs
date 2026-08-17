#region

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Reusable clickable code-link chip. Renders a monospace
///     label, an optional parenthetical <see cref="Detail" />, and an optional
///     clickable <see cref="SourceBadge" />, indented by <see cref="Indent" />.
///     <para>
///         Two opening modes:
///     </para>
///     <list type="bullet">
///         <item>
///             Bind a ready-made <see cref="Command" /> (used by the parse-chain
///             rows, whose <c>ParseChainEntry.OpenCommand</c> already encapsulates the
///             VS Code / web-fallback logic).
///         </item>
///         <item>
///             Or supply <see cref="LocalPath" /> (+ optional <see cref="LocalLine" />)
///             and/or <see cref="WebFallback" />; the chip's own click handler opens them
///             via <see cref="OpenExternal" />. Used by future link surfaces (entity class
///             links, field decode errors).
///         </item>
///     </list>
///     When neither a command nor any path is set the chip renders as plain
///     (non-clickable) text.
/// </summary>
public partial class ParseLinkChip : UserControl
{
    /// <summary>Command property.</summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<ParseLinkChip, ICommand?>(nameof(Command));

    /// <summary>Detail property.</summary>
    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<ParseLinkChip, string?>(nameof(Detail));

    /// <summary>Indent property.</summary>
    public static readonly StyledProperty<Thickness> IndentProperty =
        AvaloniaProperty.Register<ParseLinkChip, Thickness>(nameof(Indent));

    // IsClickable surfaced as a direct property so XAML can bind IsVisible to it.
    /// <summary>Is clickable property.</summary>
    public static readonly DirectProperty<ParseLinkChip, bool> IsClickableProperty =
        AvaloniaProperty.RegisterDirect<ParseLinkChip, bool>(nameof(IsClickable), o => o.IsClickable);

    /// <summary>Label property.</summary>
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ParseLinkChip, string>(nameof(Label), "");

    /// <summary>Local line property.</summary>
    public static readonly StyledProperty<int?> LocalLineProperty =
        AvaloniaProperty.Register<ParseLinkChip, int?>(nameof(LocalLine));

    /// <summary>Local path property.</summary>
    public static readonly StyledProperty<string?> LocalPathProperty =
        AvaloniaProperty.Register<ParseLinkChip, string?>(nameof(LocalPath));

    /// <summary>Source badge property.</summary>
    public static readonly StyledProperty<string?> SourceBadgeProperty =
        AvaloniaProperty.Register<ParseLinkChip, string?>(nameof(SourceBadge));

    /// <summary>Tooltip property.</summary>
    public static readonly StyledProperty<string?> TooltipProperty =
        AvaloniaProperty.Register<ParseLinkChip, string?>(nameof(Tooltip));

    /// <summary>Web fallback property.</summary>
    public static readonly StyledProperty<string?> WebFallbackProperty =
        AvaloniaProperty.Register<ParseLinkChip, string?>(nameof(WebFallback));

    /// <summary>Initializes a new <see cref="ParseLinkChip" /> instance.</summary>
    public ParseLinkChip() => InitializeComponent();

    /// <summary>Command.</summary>

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>Detail.</summary>

    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Indent.</summary>

    public Thickness Indent
    {
        get => GetValue(IndentProperty);
        set => SetValue(IndentProperty, value);
    }

    /// <summary>True when the chip has any opening affordance (bound command or a path/url).</summary>
    public bool IsClickable =>
        Command is not null
        || !string.IsNullOrEmpty(LocalPath)
        || !string.IsNullOrEmpty(WebFallback);

    /// <summary>Label.</summary>

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Local line.</summary>

    public int? LocalLine
    {
        get => GetValue(LocalLineProperty);
        set => SetValue(LocalLineProperty, value);
    }

    /// <summary>Local path.</summary>

    public string? LocalPath
    {
        get => GetValue(LocalPathProperty);
        set => SetValue(LocalPathProperty, value);
    }

    /// <summary>Source badge.</summary>

    public string? SourceBadge
    {
        get => GetValue(SourceBadgeProperty);
        set => SetValue(SourceBadgeProperty, value);
    }

    /// <summary>Tooltip.</summary>

    public string? Tooltip
    {
        get => GetValue(TooltipProperty);
        set => SetValue(TooltipProperty, value);
    }

    /// <summary>Web fallback.</summary>

    public string? WebFallback
    {
        get => GetValue(WebFallbackProperty);
        set => SetValue(WebFallbackProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CommandProperty
            || change.Property == LocalPathProperty
            || change.Property == WebFallbackProperty)
        {
            RaisePropertyChanged(IsClickableProperty, default, IsClickable);
        }
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (Command is { } cmd)
        {
            if (cmd.CanExecute(null))
            {
                cmd.Execute(null);
            }

            return;
        }

        if (!string.IsNullOrEmpty(LocalPath))
        {
            OpenExternal.OpenLocalFile(LocalPath, LocalLine);
        }
        else if (!string.IsNullOrEmpty(WebFallback))
        {
            OpenExternal.OpenUri(WebFallback);
        }
    }
}
