namespace DemoViewer.NET.Extensions.Manifest;

/// <summary>
///     Whether an extension fits this host, and if not, why. One reason per check, in the order the checks
///     run: the manifest itself, the pack contract, CS2DemoKit, then the app release. The user-facing
///     message says "extension", never "pack".
/// </summary>
public abstract record PackCompatibility
{
    private PackCompatibility()
    {
    }

    /// <summary>True only for <see cref="Compatible" />.</summary>
    public bool IsCompatible => this is Compatible;

    /// <summary>Judges <paramref name="manifest" /> against <paramref name="host" />.</summary>
    public static PackCompatibility Check(ExtensionManifest manifest, ExtensionHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(host);
        if (!manifest.RequiresHost.Satisfies(host.ContractVersion))
        {
            return new HostContractMismatch(manifest.RequiresHost, host.ContractVersion);
        }

        if (!manifest.RequiresCs2DemoKit.Satisfies(host.Cs2DemoKitVersion))
        {
            return new Cs2DemoKitMismatch(manifest.RequiresCs2DemoKit, host.Cs2DemoKitVersion);
        }

        // An unstamped build is a developer build, which no release floor is meant to keep out.
        if (manifest.MinAppVersion is { } floor && host.AppVersion is { } app && app < floor)
        {
            return new AppTooOld(floor, app);
        }

        return Compatible.Instance;
    }

    /// <summary>
    ///     The reason in user terms, naming the extension by <paramref name="manifest" />'s name and
    ///     version when there is one and by <paramref name="fallbackName" /> otherwise. Null when compatible.
    /// </summary>
    public abstract string? Describe(ExtensionManifest? manifest, string fallbackName);

    private static string Subject(ExtensionManifest? manifest, string fallbackName) =>
        manifest is null ? fallbackName : $"{manifest.Name} {manifest.Version}";

    /// <summary>Every check passed.</summary>
    public sealed record Compatible : PackCompatibility
    {
        private Compatible()
        {
        }

        /// <summary>The one instance.</summary>
        public static Compatible Instance { get; } = new();

        /// <inheritdoc />
        public override string? Describe(ExtensionManifest? manifest, string fallbackName) => null;
    }

    /// <summary>The extension was built against a pack contract this app does not implement.</summary>
    public sealed record HostContractMismatch(VersionRange Required, SemVersion Actual) : PackCompatibility
    {
        /// <inheritdoc />
        public override string Describe(ExtensionManifest? manifest, string fallbackName) =>
            $"{Subject(manifest, fallbackName)} needs app contract {Required}; this app provides {Actual}";
    }

    /// <summary>The extension was built against a CS2DemoKit this app does not ship.</summary>
    public sealed record Cs2DemoKitMismatch(VersionRange Required, SemVersion Actual) : PackCompatibility
    {
        /// <inheritdoc />
        public override string Describe(ExtensionManifest? manifest, string fallbackName) =>
            $"{Subject(manifest, fallbackName)} needs CS2DemoKit {Required}; this app ships {Actual}";
    }

    /// <summary>The app release is below the extension's floor.</summary>
    public sealed record AppTooOld(SemVersion Required, SemVersion Actual) : PackCompatibility
    {
        /// <inheritdoc />
        public override string Describe(ExtensionManifest? manifest, string fallbackName) =>
            $"{Subject(manifest, fallbackName)} needs app {Required} or newer; this is {Actual}";
    }

    /// <summary>The manifest could not be read, or does not describe the pack that carries it.</summary>
    public sealed record ManifestInvalid(string Reason) : PackCompatibility
    {
        /// <inheritdoc />
        public override string Describe(ExtensionManifest? manifest, string fallbackName) =>
            $"{Subject(manifest, fallbackName)} has an invalid manifest: {Reason}";
    }
}
