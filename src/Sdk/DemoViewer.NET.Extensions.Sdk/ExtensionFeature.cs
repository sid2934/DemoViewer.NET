namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>What an <see cref="ExtensionFeature" /> gates.</summary>
public enum ExtensionFeatureKind
{
    /// <summary>The extension's master switch. Exactly one per extension, with no parent.</summary>
    Extension,

    /// <summary>A tab or a section. Its parent is the extension's master switch.</summary>
    Tab,

    /// <summary>Part of a tab: the extension's own tab, or a host tab such as <see cref="HostIds.Playback2DFeature" />.</summary>
    SubFeature
}

/// <summary>Whether a feature starts on for each kind of user. The user can change it in Settings either way.</summary>
/// <param name="Consumer">On by default for consumers.</param>
/// <param name="PowerUser">On by default for power users.</param>
/// <param name="Developer">On by default for developers.</param>
public sealed record AudienceDefaults(bool Consumer, bool PowerUser, bool Developer)
{
    /// <summary>On for every user.</summary>
    public static AudienceDefaults Everyone { get; } = new(true, true, true);

    /// <summary>Off for consumers, on for power users and developers.</summary>
    public static AudienceDefaults PowerUsers { get; } = new(false, true, true);

    /// <summary>On for developers only.</summary>
    public static AudienceDefaults Developers { get; } = new(false, false, true);
}

/// <summary>A switch the user sees in Settings. Its id is persisted with the user's choice: never rename it.</summary>
/// <param name="Id">Stable id. The master switch's must start with <c>pack.</c>.</param>
/// <param name="Kind">What it gates.</param>
/// <param name="Label">Settings label.</param>
/// <param name="Description">Settings help text.</param>
/// <param name="ParentId">The feature it turns off with: null for the master switch.</param>
/// <param name="Defaults">Its starting state per user category.</param>
public sealed record ExtensionFeature(
    string Id,
    ExtensionFeatureKind Kind,
    string Label,
    string Description,
    string? ParentId,
    AudienceDefaults Defaults);
