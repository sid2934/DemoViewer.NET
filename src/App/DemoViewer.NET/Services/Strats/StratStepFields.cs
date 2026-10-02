namespace DemoViewer.NET.Services.Strats;

/// <summary>The step members a verb may carry beyond time, actor, verb and note, which every step has.</summary>
[Flags]
public enum StratStepField
{
    None = 0,
    From = 1,
    To = 2,
    Utility = 4,

    /// <summary>What each line's player watches.</summary>
    Watch = 8,

    /// <summary>A lurk's areas and rotate.</summary>
    Lurk = 16,

    /// <summary>Places a travel goes through on its way: <c>via</c>, the step's or each line's.</summary>
    Via = 32,

    /// <summary>What <c>other</c> and a verb outside the vocabulary use: everything but the lurk's own fields.</summary>
    All = From | To | Utility | Watch | Via
}

/// <summary>What a step's verb does with its destination (<see cref="StratStepFields.MotionOf" />).</summary>
public enum StepMotion
{
    /// <summary>No motion from the destination: throw, wait, call.</summary>
    None,

    /// <summary>The token leaves where it stands at the step's time and runs there.</summary>
    Travel,

    /// <summary>The token is there at the step's time.</summary>
    Position,

    /// <summary>The token walks to the lurk's first area from the step's time.</summary>
    Lurk
}

/// <summary>
///     Which members each verb uses: the editor shows these, and a verb change clears the rest, so an export
///     never prints a value the row hides. A verb outside the vocabulary (refused by the validator) gets all.
///     Must cover every member <see cref="StratValidator" /> asks a verb for: a move without <c>to</c> warns.
///     <see cref="StratFromRound" /> writes <c>to</c> on a plant and <c>from</c> on a throw; a member a verb does
///     not use still shows while it holds a value. Moving verbs (move, rotate) watch their path, so they take no
///     watch; push does, as it ends on a place facing something.
/// </summary>
public static class StratStepFields
{
    /// <summary>The members <paramref name="verb" /> uses. <c>other</c> and any verb outside the vocabulary use <see cref="StratStepField.All" />.</summary>
    /// <param name="verb">A step verb.</param>
    public static StratStepField For(string? verb) => verb switch
    {
        "move" or "rotate" => StratStepField.From | StratStepField.To | StratStepField.Via,
        "push" => StratStepField.From | StratStepField.To | StratStepField.Watch | StratStepField.Via,
        "hold" or "peek" => StratStepField.To | StratStepField.Watch,
        "plant" or "defuse" => StratStepField.To,
        "throw" => StratStepField.Utility,
        "fake" => StratStepField.To | StratStepField.Utility | StratStepField.Watch,
        "lurk" => StratStepField.Watch | StratStepField.Lurk | StratStepField.Via,
        "wait" or "call" => StratStepField.None,
        _ => StratStepField.All
    };

    /// <summary>Whether <paramref name="verb" /> uses <paramref name="field" />.</summary>
    public static bool Uses(string? verb, StratStepField field) => (For(verb) & field) == field;

    /// <summary>
    ///     What <paramref name="verb" /> does with its destination: move, push, rotate, <c>other</c> and any verb outside
    ///     the vocabulary travel there; hold, peek, fake, plant and defuse are there at the step's time; a lurk walks to
    ///     its first area and never moves for a <c>to</c>; throw, wait and call do not move.
    /// </summary>
    /// <param name="verb">A step verb.</param>
    public static StepMotion MotionOf(string? verb) => verb switch
    {
        "hold" or "peek" or "fake" or "plant" or "defuse" => StepMotion.Position,
        "lurk" => StepMotion.Lurk,
        "throw" or "wait" or "call" => StepMotion.None,
        _ => StepMotion.Travel
    };

    /// <summary>Whether <paramref name="verb" /> moves its players to their <c>to</c>.</summary>
    /// <param name="verb">A step verb.</param>
    public static bool MovesToTo(string? verb) => MotionOf(verb) is StepMotion.Travel or StepMotion.Position;

    /// <summary>What the row calls the <c>to</c> place for <paramref name="verb" />.</summary>
    /// <param name="verb">A step verb.</param>
    public static string ToLabel(string? verb) => verb switch
    {
        "hold" or "peek" or "fake" => "at",
        "plant" or "defuse" => "site",
        _ => "to"
    };
}
