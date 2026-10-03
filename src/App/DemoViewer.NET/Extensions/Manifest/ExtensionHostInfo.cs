namespace DemoViewer.NET.Extensions.Manifest;

/// <summary>
///     The three versions an extension is judged against. <see cref="ExtensionHost.Current" /> is the
///     running app's; a test builds its own.
/// </summary>
/// <param name="ContractVersion">The pack contract version this app implements (<see cref="ExtensionHost.ContractVersion" />).</param>
/// <param name="AppVersion">The app release, or null on a build with no version stamped.</param>
/// <param name="Cs2DemoKitVersion">The CS2DemoKit version the app was built against.</param>
public sealed record ExtensionHostInfo(SemVersion ContractVersion, SemVersion? AppVersion, SemVersion Cs2DemoKitVersion);
