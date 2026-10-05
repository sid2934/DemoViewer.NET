#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     A command the keymap resolves by string id. A user's rebinding is stored against the id, so a core
///     action and an extension command share one override list. The 2D Playback tab hands a resolved id to
///     the handlers added through <c>IPlaybackSurface.AddActionHandler</c>, and to a toolbar item or mode
///     toggle that names it.
/// </summary>
/// <param name="Id">
///     Stable id, prefixed with the extension's id and a dot, e.g. <c>"dev.example.hello.wave"</c>. A
///     persisted override key: never renamed. Core actions keep bare ids. A command whose id lacks the
///     prefix, or repeats an id already taken, is reported and left out.
/// </param>
/// <param name="Label">Human description, shown in the keybind settings list.</param>
/// <param name="Scope">
///     <c>"playback2d"</c> (always), <c>"playback2d.tool"</c> (while a drawing tool is active), or the id of
///     a <see cref="CommandScope" /> the same extension declares.
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

/// <summary>
///     A focus scope an extension declares for its commands, such as a panel that takes the keyboard. The
///     tab never resolves it: the extension asks <c>IPlaybackSurface.ActionFor</c> from its own key handler
///     while its panel has focus, which is what lets the scope shadow the tab's own keys.
/// </summary>
/// <param name="Id">Stable id, prefixed with the extension's id and a dot, e.g. <c>"dev.example.hello.panel"</c>.</param>
/// <param name="Label">How the keybind settings list names it, e.g. <c>"while tagging"</c>.</param>
public sealed record CommandScope(string Id, string Label);
