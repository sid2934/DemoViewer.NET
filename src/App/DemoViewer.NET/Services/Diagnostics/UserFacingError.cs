namespace DemoViewer.NET.Services.Diagnostics;

/// <summary>
///     Turns a caught exception into a user-facing sentence (v0.6.0, backlog item 9). Before this,
///     ~a dozen surfaces interpolated raw <see cref="Exception.Message" /> into status lines — so a
///     corrupt demo read as "Error: Index was outside the bounds of the array." The contract now:
///     the UI shows THIS text, and the caller logs the full exception through
///     <c>AppLog.OperationFailed</c> so the detail lands in the Diagnostics tab + rolling file.
///     <para>
///         Returns a string rather than assigning anywhere, because the call sites write different
///         properties (<c>StatusText</c>, <c>EntityStatusText</c>, <c>EvalSummary</c>, …) and one
///         (the stats CSV export) returns its message.
///     </para>
/// </summary>
public static class UserFacingError
{
    /// <summary>
    ///     Builds "Couldn't &lt;operation&gt; — &lt;plain-language reason&gt;." for the exception
    ///     families users actually hit. <paramref name="operation" /> is an infinitive phrase
    ///     ("load the demo", "export the scoreboard").
    /// </summary>
    public static string Describe(string operation, Exception ex)
    {
        string reason = ex switch
        {
            OperationCanceledException => "it was cancelled",
            UnauthorizedAccessException => "access to a file or folder was denied",
            FileNotFoundException or DirectoryNotFoundException => "a file it needs is missing",
            PathTooLongException => "a file path is too long for this system",
            IOException => "a file could not be read or written",
            OutOfMemoryException => "the app ran out of memory",
            InvalidDataException or EndOfStreamException =>
                "the file's data is not in the expected format (it may be damaged or truncated)",
            NotSupportedException => "this file or format isn't supported",
            TimeoutException => "the operation timed out",
            _ => "an unexpected error occurred"
        };

        return $"Couldn't {operation} — {reason}. Details are in the Diagnostics tab.";
    }
}
