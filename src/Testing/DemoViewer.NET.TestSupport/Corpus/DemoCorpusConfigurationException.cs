namespace DemoViewer.NET.TestSupport.Corpus;

/// <summary>
///     A corpus environment variable is set to something unusable. Deliberately not a
///     <c>SkipTestException</c>: whoever set the variable meant to run the corpus-backed tests, and
///     a skip is still green.
/// </summary>
public sealed class DemoCorpusConfigurationException : Exception
{
    internal DemoCorpusConfigurationException(string message) : base(message)
    {
    }
}
