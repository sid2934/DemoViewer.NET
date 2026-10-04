namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     A button Match Overview offers for the open demo while <see cref="IsAvailable" /> answers true. Call
///     <see cref="NotifyChanged" /> when the answer may have changed.
/// </summary>
public sealed class DemoAction
{
    /// <summary>Creates the action.</summary>
    /// <param name="id">Unique across the app.</param>
    /// <param name="label">The button text.</param>
    /// <param name="tooltip">The button tooltip.</param>
    /// <param name="isAvailable">Whether the demo at the given path should offer it now. Called on the UI thread.</param>
    /// <param name="run">Runs it for the demo at the given path. Queue slow work.</param>
    /// <param name="featureId">Offered only while this feature is on; null for while the extension is on.</param>
    public DemoAction(string id, string label, string tooltip, Func<string, bool> isAvailable, Action<string> run,
        string? featureId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(isAvailable);
        ArgumentNullException.ThrowIfNull(run);
        Id = id;
        Label = label;
        Tooltip = tooltip ?? "";
        IsAvailable = isAvailable;
        Run = run;
        FeatureId = featureId;
    }

    /// <summary>Unique across the app.</summary>
    public string Id { get; }

    /// <summary>The button text.</summary>
    public string Label { get; }

    /// <summary>The button tooltip.</summary>
    public string Tooltip { get; }

    /// <summary>Whether the demo at the given path should offer it now.</summary>
    public Func<string, bool> IsAvailable { get; }

    /// <summary>Runs it for the demo at the given path.</summary>
    public Action<string> Run { get; }

    /// <summary>The feature it is gated on, if narrower than the extension.</summary>
    public string? FeatureId { get; }

    /// <summary>Raised by <see cref="NotifyChanged" />.</summary>
    public event Action? Changed;

    /// <summary>Asks the host to call <see cref="IsAvailable" /> again.</summary>
    public void NotifyChanged() => Changed?.Invoke();
}
