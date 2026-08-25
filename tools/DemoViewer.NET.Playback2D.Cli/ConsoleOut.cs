#region

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     The tool's output discipline (C1 decision 8). With <c>--json</c>, <b>stdout carries exactly one
///     JSON object</b> and every human line moves to stderr, so <c>dv2d … --json | jq</c> works without
///     a filter and a CI log still shows the prose. Without it, humans get stdout as usual.
/// </summary>
internal static class ConsoleOut
{
    // Relaxed escaping: this output goes to a terminal and a CI log, never into HTML, and a reason
    // string full of ' is a reason string nobody reads.
    private static readonly JsonSerializerOptions _pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions _compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The indented, relaxed-escaping options every JSON artifact this tool writes uses.</summary>
    public static JsonSerializerOptions Pretty => _pretty;

    /// <summary>Whether <c>--json</c> was given; set once by <see cref="Program" /> before dispatch.</summary>
    public static bool IsJson { get; set; }

    /// <summary>Whether <c>--quiet</c> was given: suppresses <see cref="Info" /> only.</summary>
    public static bool IsQuiet { get; set; }

    /// <summary>Whether a single stdout JSON object has already been emitted.</summary>
    public static bool JsonEmitted { get; private set; }

    /// <summary>Resets the sticky state. Only the in-process test host needs this.</summary>
    public static void Reset()
    {
        IsJson = false;
        IsQuiet = false;
        JsonEmitted = false;
    }

    /// <summary>A normal human line. Suppressed by <c>--quiet</c>.</summary>
    /// <param name="message">The line.</param>
    public static void Info(string message)
    {
        if (!IsQuiet)
        {
            Human().WriteLine(message);
        }
    }

    /// <summary>A warning. Survives <c>--quiet</c> — a warning nobody sees is not a warning.</summary>
    /// <param name="message">The line, printed after <c>warning: </c>.</param>
    public static void Warn(string message) => Human().WriteLine("warning: " + message);

    /// <summary>An error. Always stderr, in both modes.</summary>
    /// <param name="message">The line, printed after <c>error: </c>.</param>
    public static void Error(string message) => Console.Error.WriteLine("error: " + message);

    /// <summary>Writes the command's single stdout JSON object. Called at most once per process.</summary>
    /// <param name="payload">The object to write.</param>
    public static void Json(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        JsonEmitted = true;
        Console.Out.WriteLine(payload.ToJsonString(_pretty));
    }

    /// <summary>Writes one newline-delimited JSON event to stderr (progress, diagnostics).</summary>
    /// <param name="payload">The event object.</param>
    public static void JsonEvent(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Console.Error.WriteLine(payload.ToJsonString(_compact));
    }

    private static TextWriter Human() => IsJson ? Console.Error : Console.Out;
}
