#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The callouts editor (strat-model.md §3.7): one owner's words over one map's canonical places, edited as
///     a table of alias, place and primary, plus "copy aliases from another owner". A team ships with no
///     aliases and the Valve names; typing "popdog" for "TRamp" here is what makes it resolve everywhere else
///     (the Strat Book's own step table, and any place-typed query built over <see cref="CalloutResolver" />).
///     <para>
///         <b>Every row commits on its own.</b> There is no separate save: a field edit rewrites the whole
///         table under the row's alias key and re-reads it, so what is on screen always matches
///         <c>callouts.json</c>. A refusal (a duplicate alias, an alias to no place) is never written; the
///         row snaps back to disk and <see cref="IssueLine" /> says why.
///     </para>
/// </summary>
public sealed partial class CalloutsEditorViewModel : ObservableObject
{
    private readonly CalloutResolverSource _resolvers;
    private readonly StratStore _store;

    private CalloutResolver _canonical = new([]);
    private string _map = "";
    private StratOwner _owner = StratOwner.Me();

    [ObservableProperty]
    private StratOwnerOption? _copyFrom;

    [ObservableProperty]
    private string _issueLine = "";

    [ObservableProperty]
    private string _newAliasText = "";

    [ObservableProperty]
    private string _newPlaceText = "";

    /// <param name="store">The alias tables.</param>
    /// <param name="resolvers">Builds the canonical-only resolver a typed place is checked against.</param>
    public CalloutsEditorViewModel(StratStore store, CalloutResolverSource resolvers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolvers);
        _store = store;
        _resolvers = resolvers;
    }

    public ObservableCollection<CalloutAliasRow> Aliases { get; } = [];

    /// <summary>Every other book, for the "copy from" picker.</summary>
    public ObservableCollection<StratOwnerOption> CopySources { get; } = [];

    /// <summary>True once a specific owner and map are configured; false for "all maps" or no book.</summary>
    public bool HasMap => _map.Length > 0;

    public string MapLine => HasMap ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_map} callouts for {_owner}") : "";

    public bool HasIssue => IssueLine.Length > 0;

    /// <summary>Points the editor at one owner's map and reloads its table.</summary>
    /// <param name="owner">Whose aliases to show.</param>
    /// <param name="map">The map, in the parser's spelling; empty when no single map is selected.</param>
    /// <param name="allOwners">Every book, for the copy-from list (this one excluded).</param>
    public void Configure(StratOwner owner, string map, IReadOnlyList<StratOwnerOption> allOwners)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(allOwners);
        _owner = owner;
        _map = map;
        _canonical = HasMap ? _resolvers.Canonical(map) : new CalloutResolver([]);

        CopySources.Clear();
        foreach (StratOwnerOption option in allOwners.Where(o => !o.Owner.Equals(owner)))
        {
            CopySources.Add(option);
        }

        CopyFrom = null;
        IssueLine = "";
        Reload();
        OnPropertyChanged(nameof(HasMap));
        OnPropertyChanged(nameof(MapLine));
    }

    /// <summary>The place a typed word means: canonical when it resolves, else the trimmed text (unknown places warn, never refuse).</summary>
    internal string? ResolvePlace(string? text) => string.IsNullOrWhiteSpace(text) ? null : _canonical.Resolve(text) ?? text.Trim();

    /// <summary>A new alias for the place typed beside it.</summary>
    [RelayCommand]
    private void AddAlias()
    {
        if (!HasMap || string.IsNullOrWhiteSpace(NewAliasText))
        {
            return;
        }

        if (ResolvePlace(NewPlaceText) is not { } place)
        {
            IssueLine = "type a place before adding the alias";
            return;
        }

        CalloutTable table = _store.LoadCallouts(_owner, _map);
        table.Aliases.Add(new CalloutAlias { Alias = NewAliasText.Trim(), Place = place });
        if (Persist(table))
        {
            NewAliasText = "";
            NewPlaceText = "";
        }

        Reload();
    }

    internal void RemoveAlias(CalloutAliasRow? row)
    {
        if (row is null || !HasMap)
        {
            return;
        }

        CalloutTable table = _store.LoadCallouts(_owner, _map);
        table.Aliases.RemoveAll(a => SameAlias(a.Alias, row.Alias));
        Persist(table);
        Reload();
    }

    /// <summary>Replaces this owner's table on this map with a copy of another owner's.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopyAliases()
    {
        if (!HasMap || CopyFrom is not { } from)
        {
            return;
        }

        CalloutTable source = _store.LoadCallouts(from.Owner, _map);
        CalloutTable table = new()
        {
            Map = _map,
            Aliases = [.. source.Aliases.Select(a => new CalloutAlias { Alias = a.Alias, Place = a.Place, Primary = a.Primary })]
        };
        Persist(table);
        Reload();
    }

    private bool CanCopy() => CopyFrom is not null;

    partial void OnCopyFromChanged(StratOwnerOption? value) => CopyAliasesCommand.NotifyCanExecuteChanged();

    partial void OnIssueLineChanged(string value) => OnPropertyChanged(nameof(HasIssue));

    // A row's edit rewrites the whole table under its former alias key, so a rename reads as remove-then-add
    // in one write rather than two, and the validator sees the final shape only.
    internal void CommitRow(CalloutAliasRow row, string previousAlias)
    {
        if (!HasMap)
        {
            return;
        }

        CalloutTable table = _store.LoadCallouts(_owner, _map);
        table.Aliases.RemoveAll(a => SameAlias(a.Alias, previousAlias));
        table.Aliases.Add(new CalloutAlias { Alias = row.Alias, Place = row.Place, Primary = row.Primary });
        Persist(table);
        Reload();
    }

    private static bool SameAlias(string a, string b) => string.Equals(CalloutResolver.Fold(a), CalloutResolver.Fold(b), StringComparison.Ordinal);

    // False and IssueLine set to the refusal(s) on a rejected write; the caller reloads either way so the
    // screen always matches disk.
    private bool Persist(CalloutTable table)
    {
        if (_store.SaveCallouts(_owner, table))
        {
            IssueLine = "";
            return true;
        }

        List<StratIssue> refusals = StratValidator.ValidateCallouts(table, _canonical)
            .Where(i => i.Severity == StratIssueSeverity.Refusal).ToList();
        IssueLine = refusals.Count > 0 ? string.Join("; ", refusals.Select(i => i.Message)) : "the alias table could not be saved";
        return false;
    }

    private void Reload()
    {
        CalloutTable table = HasMap ? _store.LoadCallouts(_owner, _map) : new CalloutTable();
        Aliases.Clear();
        foreach (CalloutAlias alias in table.Aliases.OrderBy(a => a.Alias, StringComparer.OrdinalIgnoreCase))
        {
            Aliases.Add(new CalloutAliasRow(this, alias.Alias, alias.Place, alias.Primary));
        }
    }
}

