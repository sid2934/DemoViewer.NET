#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     A command the keymap resolves by string id. A user's rebinding is stored against the id, so a core
///     action and an extension command share one override list.
/// </summary>
/// <param name="Id">Stable id, e.g. <c>"TagNote"</c>. A persisted override key: never renamed.</param>
/// <param name="Label">Human description, shown in the keybind settings list.</param>
/// <param name="Scope">
///     <c>"playback2d"</c> (always), <c>"playback2d.tool"</c> (while a drawing tool is active),
///     <c>"playback2d.palette"</c> (while the Tag Palette has focus) or <c>"playback2d.suggestion"</c>
///     (while a suggestion is selected).
/// </param>
/// <param name="DefaultChord">The shipped gesture, or null for a command with no default binding.</param>
/// <param name="Run">Runs the command. True when it acted; false leaves the key unhandled.</param>
/// <param name="CanRun">Optional: whether the command can act right now.</param>
public sealed record CommandDescriptor(
    string Id,
    string Label,
    string Scope,
    KeyGesture? DefaultChord,
    Func<CommandContext, bool> Run,
    Func<CommandContext, bool>? CanRun = null);

/// <summary>What a command's <see cref="CommandDescriptor.Run" /> or <see cref="CommandDescriptor.CanRun" /> receives.</summary>
/// <param name="Target">The view model the key resolved against .</param>
public sealed record CommandContext(object Target);
