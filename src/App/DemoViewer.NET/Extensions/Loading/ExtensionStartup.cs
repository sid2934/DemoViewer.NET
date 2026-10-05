#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>What a desktop launch loads.</summary>
/// <param name="Statuses">The shipped extensions, then the third-party ones that loaded.</param>
/// <param name="ExternalRejected">Third-party copies that did not load, with the reason.</param>
public sealed record ExtensionStartupResult(IReadOnlyList<PackStatus> Statuses, IReadOnlyList<LoadOutcome> ExternalRejected);

/// <summary>The desktop head's extension resolution, before Avalonia starts.</summary>
public static class ExtensionStartup
{
    /// <summary>Resolves every extension a launch loads. Safe mode loads none, the shipped ones included.</summary>
    /// <param name="configRoot">The config root; null loads only what ships.</param>
    /// <param name="shipped">The extensions this build ships.</param>
    /// <param name="host">What this app provides.</param>
    /// <param name="shippedTrust">The trust policy for staged updates of shipped extensions.</param>
    /// <param name="publisherKeys">The keys that verify a third-party copy.</param>
    /// <param name="allowUnverified">The user's "Allow unverified and potentially dangerous extensions" setting.</param>
    /// <param name="safeMode">True to load nothing.</param>
    public static ExtensionStartupResult Resolve(string? configRoot, IReadOnlyList<ShippedPack> shipped, ExtensionHostInfo host,
        ITrustPolicy shippedTrust, IReadOnlyList<string> publisherKeys, bool allowUnverified, bool safeMode)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shippedTrust);
        ArgumentNullException.ThrowIfNull(publisherKeys);
        if (safeMode)
        {
            return new ExtensionStartupResult([], []);
        }

        IReadOnlyList<PackStatus> fromShipped = ExtensionLoader.Resolve(configRoot, shipped, host, shippedTrust);
        ExternalResolution external = ExternalExtensions.Resolve(configRoot,
            [.. fromShipped.Where(s => s.IsCompatible).Select(s => s.Pack)], host,
            new ExternalTrust(publisherKeys, allowUnverified));
        return new ExtensionStartupResult([.. fromShipped, .. external.Loaded], external.Rejected);
    }

    /// <summary>
    ///     Reads <c>Extensions.AllowUnverified</c> from the settings file without the settings service, which
    ///     does not exist yet at this point of launch. Anything unreadable reads as off.
    /// </summary>
    public static bool ReadAllowUnverified(string? settingsFile)
    {
        try
        {
            if (settingsFile is null || !File.Exists(settingsFile))
            {
                return false;
            }

            JsonNode? root = JsonNode.Parse(File.ReadAllText(settingsFile));
            return Find(root, "Extensions") is JsonObject section
                   && Find(section, "AllowUnverified") is JsonValue value
                   && value.TryGetValue(out bool allowed)
                   && allowed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // The settings service binds case-insensitively, so this read does too.
    private static JsonNode? Find(JsonNode? node, string key) =>
        node is JsonObject obj
            ? obj.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value
            : null;
}
