#region

using System.Reflection;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>A generated command and every production file that names it.</summary>
/// <param name="Owner">The declaring view-model, short name.</param>
/// <param name="Name">The command property, e.g. <c>ToggleDisplayModeCommand</c>.</param>
/// <param name="Sites">Repo-relative paths of production sources that mention it.</param>
internal sealed record CommandBinding(string Owner, string Name, IReadOnlyList<string> Sites)
{
    public bool IsReachable => Sites.Count > 0;

    public string Describe() =>
        $"{Owner}.{Name} — {(Sites.Count == 0 ? "BOUND NOWHERE" : string.Join(", ", Sites.Take(3)))}";
}

/// <summary>
///     <b>D6 §4 guard 2 — every generated command on a Playback2D view-model is reachable from production.</b>
///     <para>
///         G3: <b>the XAML binds around the code path that does the extra work.</b> The AUTO level-follow
///         chip is a <c>ToggleButton</c> with <c>IsChecked="{Binding IsAutoEnabled}"</c>, which reaches the
///         property and skips <c>EnableAutoCommand</c> — the only path that raised <c>SettingsChanged</c>.
///         So AUTO applied instantly, looked right, and was forgotten on the next launch. A string-based
///         binding makes "is this command used?" invisible to the compiler, to the analyzer and to a
///         C#-only grep, and the test drove the command the UI does not take.
///     </para>
///     <para>
///         <b>Reflection for the question, source for the answer.</b> Which commands exist is metadata (a
///         public get-only property in <c>CommunityToolkit.Mvvm.Input</c>); whether anything names one is
///         text, because an <c>.axaml</c> binding is a string and compiles to nothing. Doc comments are
///         stripped from the corpus first — <c>&lt;see cref="EnableAutoCommand" /&gt;</c> is not a binding.
///     </para>
///     <para>
///         <b>Known limitation, stated rather than hidden:</b> the match is by name, so two view-models
///         with a same-named command share evidence (<c>SelectCommand</c> exists on both
///         <c>LevelStripViewModel</c> and the shell's inspector card). That can only ever HIDE a defect,
///         never invent one, and every match is printed with its file so an implausible attribution is
///         visible in the log.
///     </para>
/// </summary>
public class Playback2DCommandBindingTests
{
    /// <summary>
    ///     Commands knowingly reachable from nowhere. The reason is the entry; a bare name defeats the guard.
    ///     <para>
    ///         <b>Empty, and that is the point.</b> Round 3A dropped <c>[RelayCommand]</c> from
    ///         <c>Playback2DTabViewModel.FollowPlayer</c> and <c>.ClearFollow</c> — both were always
    ///         called as methods (the card list's selection hook, the Escape binding, the follow funnel),
    ///         so the generated wrappers were surface with no consumer — and the two entries that named
    ///         them went with the attributes. An entry added here needs a reason and a delete-condition;
    ///         <c>TheUnboundAllowList_NamesExactlyTheCommandsThatWouldFail</c> deletes it for you the day
    ///         it stops being true.
    ///     </para>
    /// </summary>
    private static readonly Dictionary<string, string> _unboundByDesign = new(StringComparer.Ordinal);

    [Test]
    public async Task EveryPlayback2dCommand_IsNamedByAnAxamlOrByProductionCSharp()
    {
        List<CommandBinding> commands = Analyse(Playback2DWholeGraph.ModuleTypes);

        foreach (CommandBinding command in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"[command-binding] {(command.IsReachable ? "ok  " : "DEAD")} {command.Describe()}");
        }

        // A floor: a namespace rename that emptied the type set would otherwise pass this silently.
        await Assert.That(commands.Count).IsGreaterThanOrEqualTo(15)
            .Because("the module's view-models carry the D-track command set, not a stub");

