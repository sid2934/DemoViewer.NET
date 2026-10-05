using System.Diagnostics.CodeAnalysis;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     The extension's own settings: a flat set of keys, each holding a JSON value, kept in one file that only
///     this extension reads and writes. Safe to call from any thread. A write lands on disk before
///     <see cref="Set{T}" /> returns; the browser build keeps the values for the session.
/// </summary>
public interface IExtensionSettings
{
    /// <summary>
    ///     The value stored under <paramref name="key" />, or <paramref name="fallback" /> when there is none or
    ///     the stored value does not read as <typeparamref name="T" /> (a hand-edited file, an enum name that no
    ///     longer exists). Enums are stored by name.
    /// </summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="fallback">What to answer when nothing usable is stored: the setting's default.</param>
    [SuppressMessage("Naming", "CA1716", Justification = "Get and Set read naturally in C#; a Visual Basic extension would implement them with escaped names.")]
    T Get<T>(string key, T fallback);

    /// <summary>
    ///     Stores <paramref name="value" /> under <paramref name="key" /> and raises <see cref="Changed" /> when
    ///     the stored value moved. Writing the value already stored does nothing.
    /// </summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="value">The new value; it must serialize to JSON.</param>
    [SuppressMessage("Naming", "CA1716", Justification = "As Get.")]
    void Set<T>(string key, T value);

    /// <summary>Forgets <paramref name="key" />, so <see cref="Get{T}" /> answers its fallback. False when nothing was stored.</summary>
    /// <param name="key">The setting's key.</param>
    bool Remove(string key);

    /// <summary>Raised on the UI thread with the key whose value moved, after the write.</summary>
    event Action<string>? Changed;
}
