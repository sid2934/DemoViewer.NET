#region

using System.Reflection;
using System.Text.Json;
using Avalonia.Input;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Services.RoundFacts;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Services.Tags;

/// <summary>
///     The tag palettes on offer (tag-store.md §3.4), loaded the way themes are
///     (<c>ThemeRegistry</c>): the built-in default from an embedded resource at construction, so a first
///     run can tag and a test is hermetic, and user drop-ins from <c>&lt;config&gt;/palettes/</c> on
///     <see cref="Reload" />, scanned in filename order. A file that fails validation is skipped with a
///     diagnostics-log line and the rest still load; a user file may not take a built-in's id.
///     <para>
///         Palettes are never written by the app. The user edits the file and reloads; that is also why
///         the validator's refusals are strict: a refused file costs the author one edit, while a
///         palette that silently could not fire a key costs a tagging session.
///     </para>
/// </summary>
public sealed class TagPaletteStore
{
    /// <summary>The drop-in extension. A plain <c>*.json</c> would pick up anything a user parks there.</summary>
    public const string FileExtension = ".tagpalette.json";

    /// <summary>The shipped palette's id: the settings row's default.</summary>
    public const string DefaultId = "cs2-default";

    private static ILogger? _diagLog;

    private readonly List<TagPaletteDefinition> _builtIn = [];
    private readonly List<string> _diagnostics = [];
    private readonly string? _directory;
    private readonly IReadOnlyCollection<string> _reservedGroups;
    private List<TagPaletteDefinition> _user = [];

    /// <summary>Creates the store with the built-in palette loaded and no drop-ins yet.</summary>
    /// <param name="directory">The drop-in directory, or null (the browser, tests) for built-ins only.</param>
    /// <param name="reservedGroups">
    ///     Label groups a palette may not write. Null reads the default: Round Facts' fact vocabulary plus
    ///     the reserved strat groups (<see cref="TagPaletteValidator.DefaultReservedGroups" />).
    /// </param>
    public TagPaletteStore(string? directory, IReadOnlyCollection<string>? reservedGroups = null)
    {
        _directory = directory;
        _reservedGroups = reservedGroups ?? TagPaletteValidator.DefaultReservedGroups;
        LoadBuiltIn();
    }

    private static ILogger Log => _diagLog ??= DiagnosticsLog.CreateLogger("App.Tags");

    /// <summary>Every palette on offer: built-ins first, then drop-ins in filename order.</summary>
    public IReadOnlyList<TagPaletteDefinition> Palettes => [.. _builtIn, .. _user];

    /// <summary>The shipped palette; always present unless the build lost its resource.</summary>
    public TagPaletteDefinition Default =>
        _builtIn.FirstOrDefault(p => string.Equals(p.Id, DefaultId, StringComparison.Ordinal))
        ?? _builtIn.FirstOrDefault()
        ?? new TagPaletteDefinition { Id = DefaultId, Name = "Empty" };

    /// <summary>One line per file skipped or warned about at the last load, newest scan only.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics.ToList();

    /// <summary>Raised after <see cref="Reload" /> re-scans the directory.</summary>
    public event Action? Reloaded;

