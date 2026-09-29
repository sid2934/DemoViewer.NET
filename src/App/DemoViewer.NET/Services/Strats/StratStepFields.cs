namespace DemoViewer.NET.Services.Strats;

/// <summary>The step members a verb may carry beyond time, actor, verb and note, which every step has.</summary>
[Flags]
public enum StratStepField
{
    None = 0,
    From = 1,
    To = 2,
    Utility = 4,
    All = From | To | Utility
}

/// <summary>
///     Which members each verb uses: the editor shows these, and a verb change clears the rest, so an export
///     never prints a value the row hides. A verb outside the vocabulary (refused by the validator) gets all.
///     Must cover every member <see cref="StratValidator" /> asks a verb for: a move without <c>to</c> warns.
///     <see cref="StratFromRound" /> writes <c>to</c> on a plant and <c>from</c> on a throw; a member a verb does
///     not use still shows while it holds a value.
/// </summary>
public static class StratStepFields
{
    /// <summary>The members <paramref name="verb" /> uses.</summary>
    /// <param name="verb">A step verb.</param>
    public static StratStepField For(string? verb) => verb switch
    {
        "move" or "rotate" => StratStepField.From | StratStepField.To,
        "hold" or "peek" or "plant" or "defuse" => StratStepField.To,
        "throw" => StratStepField.Utility,
        "fake" => StratStepField.To | StratStepField.Utility,
        "wait" or "call" => StratStepField.None,
        _ => StratStepField.All
    };

    /// <summary>Whether <paramref name="verb" /> uses <paramref name="field" />.</summary>
    public static bool Uses(string? verb, StratStepField field) => (For(verb) & field) == field;

    /// <summary>What the row calls the <c>to</c> place for <paramref name="verb" />.</summary>
    /// <param name="verb">A step verb.</param>
    public static string ToLabel(string? verb) => verb switch
    {
        "hold" or "peek" or "fake" => "at",
        "plant" or "defuse" => "site",
        _ => "to"
    };
}
