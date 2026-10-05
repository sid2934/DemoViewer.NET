using CS2DemoKit.Parser;

namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     Reads every demo the library indexes, on the queue's shared parse: one parse per demo serves the
///     library and every evaluator. Contribute it with <see cref="IExtensionContributions.Evaluator" />.
/// </summary>
public interface IExtensionEvaluator
{
    /// <summary>Unique across the app; prefix it with your extension id. Other evaluators order after it by this id.</summary>
    string Id { get; }

    /// <summary>
    ///     Whether <paramref name="path" /> still needs this evaluator. Asked when the library adds or changes
    ///     the demo, when the host re-checks the library, and again right before this evaluator's turn on the
    ///     shared parse. Called often and off the UI thread: answer from memory.
    /// </summary>
    bool Wants(string path);

    /// <summary>Reads the parsed demo. Runs on a queue thread; never touch the UI from here.</summary>
    void Evaluate(string path, ParsedDemo parsed);

    /// <summary>The parse of <paramref name="path" /> failed. The next check may ask again.</summary>
    void OnFailed(string path)
    {
    }

    /// <summary>How urgent <paramref name="path" /> is.</summary>
    JobPriority PriorityFor(string path) => JobPriority.Background;

    /// <summary>False lets the parse skip player inputs when nothing else needs them.</summary>
    bool ReadsUserCommands => true;

    /// <summary>Order among background demos; lower runs first.</summary>
    long OrderHint(string path) => 0;
}
