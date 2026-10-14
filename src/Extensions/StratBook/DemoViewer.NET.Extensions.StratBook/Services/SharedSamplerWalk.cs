#region

using System.Runtime.CompilerServices;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services;

/// <summary>
///     One projectile decode per visit for Suggested Tags' thrower fix-up and the grenade walk. Whichever
///     runs first decodes at stride 1 when the other will run on the same parse, and the second takes what
///     it needs from the first. Each consumer's share is dropped as it is taken, so nothing stays on a parse
///     the shell keeps open.
///     <para>
///         The thrower fix-up reads only <c>Removed</c> samples, which the sampler yields on their own frame
///         at any stride, so the stride-1 stream filtered to <c>Removed</c> is the stride-8 one.
///     </para>
/// </summary>
internal sealed class SharedSamplerWalk
{
    /// <summary>The stride of the thrower fix-up's own walk when the grenade walk is not coming.</summary>
    internal const int RemovedStride = 8;

    private readonly ConditionalWeakTable<ParsedDemo, Entry> _entries = new();
    private readonly object _gate = new();
    private readonly Func<string, ParsedDemo, bool> _grenadesWill;
    private readonly Func<string, bool> _suggestedTagsWill;
    private readonly Func<ParsedDemo, int, int, IEnumerable<ProjectileSample>> _walk;

    /// <param name="grenadesWill">True when the grenade walk will still run on this parse.</param>
    /// <param name="suggestedTagsWill">True when Suggested Tags will still run on this demo.</param>
    /// <param name="walk">The projectile walk (demo, stride, max frames); null is the engine's sampler.</param>
    internal SharedSamplerWalk(
        Func<string, ParsedDemo, bool> grenadesWill,
        Func<string, bool> suggestedTagsWill,
        Func<ParsedDemo, int, int, IEnumerable<ProjectileSample>>? walk = null)
    {
        ArgumentNullException.ThrowIfNull(grenadesWill);
        ArgumentNullException.ThrowIfNull(suggestedTagsWill);
        _grenadesWill = grenadesWill;
        _suggestedTagsWill = suggestedTagsWill;
        _walk = walk ?? ((demo, stride, maxFrames) => ProjectileSampler.Walk(demo, stride, maxFrames));
    }

    /// <summary>Parses that still hold a share for a consumer that has not taken it. For tests.</summary>
    internal int Held
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count();
            }
        }
    }

    /// <summary>The thrower fix-up's samples: every <c>Removed</c> projectile sample of the demo, in walk order.</summary>
    /// <param name="path">The demo's path, for the grenade walk's interest.</param>
    /// <param name="demo">The held parse.</param>
    internal List<ProjectileSample> Removed(string path, ParsedDemo demo)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(demo);
        lock (_gate)
        {
            if (_entries.TryGetValue(demo, out Entry? entry))
            {
                if (entry.Removed is { } removed)
                {
                    entry.Removed = null;
                    Drop(demo, entry);
                    return removed;
                }

                if (entry.All is { } all)
                {
                    return [.. all.Where(s => s.Removed)];
                }
            }
        }

        if (!_grenadesWill(path, demo))
        {
            DecodeSites.Enter(4);
            return [.. _walk(demo, RemovedStride, int.MaxValue).Where(s => s.Removed)];
        }

        DecodeSites.Enter(6);
        List<ProjectileSample> decoded = [.. _walk(demo, 1, int.MaxValue)];
        lock (_gate)
        {
            _entries.AddOrUpdate(demo, new Entry { All = decoded });
        }

        return [.. decoded.Where(s => s.Removed)];
    }

    /// <summary>The grenade walk's samples: every projectile sample at stride 1, up to <paramref name="maxFrames" />.</summary>
    /// <param name="path">The demo's path, for Suggested Tags' interest.</param>
    /// <param name="demo">The held parse.</param>
    /// <param name="maxFrames">The walk's frame bound.</param>
    internal IEnumerable<ProjectileSample> All(string path, ParsedDemo demo, int maxFrames = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(demo);
        lock (_gate)
        {
            if (_entries.TryGetValue(demo, out Entry? entry) && entry.All is { } all)
            {
                entry.All = null;
                Drop(demo, entry);
                // A bounded stride-1 walk is the full walk cut at the bound, Removed samples included.
                return maxFrames == int.MaxValue ? all : all.Where(s => s.FrameIndex < maxFrames);
            }
        }

        DecodeSites.Enter(6);
        IEnumerable<ProjectileSample> walk = _walk(demo, 1, maxFrames);
        // A bounded walk loses the Removed samples past the bound, so it is no stand-in for the fix-up's walk.
        return maxFrames == int.MaxValue && _suggestedTagsWill(path) ? Tee(demo, walk) : walk;
    }

    // Keeps the Removed subset for Suggested Tags while the grenade walk streams. Published only when the
    // enumeration completes, so an abandoned walk leaves nothing behind.
    private IEnumerable<ProjectileSample> Tee(ParsedDemo demo, IEnumerable<ProjectileSample> walk)
    {
        List<ProjectileSample> removed = [];
        foreach (ProjectileSample sample in walk)
        {
            if (sample.Removed)
            {
                removed.Add(sample);
            }

            yield return sample;
        }

        lock (_gate)
        {
            _entries.AddOrUpdate(demo, new Entry { Removed = removed });
        }
    }

    private void Drop(ParsedDemo demo, Entry entry)
    {
        if (entry is { All: null, Removed: null })
        {
            _entries.Remove(demo);
        }
    }

    private sealed class Entry
    {
        public List<ProjectileSample>? All { get; set; }
        public List<ProjectileSample>? Removed { get; set; }
    }
}
