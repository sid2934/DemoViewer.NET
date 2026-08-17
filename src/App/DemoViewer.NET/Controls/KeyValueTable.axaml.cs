#region

using Avalonia;
using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Generic two-column key/value grid. Renders <see cref="Rows" /> as a
///     virtualized list of key | value rows; changed rows ("delta") show their
///     previous value struck-through before the current value and are tinted.
///     <para>
///         <see cref="ShowDeltaOnly" /> filters the visible rows to those that changed.
///         Filtering happens inside the control so consumers can feed the full row set
///         and bind a single toggle.
///     </para>
///     Consumed by the Entity-tracking tab's field view (with the "Δ only" toggle).
/// </summary>
public partial class KeyValueTable : UserControl
{
    /// <summary>Rows property.</summary>
    public static readonly StyledProperty<IReadOnlyList<KvpRow>?> RowsProperty =
        AvaloniaProperty.Register<KeyValueTable, IReadOnlyList<KvpRow>?>(nameof(Rows));

    /// <summary>Show delta only property.</summary>
    public static readonly StyledProperty<bool> ShowDeltaOnlyProperty =
        AvaloniaProperty.Register<KeyValueTable, bool>(nameof(ShowDeltaOnly));

    /// <summary>Visible rows property.</summary>
    public static readonly DirectProperty<KeyValueTable, IReadOnlyList<KvpRow>> VisibleRowsProperty =
        AvaloniaProperty.RegisterDirect<KeyValueTable, IReadOnlyList<KvpRow>>(nameof(VisibleRows), o => o.VisibleRows);

    /// <summary>Initializes a new <see cref="KeyValueTable" /> instance.</summary>
    public KeyValueTable() => InitializeComponent();

    /// <summary>Rows.</summary>

    public IReadOnlyList<KvpRow>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>Show delta only.</summary>

    public bool ShowDeltaOnly
    {
        get => GetValue(ShowDeltaOnlyProperty);
        set => SetValue(ShowDeltaOnlyProperty, value);
    }

    /// <summary>The rows currently shown — <see cref="Rows" /> filtered by <see cref="ShowDeltaOnly" />.</summary>
    public IReadOnlyList<KvpRow> VisibleRows =>
        Rows is null
            ? []
            : ShowDeltaOnly
                ? Rows.Where(r => r.IsDelta).ToList()
                : Rows;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty || change.Property == ShowDeltaOnlyProperty)
        {
            RaisePropertyChanged(VisibleRowsProperty, Array.Empty<KvpRow>(), VisibleRows);
        }
    }
}

/// <summary>One row in a <see cref="KeyValueTable" />.</summary>
public sealed record KvpRow(string Key, string Value, bool IsDelta, string? PreviousValue);
