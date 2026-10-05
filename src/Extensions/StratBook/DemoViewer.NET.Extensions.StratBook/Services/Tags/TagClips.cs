#region

using System.Globalization;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Tags;

/// <summary>Tag instances as Review Queue clips. The queue is core and knows no tag type; this is the join.</summary>
public static class TagClips
{
    /// <summary>
    ///     A clip from a tag instance: the shape a Matrix cell and a <c>TagQuery.Find</c> result hand the
    ///     queue. The ref names the demo by hash, so the path comes from the cache index; a hash no
    ///     indexed demo carries has nothing to open and returns null (the caller says "demo not in
    ///     library" rather than queueing a dead link).
    /// </summary>
    /// <param name="instance">The located instance.</param>
    /// <param name="pathForSha256">Hash to path, the cache's <c>TryGetIndexBySha256</c>.</param>
    /// <param name="tickRate">The demo's tick rate when the caller knows it, else 0.</param>
    public static ReviewEntry? FromTag(TagInstanceRef instance, Func<string, string?> pathForSha256, int tickRate = 0)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(pathForSha256);
        if (pathForSha256(instance.Sha256) is not { Length: > 0 } path)
        {
            return null;
        }

        string note = instance.Round is int round
            ? string.Create(CultureInfo.InvariantCulture, $"{instance.Code} · round {round}")
            : instance.Code;
        return ReviewEntry.Clip(path, instance.FromTick, instance.ToTick, note, ReviewSources.Tag, tickRate,
            instance.Sha256);
    }
}
