#region

using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Services.Tags;

// The on-disk shape of a tag palette (<config>/palettes/<name>.tagpalette.json, tag-store.md §3.4): the
// tagging vocabulary as data, so a team authors its own codes and panels without a build. Palettes are
// never written by the app, but every object still carries a [JsonExtensionData] bag so a field a newer
// build understands is at least not an error to this one.

/// <summary>The spellings of <see cref="TagPalettePanel.Kind" />.</summary>
public static class TagPalettePanelKinds
{
    /// <summary>Buttons that create an instance. The default when a panel names no kind.</summary>
    public const string Codes = "codes";

    /// <summary>Buttons that add one label of the panel's group to the instance being made.</summary>
    public const string Labels = "labels";
}

/// <summary>One palette: its panels, which label groups are sticky, and whether a span stays in its round.</summary>
public sealed class TagPaletteDefinition
{
    /// <summary>Advisory, like the tag document's: a higher number is read for what this build understands.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The persisted key: the settings row and the tag document's <c>palette</c> name it.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The panels. The one with id <see cref="RootPanelId" />, else the first, is where tagging starts.</summary>
    public List<TagPalettePanel> Panels { get; set; } = [];

    /// <summary>Groups whose current value the session re-applies to every new instance until cleared.</summary>
    public List<string> StickyGroups { get; set; } = [];