    /// <summary>The palette with this id, else <see cref="Default" />.</summary>
    /// <param name="id">A palette id, from settings or a tag document.</param>
    public TagPaletteDefinition Resolve(string? id) =>
        Palettes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal)) ?? Default;

    /// <summary>
    ///     Re-scans the drop-in directory: previously loaded drop-ins are dropped, the current files loaded
    ///     (invalid ones skipped with a log line), then <see cref="Reloaded" /> fires. No-op on a null directory.
    /// </summary>
    public void Reload()
    {
        _diagnostics.RemoveAll(d => !d.StartsWith("built-in ", StringComparison.Ordinal));
        _user = LoadUser();
        Reloaded?.Invoke();
    }

    /// <summary>Parses one palette file's text, or null with the reason when it is not JSON of the right shape.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="error">Why it could not be read, or "".</param>
    public static TagPaletteDefinition? TryParse(string json, out string error)
    {
        try
        {
            TagPaletteDefinition? palette = JsonSerializer.Deserialize(json, TagPaletteJsonContext.Default.TagPaletteDefinition);
            error = palette is null ? "the file is empty" : "";
            return palette;
        }
        catch (JsonException e)
        {
            error = "not a palette: " + e.Message;
            return null;
        }
    }

    private void LoadBuiltIn()
    {
        Assembly assembly = typeof(TagPaletteStore).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                using Stream? stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                using StreamReader reader = new(stream);
                if (Accept(reader.ReadToEnd(), "built-in " + resource) is { } palette)
                {
                    _builtIn.Add(palette);
                }
            }
            catch (IOException)
            {
                // A shipped palette that fails to read is not offered; the empty default stands in.
            }
        }
    }

    private List<TagPaletteDefinition> LoadUser()
    {
        List<TagPaletteDefinition> loaded = [];
        if (_directory is null || !Directory.Exists(_directory))
        {
            return loaded;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "*" + FileExtension);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return loaded;
        }

        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Skip(Path.GetFileName(file), e.Message);
                continue;
            }

            if (Accept(json, Path.GetFileName(file)) is not { } palette)
            {
                continue;
            }

            if (_builtIn.Any(b => string.Equals(b.Id, palette.Id, StringComparison.Ordinal)))
            {
                Skip(Path.GetFileName(file), $"id '{palette.Id}' belongs to a built-in palette");
                continue;
            }

            // A later file with an earlier file's id replaces it, the themes' last-write-wins.
            loaded.RemoveAll(p => string.Equals(p.Id, palette.Id, StringComparison.Ordinal));
            loaded.Add(palette);
        }

        return loaded;
    }

    private TagPaletteDefinition? Accept(string json, string source)
    {
        TagPaletteDefinition? palette = TryParse(json, out string error);
        if (palette is null)
        {
            Skip(source, error);
            return null;
        }

        TagPaletteValidation result = TagPaletteValidator.Validate(palette, _reservedGroups);
        if (!result.IsValid)
        {
            Skip(source, string.Join("; ", result.Errors));
            return null;
        }

        foreach (string warning in result.Warnings)
        {
            _diagnostics.Add($"{source}: {warning}");
            TagPaletteLog.Warned(Log, source, warning);
        }

        return palette;
    }

    private void Skip(string source, string reason)
    {
        _diagnostics.Add($"{source}: skipped: {reason}");
        TagPaletteLog.Skipped(Log, source, reason);
    }
}

/// <summary>What <see cref="TagPaletteValidator.Validate" /> found: refusals, and warnings that still load.</summary>
/// <param name="Errors">Why the palette is refused; empty when it loads.</param>
/// <param name="Warnings">What it loads with, such as a shadowed transport key.</param>
public sealed record TagPaletteValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
///     The palette rules of tag-store.md §3.4 with overview corrections 21 and 23.
///     <para>
///         <b>Refused:</b> a hotkey the shell or the browser takes before the tab sees it, a hotkey on a
///         palette-scoped keymap row (Esc must always step back out), a duplicate hotkey within one panel,
///         a label group that Round Facts writes as a fact or that the Strat Model reserves, a <c>then</c>
///         that names no labels panel or loops, and the structural mistakes (no id, no panels, a code button
///         with no code, negative lead or lag).
///     </para>
///     <para>
///         <b>Warned:</b> a hotkey that shadows a tab binding while the palette has focus. Shadowing is the
///         author's choice (the palette scope wins), and the warning names the key and what it shadows.
///     </para>
/// </summary>
public static class TagPaletteValidator
{
    /// <summary>The human label groups the Strat Model reserves (overview correction 23).</summary>
    public static readonly IReadOnlyList<string> StratGroups =
        [TagStore.StratGroup, "strat.rev", "strat.result", "strat.failure"];

    // Claimed by suggested-tags.md §3.6 for the proposal queue and not in the shipped table yet, so the
    // table cannot name them; listed here so a palette that shadows them is told now, not when they land.
    private static readonly (Key Key, KeyModifiers Modifiers, string What)[] _claimed =
    [
        (Key.Y, KeyModifiers.None, "accept a suggested tag"),
        (Key.N, KeyModifiers.None, "reject a suggested tag"),
        (Key.Enter, KeyModifiers.None, "edit a suggested tag"),
        (Key.Y, KeyModifiers.Control, "accept every suggested tag")
    ];

