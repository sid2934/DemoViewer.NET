#region

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text;
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
///     What a location field does, apart from drawing it: the text it shows for a value, the list it offers for what
///     is typed, the keys that move through that list, and the value a commit stores. A single field holds one
///     location, a multi field (watching) a list. Places are stored canonical and shown by the owner's word; a
///     point-only location shows as its coordinate and is kept while its text is.
/// </summary>
public sealed class PlaceFieldModel
{
    private List<PlaceRef> _value = [];
    private bool _typed;
    private bool _moved;

    /// <param name="isMulti">A list of locations (watching) rather than one.</param>
    public PlaceFieldModel(bool isMulti = false) => IsMulti = isMulti;

    public bool IsMulti { get; }

    /// <summary>The map's callouts; null offers no list and stores typed text as written.</summary>
    public PlaceFieldOptions? Options { get; set; }

    /// <summary>The stored value the field was loaded with.</summary>
    public IReadOnlyList<PlaceRef> Value => _value;

    /// <summary>The field's text.</summary>
    public string Text { get; private set; } = "";

    /// <summary>What the open list shows.</summary>
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

    /// <summary>Whether the value holds a location with a point and no place: the clear button's cue.</summary>
    public bool HasPointOnly => _value.Any(v => !StratLocations.HasPlace(v) && StratLocations.HasPoint(v));

    /// <summary>Raised when the text, the list, the highlight or the open state change.</summary>
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
        Text = DisplayOf(_value, Options?.Resolver);
        Raise();
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

    /// <summary>The user changed the text: the list filters on it (the part after the last comma in a multi field).</summary>
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
    ///     Enter on the open list: the highlighted callout becomes the text (a multi field's last entry), the list
    ///     closes, and the new value is returned for storing. Null when the list is closed or nothing is highlighted.
    /// </summary>
    public IReadOnlyList<PlaceRef>? Accept() => IsOpen && Highlight >= 0 && Highlight < Items.Count ? Choose(Items[Highlight]) : null;

    /// <summary>A click on a list row: as <see cref="Accept" /> for that row.</summary>
    /// <param name="option">The row.</param>
    public IReadOnlyList<PlaceRef> Choose(PlaceOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        Text = IsMulti ? KeepBeforeLast(Text) + option.Display : option.Display;
        IsOpen = false;
        _typed = false;
        List<PlaceRef> value = Parse(Text);
        _value = value;
        Text = DisplayOf(_value, Options?.Resolver);
        Raise();
        return value;
    }

    /// <summary>
    ///     Focus left the field: the text parsed into locations, or null when it still says what is stored. Text that
    ///     reads as a stored entry keeps that entry (a point with it); text that resolves is its canonical place;
    ///     anything else is stored as typed.
    /// </summary>
    public IReadOnlyList<PlaceRef>? Commit()
    {
        IsOpen = false;
        List<PlaceRef> value = Parse(Text);
        bool same = value.Count == _value.Count && value.Zip(_value).All(p => StratLocations.Same(p.First, p.Second));
        _typed = false;
        if (same)
        {
            Text = DisplayOf(_value, Options?.Resolver);
            Raise();
            return null;
        }

        _value = value;
        Text = DisplayOf(_value, Options?.Resolver);
        Raise();
        return value;
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

    /// <summary>Splits on commas outside parentheses, so a coordinate stays one entry.</summary>
    /// <param name="text">A field's text.</param>
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> parts = [];
        StringBuilder part = new();
        int depth = 0;
        foreach (char c in text)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }

            if (c == ',' && depth == 0)
            {
                parts.Add(part.ToString().Trim());
                part.Clear();
            }
            else
            {
                part.Append(c);
            }
        }

        parts.Add(part.ToString().Trim());
        return [.. parts.Where(p => p.Length > 0)];
    }

    private List<PlaceRef> Parse(string text) => Parse(text, _value, Options?.Resolver, IsMulti, CurrentLevelMinZ);

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
    ///     A field's text as locations. Text that reads as a stored entry keeps that entry (a point with it); text that
    ///     resolves is its canonical place; anything else is stored as typed. A single field takes the first entry, and
    ///     a different place there drops the stored point (<see cref="StratLocations.Typed" />).
    /// </summary>
    /// <param name="text">The field's text.</param>
    /// <param name="current">The stored value.</param>
    /// <param name="callouts">The owner's words, or null.</param>
    /// <param name="multi">A list field.</param>
    /// <param name="levelMinZ">The level key a typed coordinate takes when no stored point gives one; null for none.</param>
    public static List<PlaceRef> Parse(string text, IReadOnlyList<PlaceRef> current, CalloutResolver? callouts, bool multi,
        double? levelMinZ = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(current);

        // A single field's whole text may be a bare "x, y", which the comma split would cut in two.
        IReadOnlyList<string> parts = !multi && TryParsePoint(text, out _, out _) ? [text.Trim()] : Split(text);
        List<PlaceRef> entries = [];
        HashSet<int> used = [];
        double? storedLevel = current.FirstOrDefault(StratLocations.HasPoint)?.LevelMinZ;
        foreach (string part in parts)
        {
            // Each stored entry answers one part: two points that print alike stay two points.
            int keptAt = -1;
            for (int k = 0; k < current.Count; k++)
            {
                if (!used.Contains(k) && string.Equals(DisplayOf([current[k]], callouts), part, StringComparison.Ordinal))
                {
                    keptAt = k;
                    break;
                }
            }

            PlaceRef? entry;
            if (keptAt >= 0)
            {
                used.Add(keptAt);
                entry = StratLocations.Clone(current[keptAt]);
            }
            else if (TryParsePoint(part, out double x, out double y))
            {
                PlaceRef stored = !multi && current.Count > 0 ? current[0] : new PlaceRef();
                entry = StratLocations.Clone(stored);
                entry.Place = null;
                entry.X = x;
                entry.Y = y;
                entry.LevelMinZ = stored.LevelMinZ ?? storedLevel ?? levelMinZ;
            }
            else if (NumberOnly.IsMatch(part))
            {
                entry = null; // numbers that are not a point are never a place
            }
            else
            {
                string place = callouts?.Resolve(part) ?? part;
                entry = multi ? new PlaceRef { Place = place } : StratLocations.Typed(current.Count > 0 ? current[0] : null, place);
            }

            if (entry is not null && !entries.Any(e => SameEntry(e, entry)))
            {
                entries.Add(entry);
            }

            if (!multi)
            {
                break;
            }
        }

        return entries;
    }

    // One entry per place, and one per point by its coordinates, not by how it prints.
    private static bool SameEntry(PlaceRef a, PlaceRef b) =>
        StratLocations.HasPlace(a) || StratLocations.HasPlace(b)
            ? string.Equals(a.Place, b.Place, StringComparison.Ordinal)
            : a.X == b.X && a.Y == b.Y;

    private void Refilter()
    {
        if (Options is null)
        {
            return;
        }

        string query = !_typed ? "" : IsMulti ? LastPart(Text) : Text;
        Items = Options.Filter(query);
        string? current = !_typed && !IsMulti && _value.Count > 0 ? _value[0].Place : null;
        int index = current is null ? -1 : Items.ToList().FindIndex(o => string.Equals(o.Place, current, StringComparison.Ordinal));
        Highlight = index >= 0 ? index : _typed && Items.Count > 0 ? 0 : -1;
        Raise();
    }

    private static string LastPart(string text)
    {
        IReadOnlyList<string> parts = Split(text);
        return text.TrimEnd().EndsWith(',') || parts.Count == 0 ? "" : parts[^1];
    }

    // The text up to and including the last top-level comma, then a space: where a picked callout goes.
    private static string KeepBeforeLast(string text)
    {
        IReadOnlyList<string> parts = Split(text);
        List<string> keep = [.. text.TrimEnd().EndsWith(',') ? parts : parts.Take(Math.Max(0, parts.Count - 1))];
        return keep.Count == 0 ? "" : string.Join(", ", keep) + ", ";
    }

    private void Raise() => Changed?.Invoke();
}