/// <summary>One row: a team's word, the canonical place it names, and whether it is the primary word shown for that place.</summary>
public sealed partial class CalloutAliasRow : ObservableObject
{
    private readonly CalloutsEditorViewModel _owner;
    private string _previousAlias;

    [ObservableProperty]
    private string _alias;

    [ObservableProperty]
    private bool _primary;

    [ObservableProperty]
    private string _place;

    internal CalloutAliasRow(CalloutsEditorViewModel owner, string alias, string place, bool primary)
    {
        _owner = owner;
        _alias = alias;
        _previousAlias = alias;
        _place = place;
        _primary = primary;
    }

    [RelayCommand]
    private void Remove() => _owner.RemoveAlias(this);

    partial void OnAliasChanged(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (!string.Equals(trimmed, value, StringComparison.Ordinal))
        {
            Alias = trimmed;
            return;
        }

        _owner.CommitRow(this, _previousAlias);
        _previousAlias = trimmed;
    }

    partial void OnPlaceChanged(string value)
    {
        string resolved = _owner.ResolvePlace(value) ?? value.Trim();
        if (!string.Equals(resolved, value, StringComparison.Ordinal))
        {
            Place = resolved;
            return;
        }

        _owner.CommitRow(this, _previousAlias);
    }

    partial void OnPrimaryChanged(bool value) => _owner.CommitRow(this, _previousAlias);
}
