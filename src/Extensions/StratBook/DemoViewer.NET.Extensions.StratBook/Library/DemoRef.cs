#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     How the pack names one demo across its stores. Derived stores key by <see cref="StableKey" />, which
///     follows the path; user-truth stores key by <see cref="Sha256" />, which follows the content, so a moved
///     file keeps its tags. A consumer crossing that line needs both in hand.
/// </summary>
/// <param name="Path">The demo's path as the library knows it.</param>
/// <param name="StableKey"><see cref="DemoKeys.StableKey" /> of <paramref name="Path" />.</param>
/// <param name="Sha256">Lowercase hex content hash, or null when the library has not hashed the demo.</param>
public sealed record DemoRef(string Path, string StableKey, string? Sha256)
{
    /// <summary>The reference for a library row.</summary>
    /// <param name="demo">The row.</param>
    public static DemoRef From(LibraryDemo demo)
    {
        ArgumentNullException.ThrowIfNull(demo);
        return new DemoRef(demo.FilePath, DemoKeys.StableKey(demo.FilePath), demo.Sha256);
    }
}

/// <summary>The pack's keys for a demo.</summary>
public static class DemoKeys
{
    /// <summary>
    ///     A filesystem-safe key for a demo path: SHA-256 of the lowercased path's UTF-8, first 12 bytes,
    ///     lowercase hex. Persisted in <c>teams.json</c>, the team index, the grenade headers and the
    ///     suggestions, so it must never change: a different value orphans every override keyed on it.
    /// </summary>
    /// <param name="demoPath">The demo's path.</param>
    public static string StableKey(string demoPath)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(demoPath.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <summary>A SteamID64 as the pack's stores write it: decimal, or empty for 0.</summary>
    /// <param name="steamId64">The id.</param>
    public static string SteamIdText(ulong steamId64) =>
        steamId64 == 0 ? "" : steamId64.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Reads of the library the pack's synchronous services make from queue threads.</summary>
public static class LibraryReads
{
    /// <summary>
    ///     The demo's detail, read before returning. Call it from a pass, a job or another queue thread, where
    ///     <see cref="IExtensionLibrary.GetDetailAsync" /> completes before it returns.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="path">The demo's path.</param>
    public static LibraryDemoDetail? Detail(this IExtensionLibrary library, string path)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.GetDetailAsync(path).GetAwaiter().GetResult();
    }
}
