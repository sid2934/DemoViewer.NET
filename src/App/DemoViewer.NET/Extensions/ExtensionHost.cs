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
///         that needs it. Major: a breaking change to any type under <c>DemoViewer.NET.Extensions</c>, or
///         to <c>IModuleContext</c>, <c>IHostTabViewModel</c> or the <c>IPlaybackSurface</c> family (a
///         removed or renamed member, a changed signature, a new abstract member on an interface a pack
///         implements). Minor: an additive change (a new contribution kind, a new optional member with a
///         default). Patch: never; a contract has no behaviour of its own to fix.
///     </para>
/// </summary>
public static class ExtensionHost
{
    /// <summary>The pack contract version this app implements.</summary>
    public static SemVersion ContractVersion { get; } = new(1, 0, 0);

    /// <summary>The app release (<see cref="AppVersionInfo.CurrentReleaseVersion" />), or null when unstamped.</summary>
    public static SemVersion? AppVersion { get; } =
        SemVersion.TryParse(AppVersionInfo.CurrentReleaseVersion, out SemVersion? app) ? app : null;

    /// <summary>
    ///     The CS2DemoKit version this build references, read from <c>CS2DemoKit.Analysis</c>'s
    ///     informational version so a package bump moves it with no edit here; the assembly version
    ///     (major.minor.patch, no prerelease) is the fallback when that attribute is missing.
    ///     <c>ExtensionHostTests</c> pins it to the <c>Directory.Packages.props</c> pin.
    /// </summary>
    public static SemVersion Cs2DemoKitVersion { get; } = ReadCs2DemoKitVersion();

    /// <summary>The three as one value, for <see cref="PackCompatibility.Check" />.</summary>
    public static ExtensionHostInfo Current { get; } = new(ContractVersion, AppVersion, Cs2DemoKitVersion);

    private static SemVersion ReadCs2DemoKitVersion()
    {
        Assembly assembly = typeof(DiagnosticsLog).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemVersion.TryParseInformational(informational, out SemVersion? version))
        {
            return version;
        }

        Version v = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new SemVersion(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    }
}
