#region

using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Manifests for the test fakes that implement <c>IExtension</c>: one that fits any host, and the
///     mismatching shapes <c>PackCompatibilityTests</c> and <c>FeaturePacksTests</c> need.
/// </summary>
internal static class FakeManifests
{
    /// <summary>A manifest for <paramref name="packId" /> that every host accepts.</summary>
    public static ExtensionManifest For(string packId, string name = "Fake", string version = "1.0.0") =>
        new(packId, name, SemVersion.Parse(version), "Fake.dll", "Fake.Pack", VersionRange.Any, VersionRange.Any);

    /// <summary>A manifest for <paramref name="packId" /> with explicit ranges.</summary>
    public static ExtensionManifest For(string packId, string name, string version, string requiresHost, string requiresCs2DemoKit,
        string? minAppVersion = null) =>
        new(packId, name, SemVersion.Parse(version), "Fake.dll", "Fake.Pack", VersionRange.Parse(requiresHost),
            VersionRange.Parse(requiresCs2DemoKit), minAppVersion is null ? null : SemVersion.Parse(minAppVersion));
}
