#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Strats.Mining;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>One step of a template, on the round clock. Places are canonical nav names.</summary>
/// <param name="AtSeconds">Round clock remaining; negative after a plant.</param>
/// <param name="Actor">A slot letter or <c>all</c>.</param>
/// <param name="Verb">A verb that uses every member set here (<see cref="StratStepFields" />).</param>
/// <param name="To">The <c>to</c> place, or null.</param>
/// <param name="Utility">The grenade kind of a throw or fake, or null.</param>
/// <param name="Note">What the author fills in.</param>
/// <param name="Lines">
///     Who goes where, for a step several players take different parts in; <paramref name="Actor" /> and
///     <paramref name="To" /> are then <see cref="StratStepLines.ActorFor" /> of the lines and null.
/// </param>
public sealed record StratTemplateStep(double AtSeconds, string Actor, string Verb, string? To = null, string? Utility = null,
    string? Note = null, IReadOnlyList<StratTemplateLine>? Lines = null);

/// <summary>One line of a template step: a slot and its place, or null for none.</summary>
public sealed record StratTemplateLine(string Slot, string? To = null);

/// <summary>A skeleton a new strat starts from: its type, side, site, slot roles and steps.</summary>
/// <param name="Id">A stable key, for the menus.</param>
/// <param name="Group">The menu entry the site variants share.</param>
/// <param name="Label">The entry's own label, and the new strat's name.</param>
/// <param name="Type">One of <see cref="StratVocabulary.Types" />.</param>
/// <param name="Side"><c>T</c>, <c>CT</c>, or null for either.</param>
/// <param name="TargetSite"><c>A</c>, <c>B</c> or null, as the validator wants it for <paramref name="Type" />.</param>
/// <param name="Roles">One role per slot, A to E; null leaves the slot's role alone.</param>
/// <param name="Steps">Authoring order; <c>atSeconds</c> never increases along it.</param>
public sealed record StratTemplate(
    string Id,
    string Group,
    string Label,
    string Type,
    string? Side,
    string? TargetSite,
    IReadOnlyList<string?> Roles,
    IReadOnlyList<StratTemplateStep> Steps)
{
    /// <summary>The new strat's name: the group, then the label when it says more.</summary>
    public string Name => string.Equals(Group, Label, StringComparison.Ordinal) ? Label : Group + " " + Label;

    /// <summary>Whether a strat on <paramref name="side" /> may start from this template.</summary>
    public bool AppliesTo(string side) => Side is null || string.Equals(Side, side, StringComparison.Ordinal);
}

/// <summary>
///     The step templates New Strat and Apply Template offer. Only <c>BombsiteA</c> and <c>BombsiteB</c> are
///     places on every shipped map, so every other place is left empty and named in the note.
/// </summary>
public static class StratTemplates
{
    private const string All = StratVocabulary.ActorAll;

    private static readonly string?[] ExecuteRoles = ["entry", "support", "smokes", "flashes", "lurk"];

    /// <summary>Every template, in menu order: T side first, then either side, then CT.</summary>
    public static readonly IReadOnlyList<StratTemplate> Templates =
    [
        Execute("A"), Execute("B"),
        Rush("A"), Rush("B"),
        Split("A"), Split("B"),
        Fake("B", "A"), Fake("A", "B"),
        Default(),
        AntiEco(),
        Setup(),
        Retake("A"), Retake("B")
    ];

    /// <summary>The template with <paramref name="id" />, or null.</summary>
    public static StratTemplate? Find(string? id) =>
        id is null ? null : Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>The side a new strat from <paramref name="template" /> takes: its own, else <paramref name="fallback" />.</summary>
    public static string SideFor(StratTemplate template, string fallback) => template.Side ?? fallback;

    /// <summary>The template's steps as new strat steps, each with a fresh id and no positions.</summary>
    /// <param name="template">The template.</param>
    /// <param name="roundSeconds">The strat's round length; a round-start step is clamped to it.</param>
    public static List<StratStep> BuildSteps(StratTemplate template, double roundSeconds = StratClock.DefaultRoundSeconds)
    {
        ArgumentNullException.ThrowIfNull(template);
        return
        [
            .. template.Steps.Select(s => new StratStep
            {
                Id = Guid.NewGuid(),
                AtSeconds = Math.Min(s.AtSeconds, roundSeconds),
                Actor = s.Actor,
                Verb = s.Verb,
                To = s.To is null ? null : new PlaceRef { Place = s.To },
                Utility = s.Utility is null ? null : new UtilityRef { Kind = s.Utility },
                Note = s.Note,
                Assignments = s.Lines is null
                    ? null
                    : [.. s.Lines.Select(l => new StepAssignment { Slot = l.Slot, To = l.To is null ? null : new PlaceRef { Place = l.To } })]
            })
        ];
    }

