using Microsoft.Extensions.DependencyInjection;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     An extension's entry type: the type the manifest's <c>entryType</c> names. The host creates it with its
///     public parameterless constructor, reads the DI-free members, calls <see cref="Register" /> while it
///     builds its container, and calls <see cref="Contribute" /> once the container exists.
/// </summary>
public interface IExtension
{
    /// <summary>Reverse-DNS id, equal to the manifest's <c>id</c>. A persisted key: never rename it.</summary>
    string Id { get; }

    /// <summary>
    ///     The extension's master switch: the id of its <see cref="ExtensionFeatureKind.Extension" /> feature.
    ///     Must start with <c>pack.</c>. A persisted key.
    /// </summary>
    string FeatureId { get; }

    /// <summary>
    ///     Every gate the extension declares: exactly one <see cref="ExtensionFeatureKind.Extension" /> feature
    ///     whose id is <see cref="FeatureId" />, plus its tabs and sub-features.
    /// </summary>
    IEnumerable<ExtensionFeature> Features { get; }

    /// <summary>Commands with default key gestures. Read without DI, so the keybind settings can list them.</summary>
    IEnumerable<CommandDescriptor> Commands => Array.Empty<CommandDescriptor>();

    /// <summary>The focus scopes <see cref="Commands" /> name beyond the tab's own two. Read without DI.</summary>
    IEnumerable<CommandScope> CommandScopes => Array.Empty<CommandScope>();

    /// <summary>The processing-queue job kinds the extension submits work under. Read without DI.</summary>
    IEnumerable<ExtensionJobKind> JobKinds => Array.Empty<ExtensionJobKind>();

    /// <summary>
    ///     Adds the extension's services. Runs whether the extension is on or off, so register factories
    ///     and do no work here: the switch can turn the extension on later in the same session.
    /// </summary>
    void Register(IServiceCollection services);

    /// <summary>
    ///     Hands the host what the extension adds to the app. <paramref name="services" /> is the built
    ///     container; resolve lazily inside factories where you can, since this runs during startup.
    /// </summary>
    void Contribute(IExtensionContributions contributions, IServiceProvider services);
}
