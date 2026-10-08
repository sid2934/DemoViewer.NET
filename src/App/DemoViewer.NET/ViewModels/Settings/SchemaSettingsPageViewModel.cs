#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     The page the host renders for an extension's <see cref="SettingsSchema" />: one row per setting, each
///     writing the extension's settings as it changes and following a change made anywhere else.
/// </summary>
public sealed class SchemaSettingsPageViewModel : IDisposable
{
    private readonly IExtensionSettings _settings;

    /// <param name="schema">What the page shows.</param>
    /// <param name="settings">Where the values live.</param>
    public SchemaSettingsPageViewModel(SettingsSchema schema, IExtensionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        Rows = new ObservableCollection<SchemaSettingRow>(schema.Settings.Select(d => SchemaSettingRow.For(d, settings)));
        _settings.Changed += OnChanged;
    }

    /// <summary>The rows, in the schema's order.</summary>
    public ObservableCollection<SchemaSettingRow> Rows { get; }

    /// <inheritdoc />
    public void Dispose() => _settings.Changed -= OnChanged;

    private void OnChanged(string key)
    {
        foreach (SchemaSettingRow row in Rows)
        {
            if (string.Equals(row.Key, key, StringComparison.Ordinal))
            {
                row.Reflect();
            }
        }
    }
}

/// <summary>One rendered setting.</summary>
public abstract partial class SchemaSettingRow : ObservableObject
{
    private protected SchemaSettingRow(SettingDescriptor descriptor, IExtensionSettings settings)
    {
        Descriptor = descriptor;
        Settings = settings;
    }

    private protected SettingDescriptor Descriptor { get; }

    private protected IExtensionSettings Settings { get; }

    /// <summary>The settings key.</summary>
    public string Key => Descriptor.Key;

    /// <summary>The row's title.</summary>
    public string Label => Descriptor.Label;

    /// <summary>The line under the title, or null.</summary>
    public string? Help => Descriptor.Help;

    /// <summary>True when there is a help line.</summary>
    public bool HasHelp => !string.IsNullOrWhiteSpace(Descriptor.Help);

    /// <summary>True while <see cref="Reflect" /> sets the row from the store, so the change is not written back.</summary>
    private protected bool Reflecting { get; private set; }

    /// <summary>Reads the stored value again.</summary>
    public void Reflect()
    {
        Reflecting = true;
        try
        {
            Read();
        }
        finally
        {
            Reflecting = false;
        }
    }

    /// <summary>Sets the row's value from the store.</summary>
    private protected abstract void Read();

    internal static SchemaSettingRow For(SettingDescriptor descriptor, IExtensionSettings settings) => descriptor.Kind switch
    {
        SettingKind.Toggle => new ToggleSettingRow(descriptor, settings),
        SettingKind.Number => new NumberSettingRow(descriptor, settings),
        SettingKind.Choice => new ChoiceSettingRow(descriptor, settings),
        SettingKind.Folder => new FolderSettingRow(descriptor, settings),
        _ => new TextSettingRow(descriptor, settings)
    };
}

/// <summary>An on-off row.</summary>
public sealed partial class ToggleSettingRow : SchemaSettingRow
{
    [ObservableProperty]
    private bool _value;

    internal ToggleSettingRow(SettingDescriptor descriptor, IExtensionSettings settings) : base(descriptor, settings) => Reflect();

    private protected override void Read() => Value = Settings.Get(Key, Descriptor.Default is true);

    partial void OnValueChanged(bool value)
    {
        if (!Reflecting)
        {
            Settings.Set(Key, value);
        }
    }
}

/// <summary>A number row, clamped to its range.</summary>
public sealed partial class NumberSettingRow : SchemaSettingRow
{
    [ObservableProperty]
    private decimal? _value;

    internal NumberSettingRow(SettingDescriptor descriptor, IExtensionSettings settings) : base(descriptor, settings) => Reflect();

