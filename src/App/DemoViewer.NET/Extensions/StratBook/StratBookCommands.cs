#region

using Avalonia.Input;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.StratBook.Canvas;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The Strat Book extension's keymap commands: the chords, scopes and labels that used to be rows in
///     <c>Playback2DKeymap.BuildDefault()</c>. Every id equals its <see cref="Playback2DAction" />'s enum
///     name, so a persisted <c>KeybindOverrides</c> row written before this table existed still resolves.
///     <para>
///         <see cref="CommandRegistry" /> composes this table over the core one with no DI, so the chord,
///         scope and resolution order are exactly what they were when these rows lived in the core file.
///         The handlers are unchanged too: each <c>Run</c> calls the same <c>ExecuteAction</c> the keymap
///         dispatch site called before the move.
///     </para>
/// </summary>
internal static class StratBookCommands
{
    /// <summary>Every command this extension contributes, in the shipped table's original order.</summary>
    public static IReadOnlyList<CommandDescriptor> All { get; } = Build();

    private static CommandDescriptor[] Build() =>
    [
        new("FindRoundsLikeThis",
            "Find rounds like this: snapshot the alive players onto the Situations query",
            "playback2d", new KeyGesture(Key.F, KeyModifiers.Control), TabAction(Playback2DAction.FindRoundsLikeThis)),

        new("NextSituationResult",
            "Next situation result: seek to the next card of the Situations search",
            "playback2d", new KeyGesture(Key.J, KeyModifiers.None), TabAction(Playback2DAction.NextSituationResult)),
        new("PrevSituationResult",
            "Previous situation result: seek to the previous card of the Situations search",
            "playback2d", new KeyGesture(Key.K, KeyModifiers.None), TabAction(Playback2DAction.PrevSituationResult)),

        new("FocusTagPalette",
            "Tag palette: focus it so its hotkeys tag (press again to leave)",
            "playback2d", new KeyGesture(Key.C, KeyModifiers.None), TabAction(Playback2DAction.FocusTagPalette)),
        new("TagPaletteBack",
            "Tag palette: finish the tag and go back to the codes, or leave the palette",
            "playback2d.palette", new KeyGesture(Key.Escape, KeyModifiers.None), TabAction(Playback2DAction.TagPaletteBack)),
        new("TagNote",
            "Tag palette: write a note on the tag just made",
            "playback2d.palette", new KeyGesture(Key.M, KeyModifiers.Control), TabAction(Playback2DAction.TagNote)),
        new("TagClearSticky",
            "Tag palette: clear the sticky labels",
            "playback2d.palette", new KeyGesture(Key.Back, KeyModifiers.Control), TabAction(Playback2DAction.TagClearSticky)),

        new("TagLabelMode",
            "Tag palette: label mode, adding labels to the tag under the playhead instead of making tags",
            "playback2d.palette", new KeyGesture(Key.L, KeyModifiers.Control), TabAction(Playback2DAction.TagLabelMode)),
        new("TagLabelGroupNext",
            "Tag palette: in label mode, show the next label group",
            "playback2d.palette", new KeyGesture(Key.G, KeyModifiers.Control), TabAction(Playback2DAction.TagLabelGroupNext)),

        new("SuggestionNext",
            "Suggested tags: select the next pending proposal and seek to it",
            "playback2d.suggestion", new KeyGesture(Key.J, KeyModifiers.None), TabAction(Playback2DAction.SuggestionNext)),
        new("SuggestionPrev",
            "Suggested tags: select the previous pending proposal and seek to it",
            "playback2d.suggestion", new KeyGesture(Key.K, KeyModifiers.None), TabAction(Playback2DAction.SuggestionPrev)),
        new("SuggestionAccept",
            "Suggested tags: accept the selected proposal as a tag and go to the next",
            "playback2d", new KeyGesture(Key.Y, KeyModifiers.None), TabAction(Playback2DAction.SuggestionAccept)),
        new("SuggestionReject",
            "Suggested tags: reject the selected proposal and go to the next",
            "playback2d", new KeyGesture(Key.N, KeyModifiers.None), TabAction(Playback2DAction.SuggestionReject)),
        new("SuggestionEdit",
            "Suggested tags: edit the selected proposal before accepting it",
            "playback2d", new KeyGesture(Key.Enter, KeyModifiers.None), TabAction(Playback2DAction.SuggestionEdit)),
        new("SuggestionAcceptAll",
            "Suggested tags: accept every pending proposal the queue's filter shows, after a confirm",
            "playback2d", new KeyGesture(Key.Y, KeyModifiers.Control), TabAction(Playback2DAction.SuggestionAcceptAll)),

        new("ToggleReviewMode",
            "Review mode: show the tag palette, the suggestions and the tag lanes, or hide them",
            "playback2d", new KeyGesture(Key.R, KeyModifiers.Shift), TabAction(Playback2DAction.ToggleReviewMode)),

        new("ToolToken",
            "Strat canvas: token tool (press again for pan)",
            "playback2d", new KeyGesture(Key.V, KeyModifiers.None), CanvasAction(Playback2DAction.ToolToken)),
        new("AddStep",
            "Strat canvas: insert a step after the active one at the playhead's round-clock time",
            "playback2d", new KeyGesture(Key.N, KeyModifiers.Shift), CanvasAction(Playback2DAction.AddStep)),
        new("DuplicateStep",
            "Strat canvas: copy the active step's positions and strokes into a new step 5 s later",
            "playback2d", new KeyGesture(Key.D, KeyModifiers.Control), CanvasAction(Playback2DAction.DuplicateStep)),
        new("DeleteStep",
            "Strat canvas: delete the active step",
            "playback2d", new KeyGesture(Key.Delete, KeyModifiers.Control), CanvasAction(Playback2DAction.DeleteStep)),
        new("PrevStep",
            "Strat canvas: seek to the previous step and make it active",
            "playback2d", new KeyGesture(Key.OemOpenBrackets, KeyModifiers.None), CanvasAction(Playback2DAction.PrevStep)),
        new("NextStep",
            "Strat canvas: seek to the next step and make it active",
            "playback2d", new KeyGesture(Key.OemCloseBrackets, KeyModifiers.None), CanvasAction(Playback2DAction.NextStep))
    ];

    // The 2D Playback tab VM's own ExecuteAction switch still owns these cases; Run just reaches it
    // through the context the keymap dispatch site hands a command, same as it handed the action before.
    private static Func<CommandContext, bool> TabAction(Playback2DAction action) =>
        ctx => ctx.Target is Playback2DTabViewModel tab && tab.ExecuteAction(action);

    // AddStep/DuplicateStep/DeleteStep/PrevStep/NextStep/ToolToken act on the strat canvas, not the 2D
    // Playback tab; the tab's own switch already returns false for all six.
    private static Func<CommandContext, bool> CanvasAction(Playback2DAction action) =>
        ctx => ctx.Target is StratCanvasViewModel canvas && canvas.ExecuteAction(action);
}