    /// <summary>
    ///     Whether a code's span is clamped to the round the playhead is in. On unless a palette says
    ///     otherwise: an instance that crosses a round boundary is almost always a mis-press (decision D3).
    /// </summary>
    public bool ClampToRound { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The id a palette's entry panel is expected to carry.</summary>
    public const string RootPanelId = "root";

    /// <summary>The panel tagging starts from and returns to, or null for a palette with no panels.</summary>
    public TagPalettePanel? Root =>
        Panels.FirstOrDefault(p => string.Equals(p.Id, RootPanelId, StringComparison.Ordinal)) ?? Panels.FirstOrDefault();

    /// <summary>The panel with this id, or null.</summary>
    /// <param name="id">A panel id, as a <c>then</c> names it.</param>
    public TagPalettePanel? PanelById(string? id) =>
        id is null ? null : Panels.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>The colour of the first button that makes <paramref name="code" />, or null when none gives one.</summary>
    /// <param name="code">An instance's code.</param>
    public uint? ColourOf(string code)
    {
        foreach (TagPalettePanel panel in Panels)
        {
            foreach (TagPaletteButton button in panel.Buttons)
            {
                if (button.ColorArgb is { } argb && string.Equals(button.Code, code, StringComparison.Ordinal))
                {
                    return argb;
                }
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="group" /> is one of <see cref="StickyGroups" />.</summary>
    /// <param name="group">A label group.</param>
    public bool IsSticky(string? group) =>
        group is not null && StickyGroups.Contains(group, StringComparer.Ordinal);
}

/// <summary>One panel: a row of code buttons, or of label values for one group.</summary>
public sealed class TagPalettePanel
{
    public string Id { get; set; } = "";

    /// <summary>One of <see cref="TagPalettePanelKinds" />; absent means codes.</summary>
    public string? Kind { get; set; }

    /// <summary>The label group a <c>labels</c> panel writes. Empty is a bare label.</summary>
    public string? Group { get; set; }

    public List<TagPaletteButton> Buttons { get; set; } = [];

    /// <summary>For a <c>labels</c> panel, the panel revealed after a press; null ends the gesture.</summary>
    public string? Then { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Whether this is a <c>labels</c> panel.</summary>
    [JsonIgnore]
    public bool IsLabels => string.Equals(Kind, TagPalettePanelKinds.Labels, StringComparison.Ordinal);
}

/// <summary>
///     One button. A code panel's button has a <see cref="Code" /> and its span; a labels panel's has a
///     <see cref="Value" />. Lead and lag are seconds, not ticks: the author does not know the tick rate and
///     the document does.
/// </summary>
public sealed class TagPaletteButton
{
    public string? Code { get; set; }

    public string? Value { get; set; }

    /// <summary>A single key or a <c>Ctrl+</c>/<c>Shift+</c> chord, parsed by <see cref="TagPaletteHotkey" />.</summary>
    public string? Hotkey { get; set; }

    /// <summary>Seconds before the playhead the instance starts.</summary>
    public double LeadSeconds { get; set; }

    /// <summary>Seconds after the playhead the instance ends.</summary>
    public double LagSeconds { get; set; }

    /// <summary>The panel revealed after this code's press (the panel flow); null ends the gesture.</summary>
    public string? Then { get; set; }

    /// <summary>The code's colour as ARGB, for the button and the Tag Track's band.</summary>
    public uint? ColorArgb { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>What the button says: its code, else its value.</summary>
    [JsonIgnore]
    public string Caption => Code ?? Value ?? "";
}

/// <summary>
///     Palette hotkeys: one key, optionally with <c>Ctrl</c>, <c>Shift</c> or both (tag-store.md §3.4). Alt
///     and Meta are refused: Alt opens the menu bar on Windows and Meta is the macOS command key, so a
///     palette that used them would behave differently per head.
/// </summary>
public static class TagPaletteHotkey
{
    /// <summary>Parses a hotkey. A bare digit is the top-row digit key, as a palette author means it.</summary>
    /// <param name="text">The hotkey text from the palette file.</param>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers.</param>
    /// <param name="error">Why it was refused, or "".</param>
    public static bool TryParse(string? text, out Key key, out KeyModifiers modifiers, out string error)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "no hotkey";
            return false;
        }

        string trimmed = text.Trim();

        // KeyGesture.Parse reads "1" as no key it knows; the author means the digit on the top row.
        int plus = trimmed.LastIndexOf('+');
        string keyPart = plus < 0 ? trimmed : trimmed[(plus + 1)..];
        if (keyPart.Length == 1 && char.IsAsciiDigit(keyPart[0]))
        {
            trimmed = (plus < 0 ? "" : trimmed[..(plus + 1)]) + "D" + keyPart;
        }

        KeyGesture gesture;
        try
        {
            gesture = KeyGesture.Parse(trimmed);
        }
        catch (Exception)
        {
            // Parse throws several unrelated types for a bad string; which one says nothing to an author.
            error = $"'{text}' is not a key";
            return false;
        }

        if (gesture.Key == Key.None)
        {
            error = $"'{text}' names no key";
            return false;
        }

        if ((gesture.KeyModifiers & ~(KeyModifiers.Control | KeyModifiers.Shift)) != KeyModifiers.None)
        {
            error = $"'{text}' uses Alt or Meta; a palette hotkey is a key with Ctrl, Shift or neither";
            return false;
        }

        key = gesture.Key;
        modifiers = gesture.KeyModifiers;
        error = "";
        return true;
    }

    /// <summary>
    ///     Whether a pressed key is this hotkey. The number pad's digits count as the top row's, so a
    ///     palette on <c>1</c> to <c>4</c> works with either hand.
    /// </summary>
    /// <param name="hotkey">The parsed hotkey.</param>
    /// <param name="hotkeyModifiers">Its modifiers.</param>
    /// <param name="pressed">The key pressed.</param>
    /// <param name="pressedModifiers">The modifiers held.</param>
    public static bool Matches(Key hotkey, KeyModifiers hotkeyModifiers, Key pressed, KeyModifiers pressedModifiers) =>
        hotkeyModifiers == pressedModifiers && (hotkey == pressed || hotkey == TopRow(pressed));

    private static Key TopRow(Key key) =>
        key is >= Key.NumPad0 and <= Key.NumPad9 ? Key.D0 + (key - Key.NumPad0) : key;
}

/// <summary>Source-generated, the tag document's reason: the browser head cannot trim a reflection serializer.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(TagPaletteDefinition))]
public sealed partial class TagPaletteJsonContext : JsonSerializerContext;
