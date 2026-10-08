#region

using Avalonia.Input;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The Strat Book's keymap action ids and focus scopes. Every id is a persisted override key: never
///     renamed.
/// </summary>
internal static class StratBookActions
{
    /// <summary>Every id and scope starts with the pack id and a dot.</summary>
    public const string Prefix = StratBookPack.PackId + ".";

    public const string FindRoundsLikeThis = Prefix + "FindRoundsLikeThis";
    public const string NextSituationResult = Prefix + "NextSituationResult";
    public const string PrevSituationResult = Prefix + "PrevSituationResult";
    public const string FocusTagPalette = Prefix + "FocusTagPalette";
    public const string TagPaletteBack = Prefix + "TagPaletteBack";
    public const string TagNote = Prefix + "TagNote";
    public const string TagClearSticky = Prefix + "TagClearSticky";
    public const string TagLabelMode = Prefix + "TagLabelMode";
    public const string TagLabelGroupNext = Prefix + "TagLabelGroupNext";
    public const string SuggestionNext = Prefix + "SuggestionNext";
    public const string SuggestionPrev = Prefix + "SuggestionPrev";
    public const string SuggestionAccept = Prefix + "SuggestionAccept";
    public const string SuggestionReject = Prefix + "SuggestionReject";
    public const string SuggestionEdit = Prefix + "SuggestionEdit";
    public const string SuggestionAcceptAll = Prefix + "SuggestionAcceptAll";
    public const string ToggleReviewMode = Prefix + "ToggleReviewMode";
    public const string ToolToken = Prefix + "ToolToken";
    public const string AddStep = Prefix + "AddStep";
    public const string DuplicateStep = Prefix + "DuplicateStep";
    public const string DeleteStep = Prefix + "DeleteStep";
    public const string PrevStep = Prefix + "PrevStep";
    public const string NextStep = Prefix + "NextStep";

    /// <summary>
    ///     While the Tag Palette has focus. Shadows both of the tab's scopes; the palette's own button hotkeys
    ///     are routed after its rows.
    /// </summary>
    public const string PaletteScope = Prefix + "palette";

    /// <summary>
    ///     While a proposal is selected in the Suggested Tags queue. Shadows both of the tab's scopes; the
    ///     palette scope still wins. J and K walk the Situations result set otherwise: both walks keep the
    ///     same keys, and the one on screen is the one they drive.
    /// </summary>
    public const string SuggestionScope = Prefix + "suggestion";
}

/// <summary>
///     The core actions the strat canvas runs and names in its hints, by the ids the keymap carries them under.
///     The host resolves them under the user's keymap like any other id.
/// </summary>
internal static class CoreActions
{
    public const string TogglePlay = "TogglePlay";
    public const string StepBack = "StepBack";
    public const string StepForward = "StepForward";
    public const string SpeedUp = "SpeedUp";
    public const string SpeedDown = "SpeedDown";
    public const string Undo = "Undo";
    public const string Redo = "Redo";
    public const string HoldPan = "HoldPan";
    public const string CancelGesture = "CancelGesture";
    public const string ClearAnnotations = "ClearAnnotations";
    public const string ToolDraw = "ToolDraw";
    public const string ToolErase = "ToolErase";
    public const string ToolLine = "ToolLine";
    public const string ToolArrow = "ToolArrow";
    public const string ToolRect = "ToolRect";
    public const string ToolEllipse = "ToolEllipse";
    public const string ToolText = "ToolText";

    /// <summary>The keymap scope every surface resolves in.</summary>
    public const string Scope = "playback2d";

    /// <summary>The scope resolved first while a pointer tool is active; it shadows <see cref="Scope" />.</summary>
    public const string ToolScope = "playback2d.tool";
}

/// <summary>
///     The Strat Book extension's keymap commands and the focus scopes they name. The tab hands a resolved
///     id to the pack's playback contributions through their action handlers; the strat canvas runs its own
///     through <see cref="StratCanvasViewModel.ExecuteAction(string)" />.
/// </summary>
internal static class StratBookCommands
{
    /// <summary>Every command this extension contributes, in a stable, declared order.</summary>
    public static IReadOnlyList<CommandDescriptor> All { get; } = Build();

    /// <summary>The two focus scopes the commands name beyond the tab's own.</summary>
    public static IReadOnlyList<CommandScope> Scopes { get; } =
    [
        new(StratBookActions.PaletteScope, "while tagging"),
        new(StratBookActions.SuggestionScope, "while reviewing suggestions")
    ];

