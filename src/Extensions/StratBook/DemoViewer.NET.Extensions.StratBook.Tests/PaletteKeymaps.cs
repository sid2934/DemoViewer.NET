#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Palette;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>The palette's keys under a resolved keymap profile, as the 2D tab's surface would hand them over.</summary>
internal static class PaletteKeymaps
{
    public static PaletteKeymap From(Playback2DKeymapProfile keymap) => new(
        (key, modifiers) => keymap.TryResolveInScope(new Playback2DBindingScope(StratBookActions.PaletteScope), key, modifiers,
            out string? action)
            ? action
            : null,
        keymap.GestureText);
}
