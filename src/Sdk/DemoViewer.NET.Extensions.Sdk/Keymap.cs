#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     The app's one keymap. <see cref="Bindings" /> is the table as shipped, for checking keys an extension
///     assigns on its own, such as hotkeys in a file the user edits, against what the keymap already uses.
///     <see cref="ActionFor" /> and <see cref="GestureText" /> read the user's keymap, their rebinds applied, for a
///     surface of your own that resolves keys itself; a 2D Playback contribution asks its surface instead.
/// </summary>
public interface IExtensionKeymap
{
    /// <summary>
    ///     Every shipped binding: the core table's rows first, then each extension command whose default gesture
    ///     did not collide with an earlier row. The user's rebinds are not applied.
    /// </summary>
    IReadOnlyList<KeymapBinding> Bindings { get; }

    /// <summary>Gestures the app's shell handles before any tab sees them.</summary>
    IReadOnlyList<KeyGesture> ShellReserved { get; }

    /// <summary>Gestures a browser takes before the page sees them. A key bound to one never fires in the browser build.</summary>
    IReadOnlyList<KeyGesture> BrowserReserved { get; }

    /// <summary>
    ///     The action a key is bound to in <paramref name="scope" /> under the user's keymap, or null. A surface
    ///     that hosts the 2D tools asks <c>"playback2d.tool"</c> first while a tool is active, then
    ///     <c>"playback2d"</c>, which is the order the 2D Playback tab resolves in.
    /// </summary>
    /// <param name="scope">A <see cref="CommandScope.Id" />, or <c>"playback2d"</c> or <c>"playback2d.tool"</c>.</param>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers held.</param>
    string? ActionFor(string scope, Key key, KeyModifiers modifiers);

    /// <summary>The user's gesture for an action as display text, such as <c>"Ctrl+Z"</c>, or an empty string when unbound.</summary>
    /// <param name="actionId">The action's id.</param>
    string GestureText(string actionId);

    /// <summary>Raised on the UI thread after the user rebinds a key. Refresh labels that show gestures.</summary>
    event Action? Changed;
}

/// <summary>One shipped binding of the keymap.</summary>
/// <param name="ActionId">The action's id: a core action's bare id, or an extension command's prefixed one.</param>
/// <param name="Label">The action's description, as the keybind settings list shows it.</param>
/// <param name="Scope">
///     <c>"playback2d"</c>, <c>"playback2d.tool"</c>, or the id of a <see cref="CommandScope" /> an extension
///     declared.
/// </param>
/// <param name="Gesture">The bound gesture.</param>
public sealed record KeymapBinding(string ActionId, string Label, string Scope, KeyGesture Gesture);

/// <summary>Gesture text the way the app shows it everywhere, such as <c>"Ctrl+Shift+F"</c> or <c>"Esc"</c>.</summary>
public static class KeyGestureText
{
    /// <summary>The display text for a key and its modifiers, modifiers in the order Ctrl, Shift, Alt, Meta.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers held.</param>
    public static string Format(Key key, KeyModifiers modifiers) => Format(key, modifiers, true);

    /// <summary>
    ///     The text for a key and its modifiers. Display text names arrows and a few keys for people; the other
    ///     spelling is the one <see cref="KeyGesture.Parse" /> reads back.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers held.</param>
    /// <param name="display">True for display text, false for the parseable spelling.</param>
    public static string Format(Key key, KeyModifiers modifiers, bool display)
    {
        // Meta must stay in the chain: without it a macOS Cmd+K reads as a bare K.
        List<string> parts = new(5);
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Meta");
        }

        parts.Add(display ? KeyName(key) : key.ToString());
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Escape => "Esc",
        Key.Space => "Space",
        Key.Home => "Home",
        Key.Back => "Backspace",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.Enter => "Enter", // the same value as Key.Return, which is what ToString names it
        _ => key.ToString()
    };
}