    /// <summary>
    ///     Fills a new strat from <paramref name="template" />: type, site, the roles of unnamed slots, and the steps
    ///     after whatever is there (the spawn seed). Run after <see cref="StratSpawns.Seed" />.
    /// </summary>
    /// <param name="document">A new strat.</param>
    /// <param name="template">The template.</param>
    public static void Apply(StratDocument document, StratTemplate template)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(template);
        document.Type = template.Type;
        document.TargetSite = template.TargetSite;
        for (int i = 0; i < document.Slots.Count && i < template.Roles.Count; i++)
        {
            if (string.IsNullOrEmpty(document.Slots[i].Role) && template.Roles[i] is { } role)
            {
                document.Slots[i].Role = role;
            }
        }

        document.Steps.AddRange(BuildSteps(template, document.Clock.RoundSeconds));
    }

    /// <summary>
    ///     Whether <paramref name="document" /> has no steps beyond the spawn seed: none, or one round-start
    ///     <c>all hold</c> with no place or utility. A captured or mined strat never qualifies: its freeze-end
    ///     step has the seed's shape but holds real positions.
    /// </summary>
    public static bool HasOnlySeed(StratDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Origin is not null || document.Tags.Contains(MinedStratBuilder.Tag, StringComparer.Ordinal))
        {
            return false;
        }

        if (document.Steps.Count == 0)
        {
            return true;
        }

        StratStep first = document.Steps[0];
        return document.Steps.Count == 1
               && first.AtSeconds >= document.Clock.RoundSeconds
               && string.Equals(first.Actor, All, StringComparison.Ordinal)
               && string.Equals(first.Verb, "hold", StringComparison.Ordinal)
               && first.From is null && first.To is null && first.Utility is null
               && !StratStepLines.HasLines(first)
               && string.IsNullOrEmpty(first.Note);
    }

    /// <summary>
    ///     The ops that apply <paramref name="template" /> to an open strat, for one undo entry. Empty when the
    ///     strat has steps beyond the seed or is on a side the template is not for.
    /// </summary>
    /// <param name="document">The open strat.</param>
    /// <param name="template">The template.</param>
    public static IReadOnlyList<PatchOp> Ops(StratDocument document, StratTemplate template)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(template);
        if (!HasOnlySeed(document) || !template.AppliesTo(document.Side))
        {
            return [];
        }

        List<PatchOp> ops = [];
        if (!string.Equals(document.Type, template.Type, StringComparison.Ordinal))
        {
            ops.Add(PatchOp.ReplaceOp("/type", JsonValue.Create(document.Type), JsonValue.Create(template.Type)));
        }

        if (!string.Equals(document.TargetSite, template.TargetSite, StringComparison.Ordinal))
        {
            ops.Add(PatchOp.ReplaceOp("/targetSite", Value(document.TargetSite), Value(template.TargetSite)));
        }

        for (int i = 0; i < document.Slots.Count && i < template.Roles.Count; i++)
        {
            if (string.IsNullOrEmpty(document.Slots[i].Role) && template.Roles[i] is { } role)
            {
                ops.Add(PatchOp.ReplaceOp($"/slots/{i}/role", Value(document.Slots[i].Role), JsonValue.Create(role)));
            }
        }

        // Concrete indices: the inverse of an add has to name the element it removes.
        int index = document.Steps.Count;
        foreach (StratStep step in BuildSteps(template, document.Clock.RoundSeconds))
        {
            ops.Add(PatchOp.AddOp($"/steps/{index++}", JsonSerializer.SerializeToNode(step, StratJsonContext.Default.StratStep)));
        }

        return ops;
    }

    private static JsonValue? Value(string? text) => text is null ? null : JsonValue.Create(text);

    private static string Site(string site) => "Bombsite" + site;

    private static StratTemplate Execute(string site) => new(
        "execute-" + site.ToLowerInvariant(), "Execute", site, "execute", StratVocabulary.SideT, site, ExecuteRoles,
        [
            new(75, All, "wait", Note: "group up for the " + site + " execute"),
            new(62, "C", "throw", Utility: "smoke", Note: "CT-side smoke"),
            new(60, "C", "throw", Utility: "smoke", Note: "cross smoke"),
            new(58, "D", "throw", Utility: "molotov", Note: "clear the close corner"),
            new(55, "D", "throw", Utility: "flash", Note: "entry flash"),
            new(53, "A", "move", Site(site), Note: "entry"),
            new(52, "B", "move", Site(site), Note: "trade the entry"),
            new(50, "E", "hold", Note: "lurk: cut the rotation"),
            new(45, "B", "plant", Site(site), Note: "plant for the post-plant"),
            new(40, All, "hold", Note: "post-plant crossfire")
        ]);

    private static StratTemplate Rush(string site) => new(
        "rush-" + site.ToLowerInvariant(), "Rush", site, "rush", StratVocabulary.SideT, site,
        ["entry", "support", "smokes", "flashes", "trade"],
        [
            new(110, "C", "throw", Utility: "smoke", Note: "smoke the CT side on the run"),
            new(108, "D", "throw", Utility: "flash", Note: "flash for the entry"),
            new(106, All, "move", Site(site), Note: "run the site together, entry first"),
            new(95, "B", "plant", Site(site)),
            new(90, All, "hold", Note: "post-plant")
        ]);

    private static StratTemplate Split(string site) => new(
        "split-" + site.ToLowerInvariant(), "Split", site, "split", StratVocabulary.SideT, site,
        ["entry", "support", "smokes", "split entry", "split support"],
        [
            new(80, All, "hold", Note: "A: main group on the main route; D: split group on the second route",
                Lines: [new("A"), new("D")]),
            new(62, "C", "throw", Utility: "smoke", Note: "main group: CT-side smoke"),
            new(60, "B", "throw", Utility: "flash", Note: "main group: entry flash"),
            new(58, "E", "throw", Utility: "flash", Note: "split group: flash from the other side"),
            new(55, All, "move", Note: "main and split entries at the same time",
                Lines: [new("A", Site(site)), new("D", Site(site))]),
            new(48, "C", "plant", Site(site)),
            new(42, All, "hold", Note: "post-plant")
        ]);

    private static StratTemplate Fake(string fakeSite, string site) => new(
        "fake-" + fakeSite.ToLowerInvariant() + "-" + site.ToLowerInvariant(), "Fake", fakeSite + ", hit " + site, "fake",
        StratVocabulary.SideT, site, ["entry", "support", "fake", "fake", "lurk"],
        [
            new(85, "C", "fake", Site(fakeSite), "smoke", "show utility at " + fakeSite),
            new(83, "D", "fake", Site(fakeSite), "flash", "make noise at " + fakeSite),
            new(75, All, "call", Note: "read the rotation"),
            new(62, "E", "throw", Utility: "smoke", Note: site + ": CT-side smoke"),
            new(60, "B", "throw", Utility: "flash", Note: site + ": entry flash"),
            new(58, All, "move", Site(site), Note: "hit " + site + ", entry first"),
            new(50, "B", "plant", Site(site)),
            new(45, All, "hold", Note: "post-plant")
        ]);

    private static StratTemplate Default() => new(
        "default", "Default", "Default", "default", StratVocabulary.SideT, null,
        ["entry", "support", "mid", "support", "lurk"],
        [
            new(105, "C", "hold", Note: "take mid control"),
            new(105, "E", "hold", Note: "lurk: hold the far side, report rotations"),
            new(100, "D", "throw", Utility: "smoke", Note: "smoke to take space"),
            new(95, "A", "peek", Note: "take the first map-control fight with a trade"),
            new(75, All, "call", Note: "read the info, call the site"),
            new(60, All, "wait", Note: "regroup for the late hit")
        ]);

    private static StratTemplate AntiEco() => new(
        "anti-eco", "Anti-eco", "Anti-eco", "anti-eco", null, null, [null, null, null, null, null],
        [
            new(110, All, "hold", Note: "play in pairs at range; do not give up a rifle"),
            new(90, All, "call", Note: "track their kills and what they picked up"),
            new(60, All, "wait", Note: "let them come; no dry peeks")
        ]);

    private static StratTemplate Setup() => new(
        "setup", "Setup", "2-1-2 hold", "setup", StratVocabulary.SideCt, null,
        ["A anchor", "A support", "rotator", "B anchor", "B support"],
        [
            new(115, All, "hold", Note: "A and D anchor, B and E take the second angles, C holds the middle and rotates on the call",
                Lines: [new("A", Site("A")), new("B", Site("A")), new("C"), new("D", Site("B")), new("E", Site("B"))]),
            new(105, "C", "throw", Utility: "smoke", Note: "early smoke to slow the push"),
            new(100, "D", "throw", Utility: "molotov", Note: "stall an early rush"),
            new(75, All, "call", Note: "call the rotation on first contact")
        ]);

    private static StratTemplate Retake(string site) => new(
        "retake-" + site.ToLowerInvariant(), "Retake", site, "retake", StratVocabulary.SideCt, site,
        ["smokes", "flashes", "entry", "defuser", "trade"],
        [
            new(-2, All, "call", Note: "bomb down on " + site + ": call where it is"),
            new(-4, All, "wait", Note: "group up outside " + site),
            new(-8, "A", "throw", Utility: "smoke", Note: "cut an angle off the site"),
            new(-10, "B", "throw", Utility: "flash", Note: "flash the site"),
            new(-11, "C", "throw", Utility: "molotov", Note: "clear the close corner"),
            new(-12, All, "move", Site(site), Note: "retake together, trade every fight"),
            new(-25, "D", "defuse", Site(site), Note: "defuse behind the smoke")
        ]);
}
