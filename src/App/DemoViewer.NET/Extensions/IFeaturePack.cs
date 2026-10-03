#region

using DemoViewer.NET.Features;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     A first-party feature pack: a compiled-in unit the user can switch off as a whole. The pack owns
///     its feature descriptors, its DI registrations and its contributions to the shell; the
///     composition root enumerates packs instead of naming their parts. Users see a pack as an
///     "extension"; code keeps the word pack.
/// </summary>
public interface IFeaturePack
{
    /// <summary>Stable reverse-DNS id, e.g. <c>"net.demoviewer.stratbook"</c>. A persisted key.</summary>
    string Id { get; }

    /// <summary>
    ///     The umbrella gate id, e.g. <c>"pack.stratbook"</c>. Every descriptor in <see cref="Features" />
    ///     resolves off when this id does. Must start with <c>pack.</c>: the gate never fails open on
    ///     that prefix.
    /// </summary>
    string FeatureId { get; }

    /// <summary>
    ///     The pack's gate descriptors: exactly one <see cref="FeatureScope.Pack" /> entry for
    ///     <see cref="FeatureId" /> plus every tab and sub-feature it owns, each parented to it, to one of
    ///     its own tabs, or to a core tab (2D Playback's docked Strat Book surfaces); every non-Pack row is
    ///     stamped with <see cref="FeatureId" /> as its <see cref="FeatureDescriptor.OwnerPackId" /> on
    ///     composition, so it cascades off with the pack whatever its <c>ParentId</c> says. Appended to the
    ///     core catalog once, at startup.
    /// </summary>
    IEnumerable<FeatureDescriptor> Features { get; }

    /// <summary>
    ///     Adds the pack's registrations. Unconditional: factories are lazy, and a registration that
    ///     depends on the gate would make turning the pack on without a restart impossible.
    /// </summary>
    void Register(IServiceCollection services);

    /// <summary>Hands the pack's modules and other contributions to the shell, resolving deps from <paramref name="sp" />.</summary>
    void Contribute(IPackContributions contributions, IServiceProvider sp);
}
