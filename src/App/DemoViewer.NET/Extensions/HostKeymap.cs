#region

using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The keymap as extensions read it: <see cref="CommandRegistry.Default" />'s composed table and the
///     reserved gesture lists of <see cref="Playback2DKeymap" /> as shipped, and the user's keymap, their
///     override rows composed over that table, for resolving keys and naming gestures.
/// </summary>
internal sealed class HostKeymap : IExtensionKeymap
{
    private readonly Lazy<KeymapBinding[]> _bindings;
    private readonly Func<IReadOnlyList<string>> _overrides;
    private string[] _applied = [];
    private Playback2DKeymapProfile? _profile;

    /// <summary>Reads <paramref name="bindings" /> on first use and composes <paramref name="overrides" /> on first resolve.</summary>
    /// <param name="bindings">The composed table, core rows first.</param>
    /// <param name="overrides">The user's override rows; none when omitted.</param>
    public HostKeymap(Func<IReadOnlyList<Playback2DBinding>> bindings, Func<IReadOnlyList<string>>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _overrides = overrides ?? (static () => []);
        _bindings = new Lazy<KeymapBinding[]>(() =>
        [
            .. bindings().Select(b => new KeymapBinding(b.ActionId, b.Description, b.Scope.Name,
                new KeyGesture(b.Key, b.Modifiers)))
        ]);
    }

    /// <summary>Over the registry this launch composed, with no override rows. Lazy, since the registry reads the composed packs.</summary>
    public static HostKeymap Instance { get; } = new(() => CommandRegistry.Default.EffectiveBindings);

    /// <inheritdoc />
    public IReadOnlyList<KeymapBinding> Bindings => _bindings.Value;

    /// <inheritdoc />
    public IReadOnlyList<KeyGesture> ShellReserved { get; } =
        [.. Playback2DKeymap.ShellReservedGestures.Select(g => new KeyGesture(g.Key, g.Modifiers))];

    /// <inheritdoc />
    public IReadOnlyList<KeyGesture> BrowserReserved { get; } =
        [.. Playback2DKeymap.BrowserReservedGestures.Select(g => new KeyGesture(g.Key, g.Modifiers))];

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public string? ActionFor(string scope, Key key, KeyModifiers modifiers) =>
        !string.IsNullOrEmpty(scope)
        && Profile.TryResolveInScope(new Playback2DBindingScope(scope), key, modifiers, out string? actionId)
            ? actionId
            : null;

    /// <inheritdoc />
    public string GestureText(string actionId) => actionId is null ? "" : Profile.GestureText(actionId);

    /// <summary>
    ///     Re-reads the override rows. Raises <see cref="Changed" /> only when they differ from the rows last
    ///     composed, so a settings write that touched something else is silent. Call on the UI thread.
    /// </summary>
    public void Refresh()
    {
        if (_profile is null)
        {
            return;
        }

        string[] current = [.. _overrides()];
        if (current.AsSpan().SequenceEqual(_applied))
        {
            return;
        }

        _profile = null;
        Changed?.Invoke();
    }

    private Playback2DKeymapProfile Profile
    {
        get
        {
            if (_profile is null)
            {
                _applied = [.. _overrides()];
                _profile = Playback2DKeymapProfile.FromOverrides(_applied, out _);
            }

            return _profile;
        }
    }
}
