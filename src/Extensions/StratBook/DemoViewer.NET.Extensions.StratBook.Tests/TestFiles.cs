#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>The stores' files under a test folder, laid out as plain paths.</summary>
internal static class TestFiles
{
    /// <summary><c>teams.json</c> in <paramref name="root" /> and the index under its <c>cache</c> folder; null for none.</summary>
    public static TeamIdentityFiles? Teams(string? root) => root is null ? null :
        new(Path.Combine(root, TeamIdentityService.TeamsFileName),
            StoredFile.At(Path.Combine(root, "cache", TeamIdentityService.IndexFileName)));

    /// <summary>The detections and signatures under <paramref name="cacheRoot" />, the user's choices under <paramref name="configRoot" />.</summary>
    public static StratMiningFiles Mining(string? cacheRoot, string? configRoot) =>
        new(cacheRoot is null ? null : StoredFile.At(Path.Combine(cacheRoot, StratMiningFiles.DetectedPath)),
            cacheRoot is null ? null : StoredFile.At(Path.Combine(cacheRoot, StratMiningFiles.SignaturesPath)),
            configRoot is null ? null : StoredFile.At(Path.Combine(configRoot, StratMiningFiles.StatePath)));
}
