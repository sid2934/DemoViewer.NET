#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     The one place a headless consumer builds a layer stack. <c>dv2d</c> never reads a feature gate
///     or an <c>AppSettings</c> value (design §7.7) — it takes explicit ids — so the set of layers a
///     render can contain has to be enumerable from Pipeline alone.
///     <para>
///         <b>B1 extends this, and only this.</b> Today the stack is B0's single smoke layer; when the
///         seven real layers land, they are registered here and <c>--layers radar,markers</c> starts
///         working with no CLI change. The registered ids are the persisted keys from
///         <c>plans/00-overview.md</c> §3.3 and are never renamed.
///     </para>
/// </summary>
public static class SceneLayerCatalog
{
    /// <summary>The prefix every layer id carries; accepted but not required on the command line.</summary>
    public const string IdPrefix = "playback2d.";

    private static readonly IReadOnlyList<Registration> _registrations =
    [
        // B0's smoke layer. It draws no text, so it needs no font and rasterises identically on a CI
        // container with no fontconfig — which is what makes a byte-exact CPU golden lane possible at
        // all before B1's embedded-typeface work lands.
        new("playback2d.debuggrid", static () => new DebugGridLayer())
    ];

    /// <summary>Every layer id this build can register, in registration order.</summary>
    public static IReadOnlyList<string> KnownLayerIds { get; } =
        _registrations.Select(static r => r.Id).ToArray();

    /// <summary>
    ///     Builds a compositor holding the requested layers. The caller owns and disposes it (disposing
    ///     a compositor disposes its layers).
    /// </summary>
    /// <param name="include">Ids to register, or null for every known layer.</param>
    /// <param name="exclude">Ids to subtract from <paramref name="include" />.</param>
    /// <exception cref="ArgumentException">An id is not in <see cref="KnownLayerIds" />.</exception>
    public static SceneCompositor Create(IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null)
    {
        string[] unknown = [.. UnknownIds(include), .. UnknownIds(exclude)];
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"unknown layer id(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", KnownLayerIds)}",
                include is not null && UnknownIds(include).Count > 0 ? nameof(include) : nameof(exclude));
        }

        HashSet<string>? wanted = include is null
            ? null
            : new HashSet<string>(include.Select(Normalize), StringComparer.Ordinal);
        HashSet<string> unwanted = exclude is null
            ? []
            : new HashSet<string>(exclude.Select(Normalize), StringComparer.Ordinal);

        SceneCompositor compositor = new();
        try
        {
            foreach (Registration registration in _registrations)
            {
                if (wanted is not null && !wanted.Contains(registration.Id))
                {
                    continue;
                }

                if (unwanted.Contains(registration.Id))
                {
                    continue;
                }

                compositor.Add(registration.Create());
            }
        }
        catch
        {
            compositor.Dispose();
            throw;
        }

        return compositor;
    }

    /// <summary>The ids in <paramref name="ids" /> that no layer answers to. Empty when all are known.</summary>
    /// <param name="ids">Candidate ids, bare or prefixed.</param>
    public static IReadOnlyList<string> UnknownIds(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return [];
        }

        List<string> unknown = [];
        foreach (string id in ids)
        {
            string normalized = Normalize(id);
            if (!KnownLayerIds.Contains(normalized, StringComparer.Ordinal))
            {
                unknown.Add(id);
            }
        }

        return unknown;
    }

    /// <summary>
    ///     Canonicalises a command-line id: a bare word gets the <see cref="IdPrefix" />. Both spellings
    ///     are accepted because the design's JSON samples show bare names while the persisted keys are
    ///     prefixed; only the prefixed form is ever written back out.
    /// </summary>
    /// <param name="id">A bare or prefixed layer id.</param>
    public static string Normalize(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.StartsWith(IdPrefix, StringComparison.Ordinal) ? id : IdPrefix + id;
    }

    private sealed record Registration(string Id, Func<ISceneLayer> Create);
}
