#region

using System.Reflection;
using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>One production <c>new T(...)</c> and which of T's null-defaulted seams it mentions.</summary>
/// <param name="Type">The constructed type, short name.</param>
/// <param name="Site">Repo-relative path and line of the call.</param>
/// <param name="Omitted">Optional null-defaulted parameters the call site does not mention at all.</param>
/// <param name="ExplicitlyNull">Ones it names and passes <c>null</c> to — a recorded decision, not a gap.</param>
internal sealed record CompositionSite(
    string Type,
    string Site,
    IReadOnlyList<string> Omitted,
    IReadOnlyList<string> ExplicitlyNull)
{
    public string Describe() =>
        $"{Type} at {Site} — omits {string.Join(", ", Omitted)}"
        + (ExplicitlyNull.Count > 0 ? $" (explicit null: {string.Join(", ", ExplicitlyNull)})" : "");
}

/// <summary>
///     <b>D6 §4 guard 4 — a production composition site names every seam the service offers.</b>
///     <para>
///         G1, <b>the optional constructor parameter</b>, is the gap that produced the worst finding of the
///         audit. <c>SceneExportRunner(setup, surfaces = null, ffmpegDir = null, consent = null, log =
///         null, probe = null)</c> had one production caller passing <b>one</b> argument. Tests supplied
///         the rest, so the suite proved every branch while the shipped composition took none of them: the
///         in-app ffmpeg download could never run, the GPU backend was unreachable, and every line the
///         encoder and ffmpeg wrote went to the floor. Nothing in the language, the compiler or the suite
///         distinguishes <i>"the test does not need this"</i> from <i>"production forgot it"</i>.
///     </para>
///     <para>
///         <b>The rule is MENTION, not non-null.</b> <c>fileExists: null</c> at a call site is a decision
///         somebody made and a reader can see; leaving the parameter out is the thing that is invisible.
///         So the guard asks that every null-defaulted parameter appear — positionally or by name — and
///         prints the explicit nulls beside the omissions so neither is silent.
///     </para>
///     <para>
///         <b>Source for the arguments, IL for the existence.</b> C# materialises omitted optional
///         arguments AT THE CALL SITE, so the IL for <c>new SceneExportRunner(setup)</c> and for
///         <c>new SceneExportRunner(setup, null, null, null, null)</c> is identical — an omission is
///         literally not a fact about the compiled program. The argument list therefore has to be read from
///         source. IL still answers the other half: whether production constructs the type at all, so a
///         call site the source parser cannot read is REPORTED rather than counted as absent.
///     </para>
///     <para>
///         <b>Scope</b> is the App head's own Playback2D services and view-models — the things composed by
///         <c>App.axaml.cs</c> and by the module's tab. Core's layers take <c>HudStyle? style = null</c>
///         and <c>TextBlobCache? text = null</c> as genuine styling defaults and are built by
///         <c>SceneLayerCatalog</c>, not by app composition; sweeping them in would make this a list of 20
///         non-defects, which is how a guard gets switched off.
///     </para>
/// </summary>
public class Playback2DCompositionTests
{
    /// <summary>
    ///     Seams knowingly left unmentioned. The reason is the entry.
    ///     <para>
    ///         <b>Empty, and that is the point.</b> Round 3A spelled out all three of
    ///         <c>SceneExportRunner</c>'s seams at the sole production composition —
    ///         <c>surfaces: RenderSurfaceProviderFactory.CreateCpu</c> (the only backend
    ///         <c>SceneExportSession</c> accepts, and now the landing site for the <c>RenderBackend</c>
    ///         key when C2 Stage 1 lifts that refusal), <c>managedFfmpegDirectory</c> and
    ///         <c>encoderProbe</c> — and the three entries that named them went with the omissions.
    ///     </para>
    /// </summary>
    private static readonly Dictionary<string, string> _omittedByDesign = new(StringComparer.Ordinal);

