#region

using System.Reflection;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Builds synthetic <see cref="ParsedDemo" /> fixtures for tests that need a demo-shaped object
///     without a demo file — library/queue/stats surfaces where the assertion is about our own
///     plumbing, not about parsing.
///     <para>
///         <b>Why reflection.</b> <see cref="ParsedDemo" />'s constructor is internal, and these
///         tests used to reach it through an <c>InternalsVisibleTo</c> grant back when the parser
///         was a project in this repo. It is a NuGet package now and grants nothing to this
///         assembly, which is correct — a library should not know its consumers' test assemblies by
///         name. Reflection keeps that boundary intact.
///     </para>
///     <para>
///         The trade-off is real: a constructor change upstream breaks these tests at run time
///         rather than at compile time. <see cref="Create" /> is the single place that knows the
///         signature, and it throws a directed message rather than a bare
///         <see cref="NullReferenceException" /> when it can no longer bind. The durable fix is a
///         supported factory in CS2DemoKit.Parser — file it upstream and delete this file.
///     </para>
/// </summary>
internal static class SyntheticParsedDemo
{
    private static readonly ConstructorInfo _ctor = ResolveConstructor();

    /// <summary>
    ///     Mirrors the parser's internal constructor argument-for-argument. Defaults describe a
    ///     minimal well-formed demo, so a caller sets only what its assertion depends on.
    /// </summary>
    internal static ParsedDemo Create(
        IReadOnlyList<DemoFrame>? frames = null,
        IReadOnlyList<GameEvent>? allGameEvents = null,
        IReadOnlyDictionary<int, PlayerInfo>? players = null,
        RuntimeSchema? schema = null,
        string mapName = "de_test",
        int tickCount = 6400,
        float tickInterval = 1f / 64,
        string serverName = "test",
        string clientName = "test",
        string gameDirectory = "csgo",
        int buildNumber = 0,
        int serverStartTick = 0,
        int patchVersion = 0,
        string demoVersionName = "valve_demo_2",
        string demoVersionGuid = "",
        string addons = "",
        // DemoProfile.Unknown is a static property, not a constant, so it cannot be a default
        // parameter value; null means "unknown" and is resolved below.
        DemoProfile? profile = null) =>
        (ParsedDemo)_ctor.Invoke([
            frames ?? [],
            allGameEvents ?? [],
            players ?? new Dictionary<int, PlayerInfo>(),
            schema,
            mapName,
            tickCount,
            tickInterval,
            serverName,
            clientName,
            gameDirectory,
            buildNumber,
            serverStartTick,
            patchVersion,
            demoVersionName,
            demoVersionGuid,
            addons,
            profile ?? DemoProfile.Unknown
        ]);

    private static ConstructorInfo ResolveConstructor()
    {
        ConstructorInfo[] candidates = typeof(ParsedDemo)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // Match on arity rather than the full parameter-type list: a reordering upstream should
        // surface as a failing assertion in one test, while a genuine signature change surfaces
        // here, once, with an actionable message.
        ConstructorInfo? ctor = Array.Find(candidates, c => c.GetParameters().Length == 17);
        return ctor ?? throw new InvalidOperationException(
            "CS2DemoKit.Parser.ParsedDemo no longer has a 17-argument constructor, so synthetic "
            + "fixtures cannot be built. Update SyntheticParsedDemo.Create to the new signature, or "
            + "switch to a supported factory if the package has since added one. Found arities: "
            + string.Join(", ", Array.ConvertAll(candidates, c => c.GetParameters().Length)));
    }
}
