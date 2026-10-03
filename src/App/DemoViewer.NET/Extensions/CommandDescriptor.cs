#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     One command the keymap (and, later, a command palette) can resolve by string id. A core action's id
///     is its <c>Playback2DAction</c> enum name, so a persisted <c>AppSettings.KeybindOverrides</c> row
///     keeps working whether the command is core or pack-contributed.
/// </summary>
/// <param name="Id">Stable id, e.g. <c>"TagNote"</c>. A persisted override key: never renamed.</param>
/// <param name="Label">Human description, shown in the keybind settings list.</param>
/// <param name="Scope">
///     <c>"playback2d"</c> (always), <c>"playback2d.tool"</c> (while a drawing tool is active),
///     <c>"playback2d.palette"</c> (while the Tag Palette has focus) or <c>"playback2d.suggestion"</c>
///     (while a suggestion is selected). Mirrors <c>Playback2DBindingScope</c> one to one.
/// </param>
/// <param name="DefaultChord">The shipped gesture, or null for a command with no default binding.</param>
/// <param name="Run">
///     Runs the command against <paramref name="Run" />'s <c>CommandContext</c>. True when it acted, same
///     convention as <c>Playback2DTabViewModel.ExecuteAction</c>: false leaves the key unhandled.
/// </param>
/// <param name="CanRun">Optional: whether the command can act right now, for a future command palette's enablement.</param>
public sealed record CommandDescriptor(
    string Id,
    string Label,
    string Scope,
    KeyGesture? DefaultChord,
    Func<CommandContext, bool> Run,
    Func<CommandContext, bool>? CanRun = null);

/// <summary>What a command's <see cref="CommandDescriptor.Run" /> or <see cref="CommandDescriptor.CanRun" /> receives.</summary>
/// <param name="Target">The view model the key resolved against (the 2D Playback tab VM or the Strat canvas VM today).</param>
public sealed record CommandContext(object Target);