    /// <summary>
    ///     Round Facts' fact names (overview correction 10) plus <see cref="StratGroups" />. The fact names
    ///     are read from <see cref="RoundFactsSource.Labels" /> over a round with every optional fact
    ///     present, so a fact Round Facts adds is refused here without a second list to keep in step.
    /// </summary>
    public static IReadOnlyCollection<string> DefaultReservedGroups { get; } = BuildReservedGroups();

    /// <summary>Checks a palette. Never throws.</summary>
    /// <param name="palette">The parsed palette.</param>
    /// <param name="reservedGroups">Label groups a palette may not write.</param>
    public static TagPaletteValidation Validate(TagPaletteDefinition palette, IReadOnlyCollection<string> reservedGroups)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(reservedGroups);
        List<string> errors = [];
        List<string> warnings = [];

        if (string.IsNullOrWhiteSpace(palette.Id))
        {
            errors.Add("the palette has no id");
        }

        if (palette.Panels.Count == 0)
        {
            errors.Add("the palette has no panels");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (TagPalettePanel panel in palette.Panels)
        {
            if (string.IsNullOrWhiteSpace(panel.Id))
            {
                errors.Add("a panel has no id");
            }
            else if (!ids.Add(panel.Id))
            {
                errors.Add($"panel id '{panel.Id}' is used twice");
            }

            if (panel.Kind is not null && panel.Kind != TagPalettePanelKinds.Codes && !panel.IsLabels)
            {
                errors.Add($"panel '{panel.Id}' has kind '{panel.Kind}'; a panel is 'codes' or 'labels'");
            }
        }

        if (palette.Root is { IsLabels: true } root)
        {
            errors.Add($"the entry panel '{root.Id}' is a labels panel; tagging starts with a code");
        }

        foreach (string group in palette.StickyGroups)
        {
            CheckGroup(group, "sticky group", reservedGroups, errors);
        }

        foreach (TagPalettePanel panel in palette.Panels)
        {
            CheckPanel(palette, panel, reservedGroups, errors, warnings);
        }

        return new TagPaletteValidation(errors, warnings);
    }

    private static void CheckPanel(TagPaletteDefinition palette, TagPalettePanel panel,
        IReadOnlyCollection<string> reservedGroups, List<string> errors, List<string> warnings)
    {
        if (panel.IsLabels)
        {
            if (panel.Group is null)
            {
                errors.Add($"labels panel '{panel.Id}' names no group");
            }
            else
            {
                CheckGroup(panel.Group, $"labels panel '{panel.Id}'", reservedGroups, errors);
            }

            CheckFlow(palette, panel.Id, panel.Then, errors);
        }
        else if (panel.Then is not null)
        {
            errors.Add($"codes panel '{panel.Id}' has a 'then'; a code button says which panel follows it");
        }

        HashSet<(Key, KeyModifiers)> seen = [];
        foreach (TagPaletteButton button in panel.Buttons)
        {
            string name = $"'{button.Caption}' in panel '{panel.Id}'";
            if (panel.IsLabels)
            {
                if (button.Value is null)
                {
                    errors.Add($"a button in labels panel '{panel.Id}' has no value");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(button.Code))
                {
                    errors.Add($"a button in codes panel '{panel.Id}' has no code");
                }

                if (button.LeadSeconds < 0 || button.LagSeconds < 0 || !double.IsFinite(button.LeadSeconds)
                    || !double.IsFinite(button.LagSeconds))
                {
                    errors.Add($"{name}: lead and lag are seconds, zero or more");
                }

                CheckFlow(palette, panel.Id, button.Then, errors);
            }

            if (button.Hotkey is null)
            {
                continue; // a button without a hotkey is mouse-only, which is allowed
            }

            if (!TagPaletteHotkey.TryParse(button.Hotkey, out Key key, out KeyModifiers modifiers, out string error))
            {
                errors.Add($"{name}: {error}");
                continue;
            }

            if (!seen.Add((key, modifiers)))
            {
                errors.Add($"{name}: hotkey {Playback2DKeymap.Format(key, modifiers)} is used twice in the panel");
            }

            CheckHotkey(key, modifiers, name, errors, warnings);
        }
    }

