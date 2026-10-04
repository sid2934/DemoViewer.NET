#region

using System.Reflection;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Services.Update;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     What this app offers an extension: the pack contract version, the app release and the CS2DemoKit it
///     was built against. An extension's manifest names the ranges it accepts for the first two and the
///     third; <see cref="PackCompatibility.Check" /> compares them.
///     <para>
///         <b>Contract version rule.</b> <see cref="ContractVersion" /> is bumped by hand with the change
///         that needs it. The contract is the surface a pack's own assembly references or implements, not
///         every type under <c>DemoViewer.NET.Extensions</c>: host-side types that no pack touches
///         (<c>Loading</c>, <c>CompatibilityReport</c>) change freely. Major: a breaking change to a type a
///         pack does reference or implement, including <c>IModuleContext</c>, <c>IHostTabViewModel</c> or
///         the <c>IPlaybackSurface</c> family (a removed or renamed member, a changed signature, a new
///         abstract member on an interface a pack implements). Minor: an additive change (a new
///         contribution kind, a new optional member with a default). Patch: never; a contract has no
///         behaviour of its own to fix.
///     </para>
/// </summary>
public static class ExtensionHost
{
    /// <summary>
    ///     The extension SDK version this app implements: the SDK assembly's release, without prerelease or
    ///     build metadata, so a manifest's <c>requiresHost</c> range compares against a plain version. Apps
    ///     from before the SDK reported 1.0.0, so the SDK line starts at 1.1.
    /// </summary>
    public static SemVersion ContractVersion { get; } = ReadContractVersion();

    /// <summary>The app release (<see cref="AppVersionInfo.CurrentReleaseVersion" />), or null when unstamped.</summary>
    public static SemVersion? AppVersion { get; } =
        SemVersion.TryParse(AppVersionInfo.CurrentReleaseVersion, out SemVersion? app) ? app : null;

    /// <summary>
    ///     The CS2DemoKit version this build references, read from <c>CS2DemoKit.Analysis</c>'s
    ///     informational version so a package bump moves it with no edit here; the assembly version
    ///     (major.minor.patch, no prerelease) is the fallback when that attribute is missing, so a build
    ///     stripped of the attribute reports <c>0.13.0</c> for a <c>0.13.0-beta0001</c> package and an
    ///     exact prerelease pin then fails to match. <c>ExtensionHostTests</c> pins the value to the
    ///     <c>Directory.Packages.props</c> pin.
    /// </summary>
    public static SemVersion Cs2DemoKitVersion { get; } = ReadCs2DemoKitVersion();

    /// <summary>The three as one value, for <see cref="PackCompatibility.Check" />.</summary>
    public static ExtensionHostInfo Current { get; } = new(ContractVersion, AppVersion, Cs2DemoKitVersion);

    /// <summary>
    ///     <see cref="Cs2DemoKitVersion" />'s rule over its two inputs: the informational version when it
    ///     parses, else <paramref name="assemblyVersion" />'s first three components, else 0.0.0.
    /// </summary>
    public static SemVersion ResolveVersion(string? informational, Version? assemblyVersion)
    {
        if (SemVersion.TryParseInformational(informational, out SemVersion? version))
        {
            return version;
        }

        Version v = assemblyVersion ?? new Version(0, 0, 0);
        return new SemVersion(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    }

    private static SemVersion ReadContractVersion()
    {
        Assembly sdk = typeof(IExtension).Assembly;
        SemVersion version = ResolveVersion(
            sdk.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            sdk.GetName().Version);
        return new SemVersion(version.Major, version.Minor, version.Patch);
    }

    private static SemVersion ReadCs2DemoKitVersion()
    {
        Assembly assembly = typeof(DiagnosticsLog).Assembly;
        return ResolveVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);
    }
}
