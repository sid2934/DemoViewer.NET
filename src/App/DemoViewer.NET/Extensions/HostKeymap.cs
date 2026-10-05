#region

using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The shipped keymap as extensions read it: <see cref="CommandRegistry.Default" />'s composed table and the
///     reserved gesture lists of <see cref="Playback2DKeymap" />.
/// </summary>
internal sealed class HostKeymap : IExtensionKeymap
{
    private readonly Lazy<KeymapBinding[]> _bindings;

    /// <summary>Reads <paramref name="bindings" /> on first use.</summary>
    /// <param name="bindings">The composed table, core rows first.</param>
    public HostKeymap(Func<IReadOnlyList<Playback2DBinding>> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = new Lazy<KeymapBinding[]>(() =>
        [
            .. bindings().Select(b => new KeymapBinding(b.ActionId, b.Description, b.Scope.Name,
                new KeyGesture(b.Key, b.Modifiers)))
        ]);
    }

    /// <summary>Over the registry this launch composed. Lazy, since the registry reads the composed packs.</summary>
    public static HostKeymap Instance { get; } = new(() => CommandRegistry.Default.EffectiveBindings);

    /// <inheritdoc />
    public IReadOnlyList<KeymapBinding> Bindings => _bindings.Value;

    /// <inheritdoc />
    public IReadOnlyList<KeyGesture> ShellReserved { get; } =
        [.. Playback2DKeymap.ShellReservedGestures.Select(g => new KeyGesture(g.Key, g.Modifiers))];

    /// <inheritdoc />
    public IReadOnlyList<KeyGesture> BrowserReserved { get; } =
        [.. Playback2DKeymap.BrowserReservedGestures.Select(g => new KeyGesture(g.Key, g.Modifiers))];
}