    // A reserved gesture never reaches the tab at all, and a palette-scoped keymap row would win over
    // the button, so both would be a button that cannot fire: refused. A tab binding is only shadowed
    // while the palette has focus: warned, with the shadowed action named.
    private static void CheckHotkey(Key key, KeyModifiers modifiers, string name, List<string> errors,
        List<string> warnings)
    {
        string gesture = Playback2DKeymap.Format(key, modifiers);
        if (Contains(Playback2DKeymap.ShellReservedGestures, key, modifiers))
        {
            errors.Add($"{name}: {gesture} is an app-wide shortcut");
            return;
        }

        if (Contains(Playback2DKeymap.BrowserReservedGestures, key, modifiers))
        {
            errors.Add($"{name}: {gesture} is taken by the browser, so the palette would never see it there");
            return;
        }

        foreach (Playback2DBinding binding in Playback2DKeymap.Default)
        {
            if (binding.Key != key || binding.Modifiers != modifiers)
            {
                continue;
            }

            if (binding.Scope == Playback2DBindingScope.WhenPaletteFocused)
            {
                errors.Add($"{name}: {gesture} is the palette's own key for \"{binding.Description}\"");
            }
            else
            {
                warnings.Add($"{name}: {gesture} shadows \"{binding.Description}\" while the palette has focus");
            }
        }

        foreach ((Key claimedKey, KeyModifiers claimedModifiers, string what) in _claimed)
        {
            if (claimedKey == key && claimedModifiers == modifiers)
            {
                warnings.Add($"{name}: {gesture} shadows \"{what}\" (claimed by Suggested Tags) while the palette has focus");
            }
        }
    }

    // A then names a labels panel, and following thens from there must end: the gesture is one undo
    // entry and has to finish somewhere.
    private static void CheckFlow(TagPaletteDefinition palette, string from, string? then, List<string> errors)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        string? next = then;
        while (next is not null)
        {
            if (palette.PanelById(next) is not { } target)
            {
                errors.Add($"panel '{from}' leads to '{next}', which is not a panel");
                return;
            }

            if (!target.IsLabels)
            {
                errors.Add($"panel '{from}' leads to '{next}', which is a codes panel; only labels panels follow");
                return;
            }

            if (!visited.Add(next))
            {
                errors.Add($"panel '{from}' leads round a loop through '{next}'");
                return;
            }

            next = target.Then;
        }
    }

    private static void CheckGroup(string group, string where, IReadOnlyCollection<string> reservedGroups,
        List<string> errors)
    {
        if (reservedGroups.Contains(group, StringComparer.Ordinal))
        {
            errors.Add($"{where}: '{group}' is a reserved group (a Round Facts fact or a strat link)");
        }
    }

    private static bool Contains(IEnumerable<(Key Key, KeyModifiers Modifiers)> gestures, Key key, KeyModifiers modifiers) =>
        gestures.Any(g => g.Key == key && g.Modifiers == modifiers);

    private static HashSet<string> BuildReservedGroups()
    {
        HashSet<string> groups = new(StratGroups, StringComparer.Ordinal);

        // Every optional fact present, so Labels emits every name it can; the values are irrelevant.
        Services.RoundFacts.RoundFacts probe = new()
        {
            Number = 1,
            MatchRoundNumber = 1,
            RoundTimeSeconds = 1,
            PlantTick = 1,
            PlanterSlot = 0,
            DefuseTick = 1,
            ExplodeTick = 1,
            EndTick = 1,
            FirstContactTick = 1,
            OpeningKillTick = 1,
            Ct = { MoneyAtFreezeEnd = 1 },
            T = { MoneyAtFreezeEnd = 1 }
        };

        try
        {
            foreach (FactLabel fact in RoundFactsSource.Labels(probe, 0))
            {
                groups.Add(fact.Key);
            }
        }
        catch (Exception)
        {
            // The strat groups still stand; a fact name that slips through is a namespace clash in a
            // document, not a crash.
        }

        return groups;
    }
}

/// <summary>The palette store's log seams, source-generated like <c>RoundFactsLog</c>.</summary>
internal static partial class TagPaletteLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "tag palette {source} skipped: {reason}")]
    public static partial void Skipped(ILogger logger, string source, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "tag palette {source}: {warning}")]
    public static partial void Warned(ILogger logger, string source, string warning);
}
