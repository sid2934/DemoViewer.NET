#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     The keymap as shipped: every default binding, the core table's and every extension's, and the gestures no
///     action can take. For checking keys an extension assigns on its own, such as hotkeys in a file the user
///     edits, against what the keymap already uses.
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