    /// <summary>The smallest value.</summary>
    public decimal Minimum => Descriptor.Minimum is { } minimum ? (decimal)minimum : decimal.MinValue;

    /// <summary>The largest value.</summary>
    public decimal Maximum => Descriptor.Maximum is { } maximum ? (decimal)maximum : decimal.MaxValue;

    /// <summary>One step.</summary>
    public decimal Increment => (decimal)Descriptor.Step;

    /// <summary>The display format: no decimals for a whole step.</summary>
    public string FormatString => IsWhole ? "0" : "0.###";

    private bool IsWhole => Descriptor.Step == Math.Floor(Descriptor.Step);

    private protected override void Read()
    {
        double fallback = Descriptor.Default is IConvertible c ? c.ToDouble(System.Globalization.CultureInfo.InvariantCulture) : 0;
        Value = (decimal)Settings.Get(Key, fallback);
    }

    partial void OnValueChanged(decimal? value)
    {
        if (Reflecting || value is not { } number)
        {
            return;
        }

        double clamped = Math.Clamp((double)number, (double)Minimum, (double)Maximum);
        if (IsWhole)
        {
            Settings.Set(Key, (long)Math.Round(clamped));
        }
        else
        {
            Settings.Set(Key, clamped);
        }
    }
}

/// <summary>A row offering a fixed set of values.</summary>
public sealed partial class ChoiceSettingRow : SchemaSettingRow
{
    [ObservableProperty]
    private SettingChoice? _selected;

    internal ChoiceSettingRow(SettingDescriptor descriptor, IExtensionSettings settings) : base(descriptor, settings) => Reflect();

    /// <summary>What the row offers.</summary>
    public IReadOnlyList<SettingChoice> Choices => Descriptor.Choices;

    private protected override void Read()
    {
        string stored = Settings.Get(Key, Descriptor.Default as string ?? "");
        Selected = Choices.FirstOrDefault(c => string.Equals(c.Value, stored, StringComparison.Ordinal))
                   ?? Choices.FirstOrDefault(c => string.Equals(c.Value, Descriptor.Default as string, StringComparison.Ordinal));
    }

    partial void OnSelectedChanged(SettingChoice? value)
    {
        if (!Reflecting && value is not null)
        {
            Settings.Set(Key, value.Value);
        }
    }
}

/// <summary>A free-text row.</summary>
public sealed partial class TextSettingRow : SchemaSettingRow
{
    [ObservableProperty]
    private string _value = "";

    internal TextSettingRow(SettingDescriptor descriptor, IExtensionSettings settings) : base(descriptor, settings) => Reflect();

    private protected override void Read() => Value = Settings.Get(Key, Descriptor.Default as string ?? "");

    partial void OnValueChanged(string value)
    {
        if (!Reflecting)
        {
            Settings.Set(Key, value);
        }
    }
}

/// <summary>A folder row: the picked path, or none.</summary>
public sealed partial class FolderSettingRow : SchemaSettingRow
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPath))]
    [NotifyPropertyChangedFor(nameof(Shown))]
    private string? _path;

    internal FolderSettingRow(SettingDescriptor descriptor, IExtensionSettings settings) : base(descriptor, settings) => Reflect();

    /// <summary>True when a folder is picked.</summary>
    public bool HasPath => !string.IsNullOrEmpty(Path);

    /// <summary>The path, or "Not set".</summary>
    public string Shown => Path ?? "Not set";

    private protected override void Read()
    {
        string? stored = Settings.Get<string?>(Key, Descriptor.Default as string);
        Path = string.IsNullOrEmpty(stored) ? null : stored;
    }

    /// <summary>Forgets the picked folder.</summary>
    [RelayCommand]
    private void Clear() => Path = null;

    partial void OnPathChanged(string? value)
    {
        if (Reflecting)
        {
            return;
        }

        if (string.IsNullOrEmpty(value))
        {
            Settings.Remove(Key);
        }
        else
        {
            Settings.Set(Key, value);
        }
    }
}