    [Test]
    public async Task EveryProductionCompositionSite_NamesEverySeamTheServiceOffers()
    {
        (List<CompositionSite> sites, List<string> unreadable) = Analyse();

        foreach (CompositionSite site in sites)
        {
            Console.WriteLine($"[composition] {(site.Omitted.Count == 0 ? "ok  " : "GAP ")} {site.Describe()}");
        }

        // A construction the IL sees and the source parser could not read. Reported rather than skipped:
        // an unreadable site is where the next SceneExportRunner would hide.
        await Assert.That(string.Join("; ", unreadable)).IsEqualTo("")
            .Because("production constructs this type somewhere the argument reader could not parse — a "
                     + "target-typed `new(...)`, or a call list this parser needs to grow for");

        List<string> gaps = sites
            .SelectMany(s => s.Omitted.Select(p => (Site: s, Parameter: p)))
            .Where(g => !_omittedByDesign.ContainsKey($"{g.Site.Type}.{g.Parameter}"))
            .Select(g => $"{g.Site.Type}.{g.Parameter} omitted at {g.Site.Site}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join("; ", gaps)).IsEqualTo("")
            .Because("an optional seam the shipped composition never mentions is a feature only the "
                     + "tests have ever exercised — pass it, name it null, or make it required");
    }

    /// <summary>The allow-list must be load-bearing, and each entry must say why.</summary>
    [Test]
    public async Task TheOmissionAllowList_NamesExactlyTheSeamsThatWouldFail()
    {
        (List<CompositionSite> sites, List<string> _) = Analyse();
        HashSet<string> failing = sites
            .SelectMany(s => s.Omitted.Select(p => $"{s.Type}.{p}"))
            .ToHashSet(StringComparer.Ordinal);

        Console.WriteLine($"[composition] without the allow-list: {string.Join(", ", failing)}");

        // The reach check is "the scan CLASSIFIED seams", not "the scan found a defect".
        //
        // It used to be `failing.IsNotEmpty()`, which was true only while SceneExportRunner's three
        // omissions stood — so closing them (round 3A) would have turned a guard green into a guard red
        // for the best possible reason. A guard whose passing condition is that a defect still exists
        // cannot survive the defect being fixed, and this one has a better question available: the
        // parameter reader is exercised whenever a site names a seam explicitly, and
        // Playback2DExportDialogViewModel names three nulls. If seams stop being classified at all, the
        // guard above has gone quiet, and that is what this now catches.
        List<CompositionSite> classified =
            [.. sites.Where(s => s.Omitted.Count + s.ExplicitlyNull.Count > 0)];
        Console.WriteLine($"[composition] sites with classified seams: {classified.Count}");

        await Assert.That(classified).IsNotEmpty()
            .Because("if no site has a seam to classify, the reader has stopped reading argument lists "
                     + "and every 'ok' above is a rubber stamp");
        await Assert.That(string.Join(", ", _omittedByDesign.Keys.Where(k => !failing.Contains(k))))
            .IsEqualTo("")
            .Because("an entry for a seam that IS passed now is dead weight");
        await Assert.That(_omittedByDesign.Values.All(r => r.Length > 40)).IsTrue()
            .Because("§4: an allow-list entry must carry WHY, not just a name");
    }

    /// <summary>
    ///     The self-check, on the one piece of machinery that could quietly stop working: the argument
    ///     reader. If it returned "every parameter was mentioned" for everything, the guard above would be
    ///     a rubber stamp and nothing else in the suite would notice.
    /// </summary>
    [Test]
    public async Task TheArgumentReader_CountsPositionals_NamesNamed_AndSurvivesLambdasAndComments()
    {
        (int Positional, HashSet<string> Named)? plain = Read("new Foo(a, b, c)");
        await Assert.That(plain!.Value.Positional).IsEqualTo(3);
        await Assert.That(plain.Value.Named).IsEmpty();

        // The real SceneExportRunner call's shape: one lambda, one named argument.
        (int Positional, HashSet<string> Named)? mixed =
            Read("new Foo(request => Build(host, request), log: Append)");
        await Assert.That(mixed!.Value.Positional).IsEqualTo(1)
            .Because("the comma inside the lambda's own call is nested, not an argument separator");
        await Assert.That(mixed.Value.Named).Contains("log");

        // A collection expression and an object initialiser both carry top-level-looking commas.
        await Assert.That(Read("new Foo([a, b], new Bar { X = 1, Y = 2 })")!.Value.Positional).IsEqualTo(2);

        // A ternary's colon is not a named argument, and a comment between arguments is not one either.
        (int Positional, HashSet<string> Named)? tricky =
            Read("new Foo(x ? y : z,\n  // why this one\n  gate: g)");
        await Assert.That(tricky!.Value.Positional).IsEqualTo(1);
        await Assert.That(tricky.Value.Named).Contains("gate");

        // A string containing a paren must not close the argument list early.
        await Assert.That(Read("new Foo(\")\", b)")!.Value.Positional).IsEqualTo(2);

        await Assert.That(Read("new Foo()")!.Value.Positional).IsEqualTo(0);
        await Assert.That(Read("new Foo(a, b")).IsNull().Because("an unbalanced list must report itself");
    }

    /// <summary>
    ///     Guards the guard's REACH. The whole assertion set above passes trivially if the type set, the
    ///     source corpus or the call-site regex ever finds nothing, and each of those has a plausible way
    ///     to break (a namespace rename, a moved repo root, a qualified call spelling).
    /// </summary>
    [Test]
    public async Task TheCompositionScan_IsActuallyFindingCallSites()
    {
        (List<CompositionSite> sites, List<string> _) = Analyse();

        Console.WriteLine($"[composition] production sources={Playback2DWholeGraph.ProductionSources.Count} "
                          + $"call sites={sites.Count}");

        await Assert.That(Playback2DWholeGraph.ProductionSources.Count).IsGreaterThan(200);
        await Assert.That(sites.Count).IsGreaterThanOrEqualTo(3);

        // The exemplar, by name: the qualified spelling in Playback2DTabViewModel
        // (`new ViewModels.Playback2D.Playback2DExportDialogViewModel(`) is the one the regex most easily
        // misses, and it is also the site that carries ten seams.
        await Assert.That(sites.Any(s => s.Type == "SceneExportRunner")).IsTrue();
        await Assert.That(sites.Any(s => s.Type == "Playback2DExportDialogViewModel")).IsTrue()
            .Because("a namespace-qualified `new` must be found, or the richest composition in the module "
                     + "is silently unguarded");
    }

    // ── The analysis ────────────────────────────────────────────────────────────────────────────────

    private static (List<CompositionSite> Sites, List<string> Unreadable) Analyse()
    {
        // The App head's own module services and view-models. Core/Pipeline are excluded deliberately —
        // see the class doc.
        Type[] candidates = typeof(Configuration.AppSettings).Assembly.GetTypes()
            .Where(t => Playback2DWholeGraph.IsModuleNamespace(t.Namespace))
            .Where(t => Seams(t).Length > 0)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        // Which of them production actually builds. IL, because it cannot be spelled around.
        HashSet<string> constructed = Playback2DWholeGraph
            .Scan(Playback2DWholeGraph.ProductionAssemblies,
                (type, member) => member == ".ctor"
                                  && candidates.Any(c => string.Equals(c.FullName, type, StringComparison.Ordinal)))
            .Where(s => s.Access == IlAccess.New)
            .Select(s => s.TargetType)
            .ToHashSet(StringComparer.Ordinal);

        List<CompositionSite> sites = [];
        List<string> unreadable = [];

        foreach (Type type in candidates)
        {
            if (!constructed.Contains(type.FullName!))
            {
                continue; // test-only surface: a different question, and not this guard's
            }

            ParameterInfo[] seams = Seams(type);
            int found = 0;

            foreach (Playback2DWholeGraph.SourceFile file in Playback2DWholeGraph.ProductionSources)
            {
                if (!file.Path.EndsWith(".cs", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in Regex.Matches(file.Text,
                             @"\bnew\s+(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*"
                             + Regex.Escape(type.Name) + @"\s*[({]"))
                {
                    found++;
                    string where = $"{file.Path}:{Line(file.Text, match.Index)}";

                    // `new T { ... }` takes the whole optional tail; there is no argument list to read.
                    (int Positional, HashSet<string> Named)? args =
                        file.Text[match.Index + match.Length - 1] == '{'
                            ? (0, [])
                            : Read(file.Text, match.Index + match.Length - 1);

                    if (args is null)
                    {
                        unreadable.Add($"{type.Name} at {where}");
                        continue;
                    }

                    List<string> omitted = [];
                    List<string> explicitNull = [];
                    foreach (ParameterInfo seam in seams)
                    {
                        if (seam.Position < args.Value.Positional)
                        {
                            continue; // supplied positionally
                        }

                        if (args.Value.Named.Contains(seam.Name!))
                        {
                            explicitNull.Add(seam.Name!);
                            continue;
                        }

                        omitted.Add(seam.Name!);
                    }

                    sites.Add(new CompositionSite(type.Name, where, omitted, explicitNull));
                }
            }

            if (found == 0)
            {
                unreadable.Add($"{type.Name} — IL says production constructs it, no `new {type.Name}(` "
                               + "found in source");
            }
        }

        return (sites, unreadable);
    }

    // The seams: optional constructor parameters whose default is null, on a SERVICE. Three exclusions,
    // each for a reason:
    //
    //  * a numeric or bool default is a tuning knob, not a collaborator production forgot to hand over;
    //  * a type with constructor OVERLOADS has no single "the composition", and no module service has any;
    //  * a POSITIONAL RECORD's trailing optional is a data default (`Annotations = null` on
    //    ExportSceneSetup means "no ink in this export"), not an injected seam — and records are the shape
    //    that gets built with a target-typed `new(...)`, which no regex can attribute to a type.
    private static ParameterInfo[] Seams(Type type)
    {
        if (type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            is not null)
        {
            return []; // record / record struct
        }

        ConstructorInfo[] ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length != 1)
        {
            return [];
        }

        return ctors[0].GetParameters()
            .Where(p => p is { IsOptional: true, HasDefaultValue: true } && p.DefaultValue is null)
            .ToArray();
    }

    private static (int Positional, HashSet<string> Named)? Read(string text) =>
        Read(text, text.IndexOf('('));

    /// <summary>
    ///     Reads the argument list starting at <paramref name="open" /> (the <c>(</c>). Returns null when
    ///     the list is unbalanced or runs off the end — an unreadable site is reported, never assumed empty.
    /// </summary>
    private static (int Positional, HashSet<string> Named)? Read(string text, int open)
    {
        if (open < 0 || open >= text.Length || text[open] != '(')
        {
            return null;
        }

        List<string> args = [];
        int depth = 0;
        int start = open + 1;
        int i = open;

        while (i < text.Length)
        {
            char c = text[i];

            if (c is '"' or '\'')
            {
                int end = SkipLiteral(text, i);
                if (end < 0)
                {
                    return null;
                }

                i = end;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int nl = text.IndexOf('\n', i);
                if (nl < 0)
                {
                    return null;
                }

                i = nl + 1;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return null;
                }

                i = close + 2;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    args.Add(text[start..i]);
                    HashSet<string> named = new(StringComparer.Ordinal);
                    int positional = 0;
                    foreach (string arg in args)
                    {
                        string bare = Uncomment(arg);
                        if (bare.Trim().Length == 0)
                        {
                            continue; // `new T()`
                        }

                        Match name = Regex.Match(bare, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:(?!:)");
                        if (name.Success)
                        {
                            named.Add(name.Groups[1].Value);
                        }
                        else
                        {
                            positional++;
                        }
                    }

                    return (positional, named);
                }

                if (depth < 0)
                {
                    return null;
                }
            }
            else if (c == ',' && depth == 1)
            {
                args.Add(text[start..i]);
                start = i + 1;
            }

            i++;
        }

        return null; // ran off the end
    }

    // Index just past a string / char literal, or -1 if it never closes. Verbatim and raw strings are
    // rejected (-1) rather than guessed at: they land in `unreadable`, which is a report, not a pass.
    private static int SkipLiteral(string text, int i)
    {
        if (text[i] == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
        {
            return -1; // raw string
        }

        if (i > 0 && (text[i - 1] == '@' || text[i - 1] == '$'))
        {
            return -1; // verbatim / interpolated: different escape rules, and no seam list uses one
        }

        char quote = text[i];
        for (int j = i + 1; j < text.Length; j++)
        {
            if (text[j] == '\\')
            {
                j++;
                continue;
            }

            if (text[j] == quote)
            {
                return j + 1;
            }

            if (text[j] == '\n')
            {
                return -1;
            }
        }

        return -1;
    }

    private static string Uncomment(string arg) =>
        Regex.Replace(Regex.Replace(arg, @"/\*.*?\*/", " ", RegexOptions.Singleline),
            @"//[^\n]*", " ");

    private static int Line(string text, int index) =>
        text.Take(index).Count(c => c == '\n') + 1;
}
