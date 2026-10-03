#region

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>One callout a location list offers: the canonical place stored, the owner's word shown.</summary>
/// <param name="Place">The canonical place.</param>
/// <param name="Display">The owner's primary alias, else the canonical name split into words.</param>
/// <param name="Keys">Folded names it matches on: the display, the canonical name and every alias.</param>
public sealed record PlaceOption(string Place, string Display, IReadOnlyList<string> Keys)
{
    public override string ToString() => Display;
}

/// <summary>
///     A map's callouts for location lists, built once per <see cref="CalloutResolver" /> and shared by every field
///     that shows them. Filtering is a pass over this cached list; nothing reads a file.
/// </summary>
public sealed class PlaceFieldOptions
{
    private static readonly ConditionalWeakTable<CalloutResolver, PlaceFieldOptions> Cache = new();

    private PlaceFieldOptions(CalloutResolver resolver)
    {
        Resolver = resolver;
        Dictionary<string, List<string>> aliases = new(StringComparer.Ordinal);
        foreach ((string alias, string place) in resolver.Aliases)
        {
            if (!aliases.TryGetValue(place, out List<string>? list))
            {
                aliases[place] = list = [];
            }

            list.Add(CalloutResolver.Fold(alias));
        }

        All =
        [
            .. resolver.CanonicalNames
                .Select(p => new PlaceOption(p, resolver.Display(p),
                    [CalloutResolver.Fold(resolver.Display(p)), CalloutResolver.Fold(p), .. aliases.GetValueOrDefault(p) ?? []]))
                .OrderBy(o => o.Display, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>The resolver the options came from: typed text resolves through it.</summary>
    public CalloutResolver Resolver { get; }

    /// <summary>Every callout, by display name.</summary>
    public IReadOnlyList<PlaceOption> All { get; }

    /// <summary>The options for a resolver, built on first use and kept while the resolver lives.</summary>
    /// <param name="resolver">The owner's callouts over the map's places.</param>
    public static PlaceFieldOptions For(CalloutResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return Cache.GetValue(resolver, r => new PlaceFieldOptions(r));
    }

    /// <summary>
    ///     The options matching typed text: a name equal to it first (so Enter and a blur store the same place), then
    ///     names starting with it, then names containing it. All for none.
    /// </summary>
    /// <param name="query">What was typed.</param>
    public IReadOnlyList<PlaceOption> Filter(string? query)
    {
        string key = CalloutResolver.Fold(query ?? "");
        if (key.Length == 0)
        {
            return All;
        }

        List<PlaceOption> exact = [], starts = [], contains = [];
        foreach (PlaceOption option in All)
        {
            if (option.Keys.Any(k => string.Equals(k, key, StringComparison.Ordinal)))
            {
                exact.Add(option);
            }
            else if (option.Keys.Any(k => k.StartsWith(key, StringComparison.Ordinal)))
            {
                starts.Add(option);
            }
            else if (option.Keys.Any(k => k.Contains(key, StringComparison.Ordinal)))
            {
                contains.Add(option);
            }
        }

        return [.. exact, .. starts, .. contains];
    }
}

/// <summary>
///     What a location field does, apart from drawing it: the text it shows, the list it offers for what is typed,
///     the keys that move through that list, and the value a commit stores. A single field's text is its one
///     location; a point-only location shows as its coordinate and is kept while its text is. A multi field holds a
///     list, drawn as chips, and its text is the add field: a commit or a pick appends one entry, and the chips are
///     removed and moved one at a time. Places are stored canonical and shown by the owner's word.
/// </summary>
public sealed class PlaceFieldModel
{
    private List<PlaceRef> _value = [];
    private bool _typed;
    private bool _moved;

    /// <param name="isMulti">A list of locations (watching, via, lurk areas) rather than one.</param>
    public PlaceFieldModel(bool isMulti = false) => IsMulti = isMulti;

    public bool IsMulti { get; }

    /// <summary>The map's callouts; null offers no list and stores typed text as written.</summary>
    public PlaceFieldOptions? Options { get; set; }

    /// <summary>The value as the field holds it: as loaded, or as its last edit left it.</summary>
    public IReadOnlyList<PlaceRef> Value => _value;

    /// <summary>The field's text: a single field's location, or the entry a multi field is adding.</summary>
    public string Text { get; private set; } = "";

    /// <summary>What the open list shows. A multi field leaves out the callouts it already holds.</summary>
    public IReadOnlyList<PlaceOption> Items { get; private set; } = [];

    /// <summary>The list row Enter picks, or -1.</summary>
    public int Highlight { get; private set; } = -1;

    public bool IsOpen { get; private set; }

    /// <summary>
    ///     Whether the user has typed or moved the highlight since the list opened: only then does Enter pick from it.
    ///     Otherwise Enter is the host's, as it is with no list.
    /// </summary>
    public bool HasChoice => _typed || _moved;

    /// <summary>Whether the text holds an edit not yet committed: a value pushed from outside then waits.</summary>
    public bool HasPendingEdit => _typed;

    /// <summary>The level key a typed coordinate takes when the field holds no point to take it from; null for none.</summary>
    public double? CurrentLevelMinZ { get; set; }

    /// <summary>Whether a single field holds a point and no place: the clear button's cue.</summary>
    public bool HasPointOnly => !IsMulti && _value.Any(v => !StratLocations.HasPlace(v) && StratLocations.HasPoint(v));

    /// <summary>Raised when the value, the text, the list, the highlight or the open state change.</summary>
    public event Action? Changed;

    /// <summary>
    ///     What a value reads as: each location's callout, a place the map lacks as stored, or a coordinate, comma
    ///     separated.
    /// </summary>
    /// <param name="value">Locations.</param>
    /// <param name="callouts">The owner's words, or null.</param>
    public static string DisplayOf(IReadOnlyList<PlaceRef> value, CalloutResolver? callouts)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Join(", ", value.Select(v => StratLocations.HasPlace(v)
            ? callouts is not null && callouts.IsCanonical(v.Place) ? callouts.Display(v.Place!) : v.Place!
            : StratLocations.Text(v, callouts)).OfType<string>());
    }

    /// <summary>Shows a stored value; drops whatever was typed.</summary>
    /// <param name="value">The field's locations.</param>
    public void Load(IReadOnlyList<PlaceRef>? value)
    {
        _value = [.. (value ?? []).Where(StratLocations.IsSet)];
        _typed = false;
        Text = IsMulti ? "" : DisplayOf(_value, Options?.Resolver);
        Raise();
    }

    /// <summary>A multi field's list pushed from outside (a map pick, an undo): the chips follow, the add text stays.</summary>
    /// <param name="value">The field's locations.</param>
    public void Reload(IReadOnlyList<PlaceRef>? value)
    {
        _value = [.. (value ?? []).Where(StratLocations.IsSet)];
        if (IsOpen)
        {
            Refilter();
        }
        else
        {
            Raise();
        }
    }

    /// <summary>Opens the list: every callout until something is typed, the current one highlighted.</summary>
    /// <param name="byKey">Opened with Down: Enter then picks the highlighted callout.</param>
    public void Open(bool byKey = false)
    {
        if (Options is null)
        {
            return;
        }

        IsOpen = true;
        _moved = byKey;
        Refilter();
    }

    /// <summary>Closes the list. True when it was open, so the key that closed it is consumed.</summary>
    public bool Dismiss()
    {
        if (!IsOpen)
        {
            return false;
        }

        IsOpen = false;
        Raise();
        return true;
    }

    /// <summary>The user changed the text: the list filters on it.</summary>
    /// <param name="text">The new text.</param>
    public void Type(string? text)
    {
        Text = text ?? "";
        _typed = true;
        if (Options is not null)
        {
            IsOpen = true;
            Refilter();
        }
        else
        {
            Raise();
        }
    }

    /// <summary>Moves the highlight, stopping at either end. True when the list is open, so the key is consumed.</summary>
    /// <param name="delta">-1 for up, 1 for down.</param>
    public bool Move(int delta)
    {
        if (!IsOpen || Items.Count == 0)
        {
            return IsOpen;
        }

        _moved = true;
        Highlight = Math.Clamp(Highlight < 0 ? (delta > 0 ? 0 : Items.Count - 1) : Highlight + delta, 0, Items.Count - 1);
        Raise();
        return true;
    }

    /// <summary>
    ///     Enter on the open list: the highlighted callout is stored (a multi field appends it), the list closes, and
    ///     the new value is returned. Null when the list is closed or nothing is highlighted.
    /// </summary>
    public IReadOnlyList<PlaceRef>? Accept() => IsOpen && Highlight >= 0 && Highlight < Items.Count ? Choose(Items[Highlight]) : null;

    /// <summary>A click on a list row: as <see cref="Accept" /> for that row.</summary>
    /// <param name="option">The row.</param>
    public IReadOnlyList<PlaceRef> Choose(PlaceOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        IsOpen = false;
        _typed = false;
        if (IsMulti)
        {
            Text = "";
            Append(new PlaceRef { Place = option.Place });
            Raise();
            return _value;
        }

        _value = Parse(option.Display);
        Text = DisplayOf(_value, Options?.Resolver);
        Raise();
        return _value;
    }

    /// <summary>
    ///     Focus left the field, or Enter with no list. A single field: the text as its location, or null when it still
    ///     says what is stored. A multi field: the typed entry appended and the text cleared, or null when nothing was
    ///     typed or the entry is already there.
    /// </summary>
    public IReadOnlyList<PlaceRef>? Commit()
    {
        IsOpen = false;
        if (IsMulti)
        {
            PlaceRef? entry = _typed ? ParseEntry(Text, _value, Options?.Resolver, CurrentLevelMinZ) : null;
            Text = "";
            _typed = false;
            bool added = entry is not null && Append(entry);
            Raise();
            return added ? _value : null;
        }

        List<PlaceRef> value = Parse(Text);
        bool same = value.Count == _value.Count && value.Zip(_value).All(p => StratLocations.Same(p.First, p.Second));
        _typed = false;
        if (!same)
        {
            _value = value;
        }

        Text = DisplayOf(_value, Options?.Resolver);
        Raise();
        return same ? null : value;
    }

    /// <summary>The clear button: an empty value to store.</summary>
    public IReadOnlyList<PlaceRef> Clear()
    {
        _value = [];
        Text = "";
        _typed = false;
        IsOpen = false;
        Raise();
        return [];
    }

    /// <summary>A multi field's chip removed: the list without it.</summary>
    /// <param name="index">The chip.</param>
    public IReadOnlyList<PlaceRef> Remove(int index)
    {
        if (index >= 0 && index < _value.Count)
        {
            _value = [.. _value.Where((_, i) => i != index)];
            Raise();
        }

        return _value;
    }

    /// <summary>
    ///     A multi field's chip moved one place, or null when it cannot go there: past either end, or between the
    ///     places and the points, since a list is stored as its places and then its points.
    /// </summary>
    /// <param name="index">The chip.</param>
    /// <param name="delta">-1 for left, 1 for right.</param>
    public IReadOnlyList<PlaceRef>? Shift(int index, int delta)
    {
        if (!CanShift(index, delta))
        {
            return null;
        }

        List<PlaceRef> moved = [.. _value];
        (moved[index], moved[index + delta]) = (moved[index + delta], moved[index]);
        _value = moved;
        Raise();
        return _value;
    }

    /// <summary>Whether <see cref="Shift" /> would move the chip.</summary>
    /// <param name="index">The chip.</param>
    /// <param name="delta">-1 for left, 1 for right.</param>
    public bool CanShift(int index, int delta)
    {
        int to = index + delta;
        return index >= 0 && index < _value.Count && to >= 0 && to < _value.Count
               && StratLocations.HasPlace(_value[index]) == StratLocations.HasPlace(_value[to]);
    }

    // "(1234, -560)", "1234, -560", "1234 -560": two numbers, optional parentheses, a comma or spaces between.
    private static readonly Regex PointPattern = new(
        @"^\(?\s*(-?\d+(?:\.\d+)?)\s*(?:,\s*|\s+)(-?\d+(?:\.\d+)?)\s*\)?$", RegexOptions.CultureInvariant);

    private static readonly Regex NumberOnly = new(@"^[\s()\-+.,\d]+$", RegexOptions.CultureInvariant);

    /// <summary>Typed text as a world point: two numbers, with optional parentheses and a comma or spaces between.</summary>
    /// <param name="text">The text.</param>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y.</param>
    public static bool TryParsePoint(string text, out double x, out double y)
    {
        ArgumentNullException.ThrowIfNull(text);
        Match match = PointPattern.Match(text.Trim());
        x = y = 0;
        return match.Success
               && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
               && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

    /// <summary>
    ///     A single field's text as its location. Text that reads as the stored entry keeps it, point and all; a
    ///     coordinate is a point; text that resolves is its canonical place; anything else is stored as typed. A
    ///     different place drops the stored point (<see cref="StratLocations.Typed" />).
    /// </summary>
    /// <param name="text">The field's text.</param>
    /// <param name="current">The stored value.</param>
    /// <param name="callouts">The owner's words, or null.</param>
    /// <param name="levelMinZ">The level key a typed coordinate takes when no stored point gives one; null for none.</param>
    public static List<PlaceRef> Parse(string text, IReadOnlyList<PlaceRef> current, CalloutResolver? callouts, double? levelMinZ = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(current);
        // A bare "x, y" is one point; otherwise only the text before a comma outside parentheses counts.
        string part = TryParsePoint(text, out _, out _) ? text.Trim() : BeforeComma(text).Trim();
        PlaceRef? stored = current.Count > 0 ? current[0] : null;
        if (part.Length == 0)
        {
            return [];
        }

        if (stored is not null && string.Equals(DisplayOf([stored], callouts), part, StringComparison.Ordinal))
        {
            return [StratLocations.Clone(stored)];
        }

        if (TryParsePoint(part, out double x, out double y))
        {
            PlaceRef point = StratLocations.Clone(stored ?? new PlaceRef());
            point.Place = null;
            point.X = x;
            point.Y = y;
            point.LevelMinZ = stored?.LevelMinZ ?? levelMinZ;
            return [point];
        }

        // Numbers that are not a point are never a place.
        return !NumberOnly.IsMatch(part) && StratLocations.Typed(stored, callouts?.Resolve(part) ?? part) is { } typed ? [typed] : [];
    }

    /// <summary>
    ///     A multi field's typed entry: a coordinate is a point at the level of the list's first point, else
    ///     <paramref name="levelMinZ" />; a name is the callout it resolves to, else the text as typed. Null for
    ///     nothing, or numbers that are not a point.
    /// </summary>
    /// <param name="text">What was typed.</param>
    /// <param name="current">The list it joins.</param>
    /// <param name="callouts">The owner's words, or null.</param>
    /// <param name="levelMinZ">The level key a coordinate takes when the list holds no point; null for none.</param>
    public static PlaceRef? ParseEntry(string text, IReadOnlyList<PlaceRef> current, CalloutResolver? callouts, double? levelMinZ = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(current);
        string part = text.Trim();
        if (TryParsePoint(part, out double x, out double y))
        {
            return new PlaceRef { X = x, Y = y, LevelMinZ = current.FirstOrDefault(StratLocations.HasPoint)?.LevelMinZ ?? levelMinZ };
        }

        return part.Length == 0 || NumberOnly.IsMatch(part) ? null : new PlaceRef { Place = callouts?.Resolve(part) ?? part };
    }

    private static string BeforeComma(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ',' when depth == 0:
                    return text[..i];
            }
        }

        return text;
    }

    private List<PlaceRef> Parse(string text) => Parse(text, _value, Options?.Resolver, CurrentLevelMinZ);

    // A duplicate is refused quietly. The new list is only the model's until the field stores it through Value.
    private bool Append(PlaceRef entry)
    {
        if (_value.Any(e => StratLocationPatches.SameEntry(e, entry)))
        {
            return false;
        }

        _value = [.. _value, entry];
        return true;
    }

    private bool IsChosen(string? place) =>
        place is not null && _value.Any(v => string.Equals(v.Place, place, StringComparison.Ordinal));

    private void Refilter()
    {
        if (Options is null)
        {
            return;
        }

        IReadOnlyList<PlaceOption> items = Options.Filter(_typed ? Text : "");
        Items = IsMulti ? [.. items.Where(o => !IsChosen(o.Place))] : items;
        string? current = !_typed && !IsMulti && _value.Count > 0 ? _value[0].Place : null;
        int index = current is null ? -1 : Items.ToList().FindIndex(o => string.Equals(o.Place, current, StringComparison.Ordinal));

        // Text naming a callout already chosen highlights nothing, so Enter refuses it instead of adding a neighbour.
        bool namesChosen = IsMulti && _typed && IsChosen(Options.Resolver.Resolve(Text));
        Highlight = index >= 0 ? index : _typed && Items.Count > 0 && !namesChosen ? 0 : -1;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