    private static CommandDescriptor[] Build()
    {
        const string always = "playback2d";
        const string palette = StratBookActions.PaletteScope;
        const string suggestion = StratBookActions.SuggestionScope;
        return
        [
            Command(StratBookActions.FindRoundsLikeThis,
                "Find rounds like this: snapshot the alive players onto the Situations query",
                always, Key.F, KeyModifiers.Control),

            Command(StratBookActions.NextSituationResult,
                "Next situation result: seek to the next card of the Situations search",
                always, Key.J, KeyModifiers.None),
            Command(StratBookActions.PrevSituationResult,
                "Previous situation result: seek to the previous card of the Situations search",
                always, Key.K, KeyModifiers.None),

            Command(StratBookActions.FocusTagPalette,
                "Tag palette: focus it so its hotkeys tag (press again to leave)",
                always, Key.C, KeyModifiers.None),
            Command(StratBookActions.TagPaletteBack,
                "Tag palette: finish the tag and go back to the codes, or leave the palette",
                palette, Key.Escape, KeyModifiers.None),
            Command(StratBookActions.TagNote,
                "Tag palette: write a note on the tag just made",
                palette, Key.M, KeyModifiers.Control),
            Command(StratBookActions.TagClearSticky,
                "Tag palette: clear the sticky labels",
                palette, Key.Back, KeyModifiers.Control),

            Command(StratBookActions.TagLabelMode,
                "Tag palette: label mode, adding labels to the tag under the playhead instead of making tags",
                palette, Key.L, KeyModifiers.Control),
            Command(StratBookActions.TagLabelGroupNext,
                "Tag palette: in label mode, show the next label group",
                palette, Key.G, KeyModifiers.Control),

            Command(StratBookActions.SuggestionNext,
                "Suggested tags: select the next pending proposal and seek to it",
                suggestion, Key.J, KeyModifiers.None),
            Command(StratBookActions.SuggestionPrev,
                "Suggested tags: select the previous pending proposal and seek to it",
                suggestion, Key.K, KeyModifiers.None),
            Command(StratBookActions.SuggestionAccept,
                "Suggested tags: accept the selected proposal as a tag and go to the next",
                always, Key.Y, KeyModifiers.None),
            Command(StratBookActions.SuggestionReject,
                "Suggested tags: reject the selected proposal and go to the next",
                always, Key.N, KeyModifiers.None),
            Command(StratBookActions.SuggestionEdit,
                "Suggested tags: edit the selected proposal before accepting it",
                always, Key.Enter, KeyModifiers.None),
            Command(StratBookActions.SuggestionAcceptAll,
                "Suggested tags: accept every pending proposal the queue's filter shows, after a confirm",
                always, Key.Y, KeyModifiers.Control),

            Command(StratBookActions.ToggleReviewMode,
                "Review mode: show the tag palette, the suggestions and the tag lanes, or hide them",
                always, Key.R, KeyModifiers.Shift),

            Command(StratBookActions.ToolToken,
                "Strat canvas: token tool (press again for pan)",
                always, Key.V, KeyModifiers.None),
            Command(StratBookActions.AddStep,
                "Strat canvas: insert a step after the active one at the playhead's round-clock time",
                always, Key.N, KeyModifiers.Shift),
            Command(StratBookActions.DuplicateStep,
                "Strat canvas: copy the active step's positions and strokes into a new step 5 s later",
                always, Key.D, KeyModifiers.Control),
            Command(StratBookActions.DeleteStep,
                "Strat canvas: delete the active step",
                always, Key.Delete, KeyModifiers.Control),
            Command(StratBookActions.PrevStep,
                "Strat canvas: seek to the previous step and make it active",
                always, Key.OemOpenBrackets, KeyModifiers.None),
            Command(StratBookActions.NextStep,
                "Strat canvas: seek to the next step and make it active",
                always, Key.OemCloseBrackets, KeyModifiers.None)
        ];
    }

    // On the 2D Playback tab the id reaches the pack's contributions through their action handlers, not Run;
    // Run serves the strat canvas, which runs its own step and token actions.
    private static CommandDescriptor Command(string id, string label, string scope, Key key, KeyModifiers modifiers) =>
        new(id, label, scope, new KeyGesture(key, modifiers),
            ctx => ctx.Target is StratCanvasViewModel canvas && canvas.ExecuteAction(id));
}
