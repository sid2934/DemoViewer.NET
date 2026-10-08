namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>How much a notification matters. The host maps each value to a theme colour and an icon.</summary>
public enum NotificationSeverity
{
    /// <summary>Something happened that the user may want to know.</summary>
    Info,

    /// <summary>Work the user is waiting on finished well.</summary>
    Success,

    /// <summary>Something needs the user's attention, but nothing is lost.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error
}

/// <summary>
///     A short message the host shows in the notification stack above the status strip. The host draws it and
///     keeps it for the session only.
/// </summary>
/// <param name="Id">
///     The extension's own name for the notification. Posting again under the same id replaces the shown one
///     in place, so a repeated "N new items" message never stacks.
/// </param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Title">One line. A notification without a title is dropped.</param>
/// <param name="Body">More detail under the title, or null.</param>
public sealed record Notification(string Id, NotificationSeverity Severity, string Title, string? Body = null)
{
    /// <summary>A button on the notification, or null for none. The notification closes after it runs.</summary>
    public NotificationAction? Action { get; init; }

    /// <summary>
    ///     How long the notification stays before the host closes it, or null to keep it until the user closes
    ///     it or the session ends.
    /// </summary>
    public TimeSpan? TimeToLive { get; init; }
}

/// <summary>A button on a notification.</summary>
/// <param name="Label">The button text.</param>
/// <param name="Run">What a click runs, on the UI thread. A throw counts as the extension's fault.</param>
public sealed record NotificationAction(string Label, Action Run);

/// <summary>
///     The extension's way to tell the user something happened. Safe from any thread: a post never blocks
///     and never throws. The host keeps a few notifications per extension and drops the oldest past that, so
///     a flood from one extension never pushes out another's. Notifications show only while the extension is
///     on, and closing the extension closes them.
/// </summary>
public interface IExtensionNotifications
{
    /// <summary>
    ///     Shows <paramref name="notification" />, or replaces the one shown under its id. A null notification,
    ///     or one without an id or a title, is logged against the extension and dropped.
    /// </summary>
    void Post(Notification notification);

    /// <summary>Closes the extension's notification shown under <paramref name="id" />, if any.</summary>
    void Dismiss(string id);
}
