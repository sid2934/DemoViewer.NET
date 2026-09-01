namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     Captures an expected exception so it can be asserted on. TUnit's fluent builder has no
///     <c>Throws</c> for value-returning delegates, and the repo's existing suites hand-roll the
///     try/catch each time; this is that pattern, once.
/// </summary>
internal static class Throws
{
    /// <summary>Runs the action and returns the exception it threw, or null.</summary>
    /// <typeparam name="TException">The exception type expected.</typeparam>
    /// <param name="action">The action under test.</param>
    public static TException? Capture<TException>(Action action) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            action();
        }
        catch (TException e)
        {
            return e;
        }

        return null;
    }
}
