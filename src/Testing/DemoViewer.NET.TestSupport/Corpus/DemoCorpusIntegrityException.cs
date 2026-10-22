namespace DemoViewer.NET.TestSupport.Corpus;

/// <summary>
///     A fetched demo's bytes are not the manifest's. Deliberately not a
///     <c>SkipTestException</c>: an absent demo is a skip, but a demo that is present and wrong is
///     a failure, because every golden captured against the manifest's bytes is now being compared
///     against something else.
/// </summary>
public sealed class DemoCorpusIntegrityException : Exception
{
    internal DemoCorpusIntegrityException(string message) : base(message)
    {
    }
}
