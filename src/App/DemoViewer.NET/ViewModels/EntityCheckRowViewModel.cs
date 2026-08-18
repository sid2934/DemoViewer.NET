#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Building;

#endregion

namespace DemoViewer.NET.ViewModels;

/// <summary>
///     One row of the scope-aware breakpoint editor — a subject's per-player provider value compared to a
///     literal (<c>victim · health · &lt; · 20</c>). The subject and provider are chosen from dropdowns
///     (canonical token behind a friendly label), so the user never types the entity grammar; the host
///     composes the rows into the canonical condition string via <see cref="StructuredCondition.Compose" />.
/// </summary>
public sealed partial class EntityCheckRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Action<EntityCheckRowViewModel> _onRemove;

    [ObservableProperty]
    private string _op;

    [ObservableProperty]
    private Choice? _provider;

    [ObservableProperty]
    private Choice? _subject;

    [ObservableProperty]
    private string _value;

    /// <summary>An inline, non-blocking note for this row (e.g. "pick a player in the filter"); <c>null</c> = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warning;

    public EntityCheckRowViewModel(
        IReadOnlyList<Choice> subjects,
        IReadOnlyList<Choice> providers,
        Action onChanged,
        Action<EntityCheckRowViewModel> onRemove,
        Choice? subject = null,
        Choice? provider = null,
        string? op = null,
        string value = "")
    {
        AvailableSubjects = subjects;
        AvailableProviders = providers;
        _onChanged = onChanged;
        _onRemove = onRemove;
        _subject = subject ?? (subjects.Count > 0 ? subjects[0] : null);
        _provider = provider ?? (providers.Count > 0 ? providers[0] : null);
        _op = op is not null && AvailableOps.Contains(op) ? op : AvailableOps.Count > 0 ? AvailableOps[0] : "<";
        _value = value;
    }

    /// <summary>The subjects in scope — the trigger event's <c>*Slot</c> players plus the selected player.</summary>
    public IReadOnlyList<Choice> AvailableSubjects { get; }

    /// <summary>The per-player providers (health / armor / equipment value / active weapon).</summary>
    public IReadOnlyList<Choice> AvailableProviders { get; }

    /// <summary>The comparison operators valid for the selected provider (a text provider allows only equality).</summary>
    public IReadOnlyList<string> AvailableOps =>
        Provider?.IsText == true ? StructuredCondition.TextOps : StructuredCondition.NumericOps;

    /// <summary>Whether this row has an inline warning to show.</summary>
    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    /// <summary>
    ///     The structured value of this row, or <c>null</c> when it isn't fully filled in yet — including a
    ///     blank value, so a freshly-added row contributes nothing (no dangling <c>… &lt;</c>) until typed.
    /// </summary>
    public EntityCheckRow? ToRow() =>
        Subject is null || Provider is null || string.IsNullOrWhiteSpace(Op) || string.IsNullOrWhiteSpace(Value)
            ? null
            : new EntityCheckRow(Subject.Token, Provider.Token, Op, Value.Trim());

    partial void OnSubjectChanged(Choice? value) => _onChanged();

    partial void OnProviderChanged(Choice? value)
    {
        OnPropertyChanged(nameof(AvailableOps));
        if (!AvailableOps.Contains(Op))
        {
            Op = AvailableOps.Count > 0 ? AvailableOps[0] : Op; // keep Op valid for the new provider's type
        }

        _onChanged();
    }

    partial void OnOpChanged(string value) => _onChanged();

    partial void OnValueChanged(string value) => _onChanged();

    [RelayCommand]
    private void Remove() => _onRemove(this);

    /// <summary>
    ///     A dropdown choice: the friendly <see cref="Label" /> shown, the canonical <see cref="Token" />
    ///     used in the composed expression. <see cref="IsText" /> marks a string-valued provider.
    /// </summary>
    public sealed record Choice(string Label, string Token, bool IsText = false)
    {
        /// <summary>The dropdown shows the friendly label (so no item template is needed).</summary>
        public override string ToString() => Label;
    }
}
