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

/// <summary>How the host renders one setting.</summary>
public enum SettingKind
{
    /// <summary>On or off, stored as a bool.</summary>
    Toggle,

    /// <summary>A number in a range, stored as a number.</summary>
    Number,

    /// <summary>One of a fixed set of values, stored as the chosen value's string.</summary>
    Choice,

    /// <summary>Free text, stored as a string.</summary>
    Text,

    /// <summary>A folder the user picks, stored as its path, or absent for none.</summary>
    Folder
}

/// <summary>One value a <see cref="SettingKind.Choice" /> setting offers.</summary>
/// <param name="Value">What is stored.</param>
/// <param name="Label">What the user sees.</param>
public sealed record SettingChoice(string Value, string Label);

/// <summary>
///     One setting the host renders on the extension's settings page and stores in <see cref="IExtensionSettings" />
///     under <see cref="Key" />. Build one with <see cref="Toggle" />, <see cref="Number" />, <see cref="Choice" />,
///     <see cref="Text" /> or <see cref="Folder" />.
/// </summary>
/// <param name="Key">The key in the extension's settings; read it back with <see cref="IExtensionSettings.Get{T}" />.</param>
/// <param name="Label">The row's title.</param>
/// <param name="Kind">How the row is rendered and what it stores.</param>
/// <param name="Default">What the row shows while nothing is stored: a bool, a double or a string, by kind; null for no folder.</param>
public sealed record SettingDescriptor(string Key, string Label, SettingKind Kind, object? Default)
{
    /// <summary>A line under the title saying what the setting does.</summary>
    public string? Help { get; init; }

    /// <summary>The smallest number a <see cref="SettingKind.Number" /> row accepts.</summary>
    public double? Minimum { get; init; }

    /// <summary>The largest number a <see cref="SettingKind.Number" /> row accepts.</summary>
    public double? Maximum { get; init; }

    /// <summary>How far one step of a <see cref="SettingKind.Number" /> row moves; a whole step stores whole numbers.</summary>
    public double Step { get; init; } = 1;

    /// <summary>What a <see cref="SettingKind.Choice" /> row offers.</summary>
    public IReadOnlyList<SettingChoice> Choices { get; init; } = Array.Empty<SettingChoice>();

    /// <summary>An on-off setting.</summary>
    /// <param name="key">The settings key.</param>
    /// <param name="label">The row's title.</param>
    /// <param name="defaultValue">The value while nothing is stored.</param>
    /// <param name="help">A line saying what it does.</param>
    public static SettingDescriptor Toggle(string key, string label, bool defaultValue, string? help = null) =>
        new(key, label, SettingKind.Toggle, defaultValue) { Help = help };

    /// <summary>A number between <paramref name="minimum" /> and <paramref name="maximum" />.</summary>
    /// <param name="key">The settings key.</param>
    /// <param name="label">The row's title.</param>
    /// <param name="defaultValue">The value while nothing is stored.</param>
    /// <param name="minimum">The smallest value accepted.</param>
    /// <param name="maximum">The largest value accepted.</param>
    /// <param name="step">How far one step moves; a whole step stores whole numbers.</param>
    /// <param name="help">A line saying what it does.</param>
    public static SettingDescriptor Number(string key, string label, double defaultValue, double minimum, double maximum,
        double step = 1, string? help = null) =>
        new(key, label, SettingKind.Number, defaultValue) { Minimum = minimum, Maximum = maximum, Step = step, Help = help };

    /// <summary>One of <paramref name="choices" />.</summary>
    /// <param name="key">The settings key.</param>
    /// <param name="label">The row's title.</param>
    /// <param name="defaultValue">The value while nothing is stored; one of the choices' values.</param>
    /// <param name="choices">What the row offers.</param>
    /// <param name="help">A line saying what it does.</param>
    public static SettingDescriptor Choice(string key, string label, string defaultValue, IReadOnlyList<SettingChoice> choices,
        string? help = null) =>
        new(key, label, SettingKind.Choice, defaultValue) { Choices = choices, Help = help };

    /// <summary>Free text.</summary>
    /// <param name="key">The settings key.</param>
    /// <param name="label">The row's title.</param>
    /// <param name="defaultValue">The value while nothing is stored.</param>
    /// <param name="help">A line saying what it does.</param>
    public static SettingDescriptor Text(string key, string label, string defaultValue = "", string? help = null) =>
        new(key, label, SettingKind.Text, defaultValue) { Help = help };

    /// <summary>A folder the user picks.</summary>
    /// <param name="key">The settings key.</param>
    /// <param name="label">The row's title.</param>
    /// <param name="help">A line saying what it does.</param>
    public static SettingDescriptor Folder(string key, string label, string? help = null) =>
        new(key, label, SettingKind.Folder, null) { Help = help };
}

/// <summary>
///     A settings page the host renders from <see cref="Settings" />: one row per setting, each reading and
///     writing the extension's <see cref="IExtensionSettings" />. Use it instead of a
///     <see cref="SettingsPageContribution" /> unless the page needs controls of its own.
/// </summary>
/// <param name="Id">Unique across the app.</param>
/// <param name="Header">The page's heading under Settings, Extensions.</param>
/// <param name="Settings">The rows, in order.</param>
public sealed record SettingsSchema(string Id, string Header, IReadOnlyList<SettingDescriptor> Settings)
{
    /// <summary>Position among pages; the host's pages use 0 to 99.</summary>
    public int Order { get; init; }

    /// <summary>Extra words the Settings search matches, beside the header and the row labels.</summary>
    public string Keywords { get; init; } = "";

    /// <summary>Shown only while this feature is on; null for while the extension is on.</summary>
    public string? FeatureId { get; init; }
}
