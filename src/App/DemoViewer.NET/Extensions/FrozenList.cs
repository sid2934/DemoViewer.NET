namespace DemoViewer.NET.Extensions;

/// <summary>
///     A list set once and frozen on first read. <see cref="Value" /> is empty until <see cref="Set" />;
///     a <see cref="Set" /> after another <see cref="Set" /> or after the first read throws, so a consumer
///     that cached the value can never disagree with a later one. <see cref="SetIfUnset" /> is the same
///     call for a host that cannot know whether the authoritative one already ran.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
internal sealed class FrozenList<T>
{
    private readonly Lock _gate = new();
    private IReadOnlyList<T> _value = [];
    private bool _set;
    private bool _read;

    /// <summary>The list; empty until set. Reading freezes it.</summary>
    public IReadOnlyList<T> Value
    {
        get
        {
            lock (_gate)
            {
                _read = true;
                return _value;
            }
        }
    }

    /// <summary>Whether <see cref="Set" /> or <see cref="SetIfUnset" /> has run.</summary>
    public bool IsSet
    {
        get
        {
            lock (_gate)
            {
                return _set;
            }
        }
    }

    /// <summary>Sets the list, once, before any read. An empty list is a valid value.</summary>
    /// <exception cref="InvalidOperationException">Already set, or <see cref="Value" /> was already read.</exception>
    public void Set(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (_set)
            {
                throw new InvalidOperationException("The list is already set.");
            }

            if (_read)
            {
                throw new InvalidOperationException("The list was read before it was set and is frozen.");
            }

            _value = [.. value];
            _set = true;
        }
    }

    /// <summary>
    ///     <see cref="Set" /> when nothing has set the list yet; a no-op after a <see cref="Set" />. Still
    ///     throws after a read of an unset list, which is the ordering mistake the freeze exists to catch.
    /// </summary>
    /// <returns>True when this call set the list.</returns>
    public bool SetIfUnset(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (_set)
            {
                return false;
            }

            Set(value);
            return true;
        }
    }
}