        List<string> unbound = commands
            .Where(c => !c.IsReachable && !_unboundByDesign.ContainsKey($"{c.Owner}.{c.Name}"))
            .Select(c => c.Describe())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join("; ", unbound)).IsEqualTo("")
            .Because("a command bound nowhere is either dead surface or — the AUTO toggle's case — a "
                     + "control that reaches the property and skips the work the command also does");
    }

    /// <summary>
    ///     <b>D6 round 3A, findings from the guard's own allow-list.</b> <c>FollowPlayer(int)</c> and
    ///     <c>ClearFollow()</c> carried <c>[RelayCommand]</c> and nothing anywhere bound the generated
    ///     wrappers — every caller invokes the METHODS directly (the card list's selection hook, the
    ///     Escape binding, the follow funnel, the demo-reset path).
    ///     <para>
    ///         Named explicitly rather than left to the sweep above, because the sweep proves "no command
    ///         is unreachable" and this proves "these two commands are gone". A future edit that
    ///         re-attaches the attribute AND binds the wrapper somewhere would satisfy the sweep while
    ///         re-introducing two paths to the follow funnel — which is the thing worth refusing.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments("FollowPlayerCommand")]
    [Arguments("ClearFollowCommand")]
    public async Task TheFollowMethods_ExposeNoGeneratedCommand(string command)
    {
        PropertyInfo? property = typeof(Playback2DTabViewModel)
            .GetProperty(command, BindingFlags.Public | BindingFlags.Instance);

        Console.WriteLine($"[command-binding] {command} → {(property is null ? "absent" : "PRESENT")}");

        await Assert.That(property).IsNull()
            .Because("a command on a method every caller invokes directly is generated surface with no "
                     + "consumer — and a second route into the single follow funnel besides");

        // The method itself must survive: this was a deletion of the attribute, never of the behaviour.
        await Assert.That(typeof(Playback2DTabViewModel)
                .GetMethod(command[..^"Command".Length], BindingFlags.Public | BindingFlags.Instance))
            .IsNotNull();
    }

    /// <summary>
    ///     The allow-list must name exactly the commands that would fail without it, and each entry must
    ///     say why. An entry that has become stale is deleted, not left as cover.
    /// </summary>
    [Test]
    public async Task TheUnboundAllowList_NamesExactlyTheCommandsThatWouldFail()
    {
        HashSet<string> failing = Analyse(Playback2DWholeGraph.ModuleTypes)
            .Where(c => !c.IsReachable)
            .Select(c => $"{c.Owner}.{c.Name}")
            .ToHashSet(StringComparer.Ordinal);

        Console.WriteLine($"[command-binding] without the allow-list: {string.Join(", ", failing)}");

        await Assert.That(string.Join(", ", _unboundByDesign.Keys.Where(k => !failing.Contains(k))))
            .IsEqualTo("")
            .Because("an allow-list entry for a command that IS bound now is dead weight");
        await Assert.That(_unboundByDesign.Values.All(r => r.Length > 40)).IsTrue()
            .Because("§4: an allow-list entry must carry WHY, not just a name");
    }

    /// <summary>
    ///     The self-check, in both directions against the REAL corpus: a canary command that exists
    ///     nowhere in <c>src/</c> or <c>tools/</c> is reported, and a command the XAML genuinely binds is
    ///     not. Without the first half this guard could be satisfied by a matcher that returns "found" for
    ///     everything.
    /// </summary>
    [Test]
    public async Task TheScan_ReportsACommandNothingBinds_AndClearsOneTheXamlDoesBind()
    {
        List<CommandBinding> canaries = Analyse([typeof(CommandGuardCanaryViewModel)]);
        foreach (CommandBinding canary in canaries)
        {
            Console.WriteLine($"[command-canary] {canary.Describe()}");
        }

        await Assert.That(canaries.Count).IsEqualTo(1);
        await Assert.That(canaries[0].IsReachable).IsFalse()
            .Because("nothing in src/ or tools/ names it — which is the AUTO toggle's exact situation");

        CommandBinding bound = Analyse(Playback2DWholeGraph.ModuleTypes)
            .Single(c => c.Name == "ToggleDisplayModeCommand");
        Console.WriteLine($"[command-canary] positive control: {bound.Describe()}");

        await Assert.That(bound.IsReachable).IsTrue()
            .Because("Playback2DView.axaml binds it — a matcher that could not find this would make every "
                     + "assertion above a false alarm");
        await Assert.That(bound.Sites.Any(s => s.EndsWith(".axaml", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    ///     The matcher itself, against a synthetic corpus. Whole-word or nothing (so
    ///     <c>UndoCommand</c> is not evidence for <c>RedoCommand</c>), and a doc comment is not a binding —
    ///     the mistake this whole audit is about is believing a comment that describes the missing half.
    /// </summary>
    [Test]
    public async Task TheMatcher_IgnoresDocComments_AndDoesNotMatchInsideALongerIdentifier()
    {
        await Assert.That(Mentions("FooCommand", "Command=\"{Binding FooCommand}\"")).IsTrue();
        await Assert.That(Mentions("FooCommand", "vm.FooCommand.Execute(null);")).IsTrue();
        await Assert.That(Mentions("FooCommand", "MyFooCommandThing = 1;")).IsFalse();
        await Assert.That(Mentions("FooCommand", "    /// <see cref=\"FooCommand\" /> is the one\n")).IsFalse()
            .Because("the corpus blanks /// lines, so a promise in prose can never stand in for a binding");
    }

    // ── The analysis ────────────────────────────────────────────────────────────────────────────────

    private static List<CommandBinding> Analyse(IEnumerable<Type> types)
    {
        List<CommandBinding> commands = [];

        foreach (Type type in types)
        {
            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                // The generator emits `public IRelayCommand XCommand => ...`; the namespace test catches
                // every toolkit command shape (relay, async, generic) without a compile-time dependency
                // on which one this property happens to be.
                if (property.PropertyType.Namespace != "CommunityToolkit.Mvvm.Input")
                {
                    continue;
                }

                List<string> sites = Playback2DWholeGraph.ProductionSources
                    .Where(f => Mentions(property.Name, f.Text))
                    .Select(f => f.Path)
                    .ToList();

                commands.Add(new CommandBinding(Short(type), property.Name, sites));
            }
        }

        return commands;
    }

    private static bool Mentions(string name, string text) =>
        text.Contains(name, StringComparison.Ordinal)
        && Regex.IsMatch(StripDocLines(text), $@"\b{Regex.Escape(name)}\b");

    // ProductionSources has already done this; the matcher repeats it so the synthetic self-check above
    // exercises the same rule the corpus was built with.
    private static string StripDocLines(string text) =>
        string.Join('\n', text.Split('\n')
            .Select(l => l.TrimStart().StartsWith("///", StringComparison.Ordinal) ? "" : l));

    private static string Short(Type type) => type.Name;
}

/// <summary>
///     The canary for
///     <see cref="Playback2DCommandBindingTests.TheScan_ReportsACommandNothingBinds_AndClearsOneTheXamlDoesBind" />.
///     Its command name appears in no production source, which is the whole point — if this ever starts
///     coming back "reachable", the corpus has grown to include something it must not.
/// </summary>
internal sealed class CommandGuardCanaryViewModel
{
    /// <summary>Deliberately un-XAML-able: no view binds it and none ever should.</summary>
    public IRelayCommand NeverBoundAnywhereCanaryCommand { get; } = new RelayCommand(static () => { });
}
