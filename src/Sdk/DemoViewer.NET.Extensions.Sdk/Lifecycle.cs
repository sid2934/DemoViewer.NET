namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>Why <see cref="IExtensionLifecycle.OnEnabledAsync" /> ran.</summary>
public enum ExtensionStartReason
{
    /// <summary>The app started with the extension on.</summary>
    Startup,

    /// <summary>The user turned the extension on while the app was running.</summary>
    EnabledInSession
}

/// <summary>
///     Optional. Register it as a keyed singleton under the extension's <see cref="IExtension.Id" />
///     (<c>services.AddKeyedSingleton&lt;IExtensionLifecycle, MyLifecycle&gt;(Id)</c>) and the host calls it as the
///     master switch moves.
/// </summary>
public interface IExtensionLifecycle
{
    /// <summary>Start loads and subscriptions. Queue slow work through <see cref="IExtensionJobs" />.</summary>
    Task OnEnabledAsync(ExtensionStartReason reason, CancellationToken cancellationToken);

    /// <summary>
    ///     Stop and release memory. The host has already cancelled the extension's queued work; the returned
    ///     task completes once the extension holds nothing.
    /// </summary>
    Task OnDisabledAsync();

    /// <summary>Flush what must survive the exit, within <paramref name="budget" />.</summary>
    void OnShutdown(TimeSpan budget);
}

/// <summary>State an extension can drop when it is switched off and rebuild when it is switched back on.</summary>
public interface IExtensionResident
{
    /// <summary>Subscribe to what keeps it current. Loads nothing.</summary>
    void Attach();

    /// <summary>Unsubscribe, write what is pending, drop the state.</summary>
    void Release();
}
