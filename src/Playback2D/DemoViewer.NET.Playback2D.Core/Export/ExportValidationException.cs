namespace DemoViewer.NET.Playback2D.Core.Export;

/// <summary>
///     A request the session refuses <b>before rendering anything</b>: odd dimensions on a
///     <c>yuv420p</c> format, a GIF over its frame cap, an fps the format cannot express exactly, an
///     empty range.
///     <para>
///         The message is user-facing copy: the dialog shows it verbatim and the CLI prints it. Refusing
///         up front is the whole point. A user must never render two thousand frames into a failure the
///         request already contained.
///     </para>
/// </summary>
public sealed class ExportValidationException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing explanation of what is wrong with the request.</param>
    public ExportValidationException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">User-facing explanation.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ExportValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Parameterless form required by the analyzer's exception-shape rule.</summary>
    public ExportValidationException() : base("The export request is not valid.")
    {
    }
}
