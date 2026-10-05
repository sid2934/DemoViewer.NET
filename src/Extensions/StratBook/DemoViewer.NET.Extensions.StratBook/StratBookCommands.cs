#region

using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
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
    public static Playback2DBindingScope PaletteScope { get; } = new(Prefix + "palette");

    /// <summary>
    ///     While a proposal is selected in the Suggested Tags queue. Shadows both of the tab's scopes; the
    ///     palette scope still wins. J and K walk the Situations result set otherwise: both walks keep the
    ///     same keys, and the one on screen is the one they drive.
    /// </summary>
    public static Playback2DBindingScope SuggestionScope { get; } = new(Prefix + "suggestion");
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
        new(StratBookActions.PaletteScope.Name, "while tagging"),
        new(StratBookActions.SuggestionScope.Name, "while reviewing suggestions")
    ];

    /// <summary>
    ///     The bare ids these commands shipped under before ids carried the pack's prefix, each to its id now.
    ///     A user's override row keyed by a bare id still applies through this map.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Aliases { get; } =
        All.ToDictionary(c => c.Id[StratBookActions.Prefix.Length..], c => c.Id, StringComparer.Ordinal);

    private static CommandDescriptor[] Build()
    {
        const string always = "playback2d";
        string palette = StratBookActions.PaletteScope.Name;
        string suggestion = StratBookActions.SuggestionScope.Name;
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

    // Run reaches whichever surface the key resolved against: the 2D Playback tab offers the id to the
    // pack's contributions, and the strat canvas runs its own step and token actions.
    private static CommandDescriptor Command(string id, string label, string scope, Key key, KeyModifiers modifiers) =>
        new(id, label, scope, new KeyGesture(key, modifiers), ctx => ctx.Target switch
        {
            Playback2DTabViewModel tab => tab.ExecuteAction(id),
            StratCanvasViewModel canvas => canvas.ExecuteAction(id),
            _ => false
        });
}
