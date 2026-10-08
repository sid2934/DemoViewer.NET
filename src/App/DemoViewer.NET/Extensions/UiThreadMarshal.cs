#region

using Avalonia.Threading;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     Runs a host handler for an event an extension raised on the UI thread: inline when the raise already
///     came from it, posted otherwise. The handlers it carries touch observable collections and bound
///     properties, which are only safe to change on the UI thread.
/// </summary>
internal static class UiThreadMarshal
{
    /// <summary>Runs <paramref name="action" /> on the UI thread.</summary>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
